using Apache.Ignite.Core.Events;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AspNetCore.IgniteServer.Listeners;

/// <summary>
///     Tracks the caches this node is rebalancing (receiving partitions), from the rebalance started/stopped events.
///     It is registered in the configuration, so it also sees rebalancing that starts while the node joins.
/// </summary>
internal sealed class RebalanceTracker(ILogger logger, TimeSpan maxWait, TimeProvider? timeProvider = null)
    : IEventListener<CacheRebalancingEvent>
{
    /// <summary>
    ///     How long the running node, once in the baseline, must not rebalance for its catch-up after the start to be
    ///     over.
    /// </summary>
    internal static readonly TimeSpan CatchUpSettleTime = TimeSpan.FromSeconds(10);

    private readonly Lock catchUpLock = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> rebalancing = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private volatile bool caughtUp;
    private DateTimeOffset? inBaselineSince;
    private DateTimeOffset lastActivity;
    private DateTimeOffset? runningSince;

    /// <summary>The caches being rebalanced now.</summary>
    public IReadOnlyCollection<string> RebalancingCaches => rebalancing.Keys.ToList();

    public bool Invoke(CacheRebalancingEvent evt)
    {
        if (evt.Type == EventType.CacheRebalanceStarted)
        {
            OnStarted(evt.CacheName ?? string.Empty);
        }
        else if (evt.Type == EventType.CacheRebalanceStopped)
        {
            OnStopped(evt.CacheName ?? string.Empty);
        }

        return true;
    }

    internal void OnStarted(string cacheName)
    {
        lock (catchUpLock)
        {
            var now = timeProvider.GetUtcNow();
            UpdateCaughtUp(now);
            lastActivity = now;
            // Only the first start is recorded. Ignite restarts a rebalance on every topology change, and that must
            // not extend the wait.
            if (rebalancing.TryAdd(cacheName, now))
            {
                logger.Information("Rebalancing of cache {CacheName} started.", cacheName);
            }
        }
    }

    internal void OnStopped(string cacheName)
    {
        lock (catchUpLock)
        {
            var now = timeProvider.GetUtcNow();
            lastActivity = now;
            if (rebalancing.TryRemove(cacheName, out var started))
            {
                logger.Information(
                    "Rebalancing of cache {CacheName} finished in {Duration}.",
                    cacheName,
                    now - started
                );
            }
        }
    }

    /// <summary>The node is running and its cluster is active: from now on, its catch-up can end.</summary>
    public void OnRunning()
    {
        lock (catchUpLock)
        {
            runningSince = timeProvider.GetUtcNow();
        }
    }

    /// <summary>
    ///     True while the node catches up after its start, that is, until it holds its data.
    ///     <para>
    ///         A joining node gets partitions only once the baseline auto-adjust has added it to the baseline, so it is
    ///         catching up while it is not in the baseline, for at most <c>maxWait</c> after it runs.
    ///     </para>
    ///     <para>
    ///         In the baseline, it is catching up while a cache rebalances. A rebalance that started more than
    ///         <c>maxWait</c> ago is ignored: one that never reports its end must not keep the node unready forever.
    ///     </para>
    ///     <para>
    ///         The catch-up ends for good once the node, running and in the baseline, has not rebalanced for
    ///         <see cref="CatchUpSettleTime" />. Rebalancing after that (e.g., another node left) does not count, because
    ///         every node of the cluster rebalances then.
    ///     </para>
    /// </summary>
    /// <param name="isInBaseline">Whether the node is in the baseline now (read only while it catches up).</param>
    public bool IsCatchingUp(Func<bool> isInBaseline)
    {
        if (caughtUp)
        {
            return false;
        }

        // Called outside the lock because it calls into Ignite, whose event thread takes the same lock.
        var inBaseline = isInBaseline();
        lock (catchUpLock)
        {
            var now = timeProvider.GetUtcNow();
            if (runningSince is not { } running)
            {
                return IsRebalancing(now);
            }

            if (!inBaseline)
            {
                return now - running < maxWait;
            }

            inBaselineSince ??= now;
            UpdateCaughtUp(now);
            return !caughtUp && IsRebalancing(now);
        }
    }

    /// <summary>Forgets the previous run (the node restarts).</summary>
    public void Reset()
    {
        lock (catchUpLock)
        {
            rebalancing.Clear();
            caughtUp = false;
            inBaselineSince = null;
            lastActivity = default;
            runningSince = null;
        }
    }

    private void UpdateCaughtUp(DateTimeOffset now)
    {
        if (caughtUp || runningSince is not { } running || inBaselineSince is not { } inBaseline || IsRebalancing(now))
        {
            return;
        }

        DateTimeOffset[] quietSince = [running, inBaseline, lastActivity,];
        if (now - quietSince.Max() >= CatchUpSettleTime)
        {
            caughtUp = true;
        }
    }

    private bool IsRebalancing(DateTimeOffset now) => rebalancing.Values.Any(started => now - started < maxWait);
}
