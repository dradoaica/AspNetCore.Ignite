using Apache.Ignite.Core;
using Apache.Ignite.Core.Cache;
using Apache.Ignite.Core.Cluster;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;

namespace AspNetCore.IgniteServer.Management;

/// <summary>
///     Publishes node, cluster, data region and cache metrics as <see cref="System.Diagnostics.Metrics" />
///     instruments (exported by prometheus-net on /metrics). One snapshot of the node is taken per collection (at most
///     once per second), not one read per instrument; while the node is not running, only readiness is reported, and while
///     its cluster is inactive, no cache metrics.
/// </summary>
internal sealed class IgniteNodeMetrics : IDisposable
{
    // prometheus-net prefixes the instruments with the meter name: ignite_node_ready, ignite_cluster_active, ...
    public const string MeterName = "Ignite";

    private static readonly TimeSpan SnapshotMaxAge = TimeSpan.FromSeconds(1);

    private readonly ILogger? logger;
    private readonly Meter meter = new(MeterName);
    private readonly IgniteNodeState nodeState;

    private readonly bool perCache;
    private readonly Lock snapshotLock = new();
    private readonly TimeProvider timeProvider;
    private NodeSnapshot? snapshot;
    private DateTimeOffset snapshotTime;

    public IgniteNodeMetrics(
        IgniteNodeState nodeState,
        bool perCache = true,
        TimeProvider? timeProvider = null,
        ILogger? logger = null
    )
    {
        this.nodeState = nodeState;
        this.logger = logger;
        this.perCache = perCache;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        meter.CreateObservableGauge(
            "node_ready",
            () => nodeState.IsReady ? 1 : 0,
            description: "1 when the node is running, its cluster is active and it has caught up after its start."
        );
        meter.CreateObservableGauge(
            "node_rebalancing_caches",
            () => nodeState.RebalancingCaches.Count,
            description: "Caches this node is rebalancing (receiving partitions of)."
        );

        Gauge("cluster_active", s => s.ClusterActive ? 1 : 0, null, "1 when the cluster is active.");
        Gauge("cluster_server_nodes", s => s.ServerNodes, null, "Server nodes in the topology.");
        Gauge("cluster_baseline_nodes", s => s.BaselineNodes, null, "Nodes of the baseline topology.");
        // Not reported while the cluster is inactive, because the caches cannot be read then.
        meter.CreateObservableGauge(
            "cluster_lost_partitions",
            () => Read<int>(s => s.ClusterActive ? [new Measurement<int>(s.LostPartitions.Values.Sum()),] : []),
            description:
            "Lost partitions of all caches (their data is gone until reset, or until the nodes that own them return)."
        );

        Gauge("node_heap_used_bytes", s => s.Local.HeapMemoryUsed, "bytes", "JVM heap in use.");
        Gauge("node_heap_committed_bytes", s => s.Local.HeapMemoryCommitted, "bytes", "JVM heap committed.");
        Gauge("node_heap_max_bytes", s => s.Local.HeapMemoryMaximum, "bytes", "JVM heap maximum.");
        Gauge("node_non_heap_used_bytes", s => s.Local.NonHeapMemoryUsed, "bytes", "JVM non-heap memory in use.");
        Gauge("node_cpu_load_ratio", s => s.Local.CurrentCpuLoad, null, "CPU load of the node (0 to 1).");
        Gauge(
            "node_gc_cpu_load_ratio",
            s => s.Local.CurrentGcCpuLoad,
            null,
            "CPU load of the JVM garbage collector (0 to 1)."
        );
        Gauge("node_threads", s => s.Local.CurrentThreadCount, null, "JVM threads.");
        Gauge("node_active_jobs", s => s.Local.CurrentActiveJobs, null, "Compute jobs running.");
        Gauge("node_waiting_jobs", s => s.Local.CurrentWaitingJobs, null, "Compute jobs waiting.");
        Gauge(
            "node_outbound_messages_queue",
            s => s.Local.OutboundMessagesQueueSize,
            null,
            "Messages waiting to be sent."
        );
        meter.CreateObservableCounter(
            "node_sent_bytes",
            () => Measure(s => s.Local.SentBytesCount),
            "bytes",
            "Bytes sent to other nodes."
        );
        meter.CreateObservableCounter(
            "node_received_bytes",
            () => Measure(s => s.Local.ReceivedBytesCount),
            "bytes",
            "Bytes received from other nodes."
        );

        // data region metrics need ENABLE_OFF_HEAP_METRICS (on by default)
        Region(
            "data_region_off_heap_size_bytes",
            region => region.OffHeapSize,
            "bytes",
            "Off-heap memory of the region."
        );
        Region(
            "data_region_off_heap_used_bytes",
            region => region.OffheapUsedSize,
            "bytes",
            "Off-heap memory used by the region."
        );
        Region("data_region_physical_memory_bytes", region => region.PhysicalMemorySize, "bytes", "Data in RAM.");
        Region(
            "data_region_total_allocated_bytes",
            region => region.TotalAllocatedSize,
            "bytes",
            "Allocated size (RAM and, with persistence, disk)."
        );
        Region(
            "data_region_checkpoint_buffer_used_bytes",
            region => region.UsedCheckpointBufferSize,
            "bytes",
            "Checkpoint buffer in use."
        );
        Region(
            "data_region_dirty_pages",
            region => region.DirtyPages,
            null,
            "Pages changed since the last checkpoint."
        );
        Region(
            "data_region_page_fill_factor_ratio",
            region => region.PageFillFactor,
            null,
            "Fill factor of the allocated pages (0 to 1)."
        );
        Region("data_region_eviction_rate", region => region.EvictionRate, null, "Pages evicted per second.");

        if (!perCache)
        {
            return;
        }

        meter.CreateObservableGauge(
            "cache_local_entries",
            () => Read(s => s.CacheEntries.Select(entry => new Measurement<long>(entry.Value, Cache(entry.Key)))),
            description: "Entries of the cache on this node (primary and backup)."
        );
        meter.CreateObservableGauge(
            "cache_lost_partitions",
            () => Read(s => s.LostPartitions.Select(entry => new Measurement<int>(entry.Value, Cache(entry.Key)))),
            description: "Lost partitions of the cache."
        );
        meter.CreateObservableGauge(
            "cache_rebalancing",
            () => Read(s =>
                {
                    var rebalancing = nodeState.RebalancingCaches;
                    return s.CacheEntries.Keys.Select(cache => new Measurement<int>(
                            rebalancing.Contains(cache) ? 1 : 0,
                            Cache(cache)
                        )
                    );
                }
            ),
            description: "1 while this node rebalances the cache."
        );
    }

