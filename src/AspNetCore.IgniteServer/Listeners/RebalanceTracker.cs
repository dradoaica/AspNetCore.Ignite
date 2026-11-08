using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Apache.Ignite.Core.Events;
using Serilog;

namespace AspNetCore.IgniteServer.Listeners;

/// <summary>
///     Tracks the caches this node is rebalancing (receiving partitions), from the rebalance started/stopped events. It is
///     registered in the configuration, so it also sees rebalancing that starts while the node joins.
/// </summary>
internal sealed class RebalanceTracker(ILogger logger, TimeSpan maxWait, TimeProvider? timeProvider = null)
    : IEventListener<CacheRebalancingEvent>
{
    /// <summary>
    ///     How long the running node, once in the baseline, must not rebalance for its catch-up after the start to be over.
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
            OnStarted(evt.CacheName ?? string.Empty);
        else if (evt.Type == EventType.CacheRebalanceStopped) OnStopped(evt.CacheName ?? string.Empty);

        return true;
    }

    internal void OnStarted(string cacheName)
    {
        lock (catchUpLock)
        {
            var now = timeProvider.GetUtcNow();
            UpdateCaughtUp(now);
            lastActivity = now;
            // the first start counts: Ignite restarts a rebalance on every topology change, which must not extend the wait
            if (rebalancing.TryAdd(cacheName, now))
                logger.Information("Rebalancing of cache {CacheName} started.", cacheName);
        }
    }

    internal void OnStopped(string cacheName)
    {
        lock (catchUpLock)
        {
            var now = timeProvider.GetUtcNow();
            lastActivity = now;
            if (rebalancing.TryRemove(cacheName, out var started))
                logger.Information("Rebalancing of cache {CacheName} finished in {Duration}.", cacheName, now - started);
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
    ///     True while the node catches up after its start, until it holds its data: while it is not in the baseline (a
    ///     joining node gets partitions only once the baseline auto-adjust adds it), at most <c>maxWait</c> after it runs,
    ///     and while a cache rebalances that started less than <c>maxWait</c> ago (a rebalance that never reports its end
    ///     must not keep the node unready forever, but it does not end the wait for the others). The catch-up ends once the
    ///     node, running and in the baseline, has not rebalanced for <see cref="CatchUpSettleTime" />; rebalancing after
    ///     that (e.g., another node left) does not count, as every node of the cluster rebalances then.
    /// </summary>
    /// <param name="isInBaseline">Whether the node is in the baseline now (read only while it catches up).</param>
    public bool IsCatchingUp(Func<bool> isInBaseline)
    {
        if (caughtUp) return false;

        // outside the lock, which the event listener takes: it calls into Ignite
        var inBaseline = isInBaseline();
        lock (catchUpLock)
        {
            var now = timeProvider.GetUtcNow();
            if (runningSince is not { } running) return IsRebalancing(now);

            if (!inBaseline) return now - running < maxWait;

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
        if (caughtUp || runningSince is not { } running || inBaselineSince is not { } inBaseline ||
            IsRebalancing(now)) return;

        DateTimeOffset[] quietSince = [running, inBaseline, lastActivity];
        if (now - quietSince.Max() >= CatchUpSettleTime) caughtUp = true;
    }

    private bool IsRebalancing(DateTimeOffset now)
    {
        return rebalancing.Values.Any(started => now - started < maxWait);
    }
}
