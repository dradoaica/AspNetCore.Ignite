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
///     Logs partition data loss and, when <c>autoReset</c> is set, resets the lost partitions so that the caches become
///     usable again.
///     <para>
///         Ignite raises one event per lost partition. The events of one second form a batch: each cache is logged once
///         per batch, and the reset runs once at the end of the batch, off the Ignite thread that raised the events.
///     </para>
///     <para>
///         Every node remembers the caches that lost partitions, but only the oldest server node resets them. The reset
///         is tried again whenever a node joins or leaves: a node that owns the partitions may be back, or this node may
///         have become the oldest one.
///     </para>
///     <para>
///         Ignite resets partitions only while a baseline node that owns them is online. A persistent cluster therefore
///         waits for its nodes to return with their data. The departed nodes of an in-memory cluster took their data with
///         them, so they are dropped from the baseline first; that data is NOT recovered.
///     </para>
/// </summary>
internal sealed class LostPartitionsListener : IEventListener<CacheRebalancingEvent>, IEventListener<DiscoveryEvent>,
    IDisposable
{
    private const string ResetWithReturnedData =
        "The {Partitions} lost partitions of cache {CacheName} were reset, with the data of the nodes that returned.";

    private const string ResetWithoutData =
        "The {Partitions} lost partitions of cache {CacheName} were reset; their data is gone.";

    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    private readonly bool autoReset;
    private readonly IIgnite ignite;
    private readonly ILogger logger;

    // the caches that lost partitions, until they are reset
    private readonly ConcurrentDictionary<string, byte> lost = new(StringComparer.Ordinal);
    private readonly bool persistent;

    // the caches whose loss was logged in the current batch of losses
    private readonly ConcurrentDictionary<string, byte> reported = new(StringComparer.Ordinal);

    // Timer callbacks can overlap: a reset that takes longer than the debounce would otherwise run twice at once.
    private readonly Lock resetLock = new();
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
        // Ignite raises one event per lost partition; only the first one of a cache in this batch is logged. That
        // also holds on a node that still remembers an earlier loss of the cache which another node has reset.
        lost.TryAdd(cacheName, 0);
        if (reported.TryAdd(cacheName, 0))
        {
            logger.Error(
                "Partition data lost: CacheName: {CacheName}; DiscoveryEventName: {DiscoveryEventName}; " +
                "DiscoveryNodeAddresses: {DiscoveryNodeAddresses}; DiscoveryHostNames: {DiscoveryHostNames}",
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
        // A node that owns lost partitions may be back, or the oldest node (which resets them) may have left.
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
        if (!resetLock.TryEnter())
        {
            // The reset in progress may have missed the losses of this batch: they are handled after it.
            ScheduleBatchEnd();
            return;
        }

        try
        {
            reported.Clear();
            if (autoReset)
            {
                Reset();
            }
        }
        finally
        {
            resetLock.Exit();
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
            var cacheNames = ignite.GetCacheNames().ToHashSet(StringComparer.Ordinal);
            Dictionary<string, int> partitions = [];
            foreach (var cacheName in lost.Keys)
            {
                // A failure to count (e.g., during a topology change) propagates: the record stays for the next try.
                var count = cacheNames.Contains(cacheName)
                    ? ignite.GetCache<object, object>(cacheName).GetLostPartitions().Count : 0;
                if (count == 0)
                {
                    // Nothing is lost any more: the oldest node reset the cache, or the cache was destroyed.
                    lost.TryRemove(cacheName, out _);
                }
                else
                {
                    partitions[cacheName] = count;
                }
            }

            var cluster = ignite.GetCluster();
            // Only the oldest node resets. The others keep their record, in case it leaves before it has done so.
            if (partitions.Count == 0 || !cluster.ForServers().ForOldest().GetNodes().Any(node => node.IsLocal))
            {
                return;
            }

            if (!persistent)
            {
                DropDepartedBaselineNodes(cluster);
            }

            // Removed before the reset, so that a loss reported in the meantime stays recorded for the next one.
            foreach (var cacheName in partitions.Keys)
            {
                lost.TryRemove(cacheName, out _);
            }

            try
            {
                ignite.ResetLostPartitions(partitions.Keys.ToArray());
            }
            catch
            {
                foreach (var cacheName in partitions.Keys)
                {
                    lost.TryAdd(cacheName, 0);
                }

                throw;
            }

            foreach (var (cacheName, count) in partitions)
            {
                logger.Warning(persistent ? ResetWithReturnedData : ResetWithoutData, count, cacheName);
            }
        }
        catch (ClusterTopologyException ex)
        {
            logger.Warning(
                "The lost partitions of caches {CacheNames} are reset once a node that owns them returns (or is " +
                "removed from the baseline): {Reason}",
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

        // Ignite accepts a manual baseline change only while auto-adjust is off.
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