    public void Dispose() => meter.Dispose();

    private static KeyValuePair<string, object?> Cache(string name) => new("cache", name);

    private void Gauge<T>(string name, Func<NodeSnapshot, T> read, string? unit, string description)
        where T : struct => meter.CreateObservableGauge(name, () => Measure(read), unit, description);

    private void Region<T>(string name, Func<IDataRegionMetrics, T> read, string? unit, string description)
        where T : struct => meter.CreateObservableGauge(
        name,
        () => Read(s => s.Regions.Select(region => new Measurement<T>(
                    read(region),
                    new KeyValuePair<string, object?>("region", region.Name)
                )
            )
        ),
        unit,
        description
    );

    private List<Measurement<T>> Measure<T>(Func<NodeSnapshot, T> read)
        where T : struct => Read<T>(s => [new Measurement<T>(read(s)),]);

    private List<Measurement<T>> Read<T>(Func<NodeSnapshot, IEnumerable<Measurement<T>>> read)
        where T : struct
    {
        // The node may stop while it is read. Reporting nothing is better than failing the whole scrape.
        try
        {
            return GetSnapshot() is { } current ? read(current).ToList() : [];
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "Ignite metrics were not read.");
            return [];
        }
    }

    private NodeSnapshot? GetSnapshot()
    {
        lock (snapshotLock)
        {
            var now = timeProvider.GetUtcNow();
            if (snapshot == null ||
                now - snapshotTime >= SnapshotMaxAge ||
                !ReferenceEquals(snapshot.Node, nodeState.Node))
            {
                snapshot = nodeState.Node is { } node ? NodeSnapshot.Take(node, perCache, logger) : null;
                snapshotTime = now;
            }

            return snapshot;
        }
    }

    internal sealed record NodeSnapshot(
        IIgnite Node,
        bool ClusterActive,
        int ServerNodes,
        int BaselineNodes,
        IClusterMetrics Local,
        IReadOnlyList<IDataRegionMetrics> Regions,
        IReadOnlyDictionary<string, long> CacheEntries,
        IReadOnlyDictionary<string, int> LostPartitions
    )
    {
        public static NodeSnapshot Take(IIgnite node, bool perCache, ILogger? logger = null)
        {
            var cluster = node.GetCluster();
            var clusterActive = cluster.IsActive();
            Dictionary<string, long> cacheEntries = [];
            Dictionary<string, int> lostPartitions = [];
            // Lost partitions are read for every cache, because their total is reported even without
            // METRICS_PER_CACHE. Ignite refuses to read the caches of an inactive cluster (e.g., one waiting for
            // ACTIVATION_MIN_SERVER_NODES); its other metrics are still reported.
            foreach (var cacheName in clusterActive ? node.GetCacheNames() : [])
            {
                try
                {
                    var cache = node.GetCache<object, object>(cacheName);
                    lostPartitions[cacheName] = cache.GetLostPartitions().Count;
                    if (perCache)
                    {
                        cacheEntries[cacheName] = cache.GetLocalSizeLong(CachePeekMode.Primary, CachePeekMode.Backup);
                    }
                }
                catch (Exception ex)
                {
                    // E.g., the cache was destroyed while it was read (such as the temporary cache of a password
                    // change). The other caches are still reported.
                    logger?.Debug(ex, "The metrics of cache {CacheName} were not read.", cacheName);
                    lostPartitions.Remove(cacheName);
                }
            }

            return new NodeSnapshot(
                node,
                clusterActive,
                cluster.ForServers().GetNodes().Count,
                IgniteServerRunner.GetBaselineTopology(cluster)?.Count ?? 0,
                // Ignite.NET reads the metrics once into this object (one call for all node metrics)
                cluster.GetLocalNode().GetMetrics(),
                node.GetDataRegionMetrics().ToList(),
                cacheEntries,
                lostPartitions
            );
        }
    }
}
