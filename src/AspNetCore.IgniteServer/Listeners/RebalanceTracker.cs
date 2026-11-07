using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Apache.Ignite.Core.Events;
using Serilog;

namespace AspNetCore.IgniteServer.Listeners;

/// <summary>
///     Tracks the caches this node is rebalancing (receiving partitions), from the rebalance started/stopped events. It is
///     registered in the configuration, so it also sees rebalancing that starts while the node joins.
/// </summary>
internal sealed class RebalanceTracker(ILogger logger, TimeProvider? timeProvider = null)
    : IEventListener<CacheRebalancingEvent>
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> rebalancing = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

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
        // the first start counts: Ignite restarts a rebalance on every topology change, which must not extend the wait
        if (rebalancing.TryAdd(cacheName, timeProvider.GetUtcNow()))
            logger.Information("Rebalancing of cache {CacheName} started.", cacheName);
    }

    internal void OnStopped(string cacheName)
    {
        if (rebalancing.TryRemove(cacheName, out var started))
            logger.Information(
                "Rebalancing of cache {CacheName} finished in {Duration}.",
                cacheName,
                timeProvider.GetUtcNow() - started
            );
    }

    /// <summary>
    ///     True while a cache rebalances that started less than <paramref name="maxWait" /> ago (a rebalance that never
    ///     reports its end must not keep the node unready forever, but it does not end the wait for the others).
    /// </summary>
    public bool IsRebalancing(TimeSpan maxWait)
    {
        var now = timeProvider.GetUtcNow();
        return rebalancing.Values.Any(started => now - started < maxWait);
    }

    /// <summary>Forgets the previous run (the node restarts).</summary>
    public void Reset()
    {
        rebalancing.Clear();
    }
}