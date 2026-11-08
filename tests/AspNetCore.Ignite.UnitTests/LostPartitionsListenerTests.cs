using Apache.Ignite.Core;
using Apache.Ignite.Core.Cluster;
using AspNetCore.IgniteServer.Listeners;
using Microsoft.Extensions.Time.Testing;
using Serilog.Core;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class LostPartitionsListenerTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    [Fact]
    public void Losses_AreResetOnce_AfterTheDebounce()
    {
        FakeCluster cluster = new();
        FakeTimeProvider time = new();
        using LostPartitionsListener listener = new(cluster.Ignite, Logger.None, true, true, time);

        // one event per lost partition
        for (var i = 0; i < 3; i++)
        {
            Lose(listener, "c");
        }

        Assert.Empty(cluster.Calls);
        time.Advance(Debounce);

        Assert.Equal(["ResetLostPartitions c",], cluster.Calls);
    }

    [Fact]
    public void OnlyTheOldestServerNode_Resets()
    {
        FakeCluster cluster = new()
        {
            LocalIsOldest = false,
        };
        FakeTimeProvider time = new();
        using LostPartitionsListener listener = new(cluster.Ignite, Logger.None, true, true, time);

        Lose(listener, "c");
        time.Advance(Debounce);
        listener.OnNodeJoined();
        time.Advance(Debounce);

        Assert.Empty(cluster.Calls);
    }

    [Fact]
    public void Persistent_ResetsOnceANodeThatOwnsThemReturns()
    {
        FakeCluster cluster = new()
        {
            ResetFailures = 1,
        };
        FakeTimeProvider time = new();
        using LostPartitionsListener listener = new(cluster.Ignite, Logger.None, true, true, time);

        Lose(listener, "c");
        time.Advance(Debounce);
        // no node that owns them is online: retried when a node joins
        Assert.Equal(["ResetLostPartitions c (failed)",], cluster.Calls);
        listener.OnNodeJoined();
        time.Advance(Debounce);
        listener.OnNodeJoined();
        time.Advance(Debounce);

        // the baseline is left as is: the departed nodes return with their data
        Assert.Equal(["ResetLostPartitions c (failed)", "ResetLostPartitions c",], cluster.Calls);
    }

    [Fact]
    public void InMemory_DropsTheDepartedNodesFromTheBaseline_ThenResets()
    {
        FakeCluster cluster = new()
        {
            Baseline = ["n1", "n2",],
            Online = ["n1",],
        };
        FakeTimeProvider time = new();
        using LostPartitionsListener listener = new(cluster.Ignite, Logger.None, true, false, time);

        Lose(listener, "c");
        time.Advance(Debounce);

        Assert.Equal(
            [
                "SetBaselineAutoAdjustEnabledFlag False", "SetBaselineTopology 7",
                "SetBaselineAutoAdjustEnabledFlag True", "ResetLostPartitions c",
            ],
            cluster.Calls
        );
    }

    [Fact]
    public void WithoutAutoReset_TheLossIsOnlyLogged()
    {
        FakeCluster cluster = new();
        FakeTimeProvider time = new();
        using LostPartitionsListener listener = new(cluster.Ignite, Logger.None, false, false, time);

        Lose(listener, "c");
        listener.OnNodeJoined();
        time.Advance(Debounce);

        Assert.Empty(cluster.Calls);
    }

    private static void Lose(LostPartitionsListener listener, string cacheName) =>
        listener.OnPartitionLost(cacheName, "NODE_LEFT", ["10.0.0.2",], ["ignite-1",]);

    /// <summary>The cluster of the listener's node (n1), recording the changes made to it.</summary>
    private sealed class FakeCluster
    {
        public FakeCluster()
        {
            var cluster = Fake.Of<ICluster>((name, args) => name switch
                {
                    "ForServers" => Group(Online),
                    "GetBaselineTopology" => Baseline.Select(id => Fake.Of<IBaselineNode>((_, _) => id)).ToList(),
                    "get_TopologyVersion" => 7L,
                    "SetBaselineAutoAdjustEnabledFlag" or "SetBaselineTopology" => Record($"{name} {args[0]}"),
                    _ => throw new NotSupportedException(name),
                }
            );
            Ignite = Fake.Of<IIgnite>((name, args) => name switch
                {
                    "GetCluster" => cluster,
                    "ResetLostPartitions" => Reset(string.Join(",", (string[])args[0]!)),
                    _ => throw new NotSupportedException(name),
                }
            );
        }

        public IIgnite Ignite { get; }
        public List<string> Calls { get; } = [];
        public bool LocalIsOldest { get; init; } = true;
        public string[] Baseline { get; init; } = ["n1",];
        public string[] Online { get; init; } = ["n1",];
        public int ResetFailures { get; set; }

        private IClusterGroup Group(IEnumerable<string> consistentIds) => Fake.Of<IClusterGroup>((name, _) =>
            name switch
            {
                "ForOldest" => Group([LocalIsOldest ? "n1" : "n0",]),
                "GetNodes" => consistentIds.Select(Node).ToList(),
                _ => throw new NotSupportedException(name),
            }
        );

        private static IClusterNode Node(string consistentId) => Fake.Of<IClusterNode>((name, _) => name switch
            {
                "get_ConsistentId" => consistentId,
                "get_IsLocal" => consistentId == "n1",
                _ => throw new NotSupportedException(name),
            }
        );

        private object? Record(string call)
        {
            Calls.Add(call);
            return null;
        }

        private object? Reset(string caches)
        {
            if (ResetFailures-- <= 0)
            {
                return Record($"ResetLostPartitions {caches}");
            }

            Record($"ResetLostPartitions {caches} (failed)");
            throw new ClusterTopologyException("Cannot reset lost partitions because no baseline nodes are online");
        }
    }
}
