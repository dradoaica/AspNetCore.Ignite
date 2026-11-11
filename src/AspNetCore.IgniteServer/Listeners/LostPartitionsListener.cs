using Apache.Ignite.Core;
using Apache.Ignite.Core.Cluster;
using Apache.Ignite.Core.Events;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AspNetCore.IgniteServer.Listeners;

/// <summary>
///     Logs partition data loss (once per cache) and, when <c>autoReset</c> is set, resets the lost partitions so the
///     caches become usable again. Ignite resets partitions only while a baseline node that owns them is online: a
///     persistent cluster waits for its nodes to return, with their data; an in-memory cluster, whose departed nodes took
///     their data with them, first drops them from the baseline (the lost data is NOT recovered). Every node reports the
///     loss (once per cache and batch of losses) and remembers it until the cache has no lost partitions any more, the
///     oldest server node resets: off the Ignite thread that reports it, once per batch of losses, and again whenever a
///     node joins or leaves (a node that owns them may be back, or this node may have become the oldest one).
/// </summary>
internal sealed class LostPartitionsListener : IEventListener<CacheRebalancingEvent>, IEventListener<DiscoveryEvent>,
    IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    private readonly bool autoReset;
    private readonly IIgnite ignite;
    private readonly ILogger logger;

    // the lost partitions of each cache, until they are reset
    private readonly ConcurrentDictionary<string, int> lost = new(StringComparer.Ordinal);
    private readonly bool persistent;

    // the caches whose loss was logged in the current batch of losses
    private readonly ConcurrentDictionary<string, byte> reported = new(StringComparer.Ordinal);
    private readonly ITimer timer;
    private int disposed;

    public LostPartitionsListener(
        IIgnite ignite,
        ILogger logger,
        bool autoReset,
        bool persistent,
        TimeProvider? timeProvider = null
    )
    {
        this.ignite = ignite;
        this.logger = logger;
        this.autoReset = autoReset;
        this.persistent = persistent;
        timer = (timeProvider ?? TimeProvider.System).CreateTimer(
            _ => OnBatchEnded(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            timer.Dispose();
        }
    }

    public bool Invoke(CacheRebalancingEvent evt)
    {
        if (evt.Type == EventType.CacheRebalancePartDataLost)
        {
            OnPartitionLost(
                evt.CacheName ?? string.Empty,
                evt.DiscoveryEventName,
                evt.DiscoveryNode.Addresses,
                evt.DiscoveryNode.HostNames
            );
        }

        return true;
    }

    public bool Invoke(DiscoveryEvent evt)
    {
        OnTopologyChanged();
        return true;
    }

    internal void OnPartitionLost(
        string cacheName,
        string? discoveryEventName,
        IEnumerable<string> addresses,
        IEnumerable<string> hostNames
    )
    {
        // one event per lost partition: the first one of the cache in this batch is logged (also on the nodes that
        // still remember an earlier loss of the cache, reset by another node)
        lost.AddOrUpdate(cacheName, 1, (_, count) => count + 1);
        if (reported.TryAdd(cacheName, 0))
        {
            logger.Error(
                "Partition data lost: CacheName: {CacheName}; DiscoveryEventName: {DiscoveryEventName}; DiscoveryNodeAddresses: {DiscoveryNodeAddresses}; DiscoveryHostNames: {DiscoveryHostNames}",
                cacheName,
                discoveryEventName,
                string.Join(",", addresses),
                string.Join(",", hostNames)
            );
        }

        ScheduleBatchEnd();
    }

    internal void OnTopologyChanged()
    {
        // a node that owns lost partitions may be back, or the oldest node, which resets them, left
        if (autoReset && !lost.IsEmpty)
        {
            ScheduleBatchEnd();
        }
    }

    private void ScheduleBatchEnd()
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnBatchEnded()
    {
        reported.Clear();
        if (autoReset)
        {
            Reset();
        }
    }

    private void Reset()
    {
        if (lost.IsEmpty || Volatile.Read(ref disposed) == 1)
        {
            return;
        }

        try
        {
            // reset by the oldest node (on the others), or destroyed
            foreach (var cacheName in lost.Keys.Where(cacheName => !HasLostPartitions(cacheName)))
            {
                lost.TryRemove(cacheName, out _);
            }

            var caches = lost.Keys.ToArray();
            var cluster = ignite.GetCluster();
            // the other nodes keep the losses, should the oldest node leave before it reset them
            if (caches.Length == 0 || !cluster.ForServers().ForOldest().GetNodes().Any(node => node.IsLocal))
            {
                return;
            }

            if (!persistent)
            {
                DropDepartedBaselineNodes(cluster);
            }

            ignite.ResetLostPartitions(caches);
            foreach (var cacheName in caches)
            {
                if (lost.TryRemove(cacheName, out var partitions))
                {
                    logger.Warning(
                        persistent
                            ? "The {Partitions} lost partitions of cache {CacheName} were reset, with the data of the nodes that returned."
                            : "The {Partitions} lost partitions of cache {CacheName} were reset; their data is gone.",
                        partitions,
                        cacheName
                    );
                }
            }
        }
        catch (ClusterTopologyException ex)
        {
            logger.Warning(
                "The lost partitions of caches {CacheNames} are reset once a node that owns them returns (or is removed from the baseline): {Reason}",
                string.Join(", ", lost.Keys),
                ex.Message
            );
        }
        catch (Exception ex)
        {
            logger.Warning(
                ex,
                "Failed to reset the lost partitions of caches {CacheNames}.",
                string.Join(", ", lost.Keys)
            );
        }
    }

    private bool HasLostPartitions(string cacheName)
    {
        try
        {
            return ignite.GetCache<object, object>(cacheName).GetLostPartitions().Count > 0;
        }
        catch (Exception)
        {
            // destroyed meanwhile
            return false;
        }
    }

    /// <summary>
    ///     The baseline follows the topology now rather than after the auto-adjust timeout, which does not change it
    ///     while partitions are lost: the departed in-memory nodes hold no data to come back with.
    /// </summary>
    private void DropDepartedBaselineNodes(ICluster cluster)
    {
        var online = cluster.ForServers().GetNodes().Select(node => node.ConsistentId).ToHashSet();
        if (IgniteServerRunner.GetBaselineTopology(cluster) is not { } baseline ||
            baseline.All(node => online.Contains(node.ConsistentId)))
        {
            return;
        }

        // Ignite changes the baseline by hand only without auto-adjust
        cluster.SetBaselineAutoAdjustEnabledFlag(false);
        try
        {
            cluster.SetBaselineTopology(cluster.TopologyVersion);
        }
        finally
        {
            cluster.SetBaselineAutoAdjustEnabledFlag(true);
        }

        logger.Warning("The nodes that left were removed from the baseline, so that the lost partitions can be reset.");
    }
}
