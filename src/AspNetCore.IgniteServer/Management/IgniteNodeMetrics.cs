using Apache.Ignite.Core;
using Apache.Ignite.Core.Cluster;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;

namespace AspNetCore.IgniteServer.Management;

/// <summary>
///     Publishes node, cluster and data region metrics as <see cref="System.Diagnostics.Metrics" /> instruments (exported
///     by prometheus-net on /metrics). Values are read from the running node on each collection; while the node is not
///     running, nothing is reported.
/// </summary>
internal sealed class IgniteNodeMetrics : IDisposable
{
    // prometheus-net prefixes the instruments with the meter name: ignite_node_ready, ignite_cluster_active, ...
    public const string MeterName = "Ignite";

    private readonly Meter meter = new(MeterName);
    private readonly IgniteNodeState nodeState;

    public IgniteNodeMetrics(IgniteNodeState nodeState)
    {
        this.nodeState = nodeState;
        meter.CreateObservableGauge("node_ready", () => nodeState.IsReady ? 1 : 0, description: "1 when the node is running and its cluster is active.");
        Cluster("cluster_active", cluster => cluster.IsActive() ? 1 : 0, "1 when the cluster is active.");
        Cluster("cluster_server_nodes", cluster => cluster.ForServers().GetNodes().Count, "Server nodes in the topology.");
        Cluster("cluster_baseline_nodes", cluster => IgniteServerRunner.GetBaselineTopology(cluster)?.Count ?? 0, "Nodes of the baseline topology.");

        LocalNode("node_heap_used_bytes", metrics => metrics.HeapMemoryUsed, "bytes", "JVM heap in use.");
        LocalNode("node_heap_committed_bytes", metrics => metrics.HeapMemoryCommitted, "bytes", "JVM heap committed.");
        LocalNode("node_heap_max_bytes", metrics => metrics.HeapMemoryMaximum, "bytes", "JVM heap maximum.");
        LocalNode("node_non_heap_used_bytes", metrics => metrics.NonHeapMemoryUsed, "bytes", "JVM non-heap memory in use.");
        LocalNodeDouble("node_cpu_load_ratio", metrics => metrics.CurrentCpuLoad, "CPU load of the node (0 to 1).");
        LocalNodeDouble("node_gc_cpu_load_ratio", metrics => metrics.CurrentGcCpuLoad, "CPU load of the JVM garbage collector (0 to 1).");
        LocalNode("node_threads", metrics => metrics.CurrentThreadCount, null, "JVM threads.");
        LocalNode("node_active_jobs", metrics => metrics.CurrentActiveJobs, null, "Compute jobs running.");
        LocalNode("node_waiting_jobs", metrics => metrics.CurrentWaitingJobs, null, "Compute jobs waiting.");
        LocalNode("node_outbound_messages_queue", metrics => metrics.OutboundMessagesQueueSize, null, "Messages waiting to be sent.");
        meter.CreateObservableCounter("node_sent_bytes", () => FromLocalNode(metrics => metrics.SentBytesCount), "bytes", "Bytes sent to other nodes.");
        meter.CreateObservableCounter("node_received_bytes", () => FromLocalNode(metrics => metrics.ReceivedBytesCount), "bytes", "Bytes received from other nodes.");

        // data region metrics need ENABLE_OFF_HEAP_METRICS (on by default)
        DataRegion("data_region_off_heap_size_bytes", region => region.OffHeapSize, "bytes", "Off-heap memory of the region.");
        DataRegion("data_region_off_heap_used_bytes", region => region.OffheapUsedSize, "bytes", "Off-heap memory used by the region.");
        DataRegion("data_region_physical_memory_bytes", region => region.PhysicalMemorySize, "bytes", "Data in RAM.");
        DataRegion("data_region_total_allocated_bytes", region => region.TotalAllocatedSize, "bytes", "Allocated size (RAM and, with persistence, disk).");
        DataRegion("data_region_checkpoint_buffer_used_bytes", region => region.UsedCheckpointBufferSize, "bytes", "Checkpoint buffer in use.");
        DataRegion("data_region_dirty_pages", region => region.DirtyPages, null, "Pages changed since the last checkpoint.");
        DataRegionDouble("data_region_page_fill_factor_ratio", region => region.PageFillFactor, "Fill factor of the allocated pages (0 to 1).");
        DataRegionDouble("data_region_eviction_rate", region => region.EvictionRate, "Pages evicted per second.");
    }

    public void Dispose() => meter.Dispose();

    private void Cluster(string name, Func<ICluster, int> read, string description) =>
        meter.CreateObservableGauge(name, () => FromNode(node => read(node.GetCluster())), description: description);

    private void LocalNode(string name, Func<IClusterMetrics, long> read, string? unit, string description) =>
        meter.CreateObservableGauge(name, () => FromLocalNode(read), unit, description);

    private void LocalNodeDouble(string name, Func<IClusterMetrics, double> read, string description) =>
        meter.CreateObservableGauge(name, () => FromLocalNode(read), description: description);

    private void DataRegion(string name, Func<IDataRegionMetrics, long> read, string? unit, string description) =>
        meter.CreateObservableGauge(name, () => FromDataRegions(read), unit, description);

    private void DataRegionDouble(string name, Func<IDataRegionMetrics, double> read, string description) =>
        meter.CreateObservableGauge(name, () => FromDataRegions(read), description: description);

    private IEnumerable<Measurement<T>> FromNode<T>(Func<IIgnite, T> read) where T : struct =>
        Read<T>(() => nodeState.Node is { } node ? [new Measurement<T>(read(node)),] : []);

    private IEnumerable<Measurement<T>> FromLocalNode<T>(Func<IClusterMetrics, T> read) where T : struct =>
        Read<T>(() => nodeState.Node is { } node ? [new Measurement<T>(read(node.GetCluster().GetLocalNode().GetMetrics())),] : []);

    private IEnumerable<Measurement<T>> FromDataRegions<T>(Func<IDataRegionMetrics, T> read) where T : struct =>
        Read<T>(() => nodeState.Node is { } node
            ? node.GetDataRegionMetrics()
                .Select(region => new Measurement<T>(read(region), new KeyValuePair<string, object?>("region", region.Name)))
                .ToList()
            : []);

    // the node may stop while it is read: report nothing rather than failing the whole scrape
    private static IEnumerable<Measurement<T>> Read<T>(Func<IReadOnlyCollection<Measurement<T>>> read) where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return [];
        }
    }
}
