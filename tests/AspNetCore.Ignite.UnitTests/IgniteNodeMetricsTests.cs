using Apache.Ignite.Core;
using Apache.Ignite.Core.Cache;
using Apache.Ignite.Core.Cluster;
using AspNetCore.IgniteServer;
using AspNetCore.IgniteServer.Management;
using System.Diagnostics.Metrics;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class IgniteNodeMetricsTests
{
    [Fact]
    public void WithoutRunningNode_OnlyReadinessAndRebalancingAreReported()
    {
        using IgniteNodeMetrics metrics = new(new IgniteNodeState());
        Dictionary<string, List<double>> measurements = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == IgniteNodeMetrics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((instrument, value, _, _) => Record(instrument, value));
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => Record(instrument, value));
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => Record(instrument, value));
        listener.Start();

        listener.RecordObservableInstruments();

        Assert.Equal([0d,], measurements["node_ready"]);
        Assert.Equal([0d,], measurements["node_rebalancing_caches"]);
        Assert.Equal(2, measurements.Count);

        void Record(Instrument instrument, double value)
        {
            if (!measurements.TryGetValue(instrument.Name, out var values))
            {
                measurements[instrument.Name] = values = [];
            }

            values.Add(value);
        }
    }

    [Fact]
    public void PerCacheMetrics_CanBeTurnedOff()
    {
        using IgniteNodeMetrics metrics = new(new IgniteNodeState(), false);
        List<string> instruments = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == IgniteNodeMetrics.MeterName)
            {
                instruments.Add(instrument.Name);
            }
        };
        listener.Start();

        Assert.DoesNotContain(instruments, name => name.StartsWith("cache_", StringComparison.Ordinal));
        Assert.Contains("node_ready", instruments);
        // data loss is reported without the per-cache series too
        Assert.Contains("cluster_lost_partitions", instruments);
    }

    [Fact]
    public void InactiveCluster_IsReportedWithoutItsCaches()
    {
        // Ignite refuses to read the caches of an inactive cluster
        var snapshot = IgniteNodeMetrics.NodeSnapshot.Take(
            Node(false, () => throw new InvalidOperationException("The cluster is inactive."), _ => null),
            true
        );

        Assert.False(snapshot.ClusterActive);
        Assert.Equal(1, snapshot.ServerNodes);
        Assert.Empty(snapshot.CacheEntries);
        Assert.Empty(snapshot.LostPartitions);
    }

    [Fact]
    public void CacheDestroyedWhileRead_IsLeftOut()
    {
        var snapshot = IgniteNodeMetrics.NodeSnapshot.Take(
            Node(
                true,
                () => ["c", "gone",],
                cacheName => cacheName == "gone" ? throw new ArgumentException("The cache does not exist.")
                    : Fake.Of<ICache<object, object>>((name, _) => name switch
                        {
                            nameof(ICache<object, object>.GetLostPartitions) => new List<int> { 3, },
                            nameof(ICache<object, object>.GetLocalSizeLong) => 5L,
                            _ => throw new NotSupportedException(name),
                        }
                    )
            ),
            true
        );

        Assert.True(snapshot.ClusterActive);
        Assert.Equal(new Dictionary<string, long> { ["c"] = 5, }, snapshot.CacheEntries);
        Assert.Equal(new Dictionary<string, int> { ["c"] = 1, }, snapshot.LostPartitions);
    }

    private static IIgnite Node(
        bool active,
        Func<ICollection<string>> getCacheNames,
        Func<string, ICache<object, object>?> getCache
    )
    {
        var localNode = Fake.Of<IClusterNode>((name, _) => name switch
            {
                nameof(IClusterNode.GetMetrics) => Fake.Of<IClusterMetrics>((metric, _) =>
                    throw new NotSupportedException(metric)
                ),
                _ => throw new NotSupportedException(name),
            }
        );
        var servers = Fake.Of<IClusterGroup>((name, _) => name switch
            {
                nameof(IClusterGroup.GetNodes) => new List<IClusterNode> { localNode, },
                _ => throw new NotSupportedException(name),
            }
        );
        var cluster = Fake.Of<ICluster>((name, _) => name switch
            {
                nameof(ICluster.IsActive) => active,
                nameof(ICluster.ForServers) => servers,
                nameof(ICluster.GetBaselineTopology) => null,
                nameof(ICluster.GetLocalNode) => localNode,
                _ => throw new NotSupportedException(name),
            }
        );
        return Fake.Of<IIgnite>((name, args) => name switch
            {
                nameof(IIgnite.GetCluster) => cluster,
                nameof(IIgnite.GetCacheNames) => getCacheNames(),
                nameof(IIgnite.GetCache) => getCache((string)args[0]!),
                nameof(IIgnite.GetDataRegionMetrics) => new List<IDataRegionMetrics>(),
                _ => throw new NotSupportedException(name),
            }
        );
    }
}
