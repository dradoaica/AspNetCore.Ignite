using System.Net;
using Apache.Ignite.Core.Cache.Configuration;
using Apache.Ignite.Core.Client;
using Apache.Ignite.Core.Client.Cache;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using DotNet.Testcontainers.Volumes;
using Xunit;

namespace AspNetCore.Ignite.IntegrationTests;

[Collection(IgniteClusterCollection.Name)]
public sealed class ClusterActivationTests(IgniteClusterFixture fixture)
{
    [SkippableFact]
    public async Task PersistentCluster_IsActivatedOnceTheMinimumServerNodesJoined()
    {
        Skip.IfNot(fixture.IsDockerAvailable, "Docker is not available.");
        await using var network = new NetworkBuilder().WithName($"ignite-{Guid.NewGuid():N}").Build();
        await network.CreateAsync();
        const string clusterEndpoints = "ignite-1:47500,ignite-2:47500";
        await using var node1 = fixture.CreateNodeBuilder()
            .WithNetwork(network)
            .WithNetworkAliases("ignite-1")
            .WithEnvironment("DEFAULT_CONSISTENT_ID", "ignite-1")
            .WithEnvironment("CLUSTER_ENDPOINTS", clusterEndpoints)
            .WithEnvironment("ACTIVATION_MIN_SERVER_NODES", "2")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Waiting for 2 server nodes"))
            .Build();
        await using var node2 = fixture.CreateNodeBuilder()
            .WithNetwork(network)
            .WithNetworkAliases("ignite-2")
            .WithEnvironment("DEFAULT_CONSISTENT_ID", "ignite-2")
            .WithEnvironment("CLUSTER_ENDPOINTS", clusterEndpoints)
            .WithEnvironment("ACTIVATION_MIN_SERVER_NODES", "2")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(IgniteServerFixture.RunningMessage))
            .Build();

        // alone, the first node waits instead of activating a one-node baseline
        await node1.StartAsync();
        await node2.StartAsync();
        await TestContainerLogs.WaitForMessageAsync(node1, IgniteServerFixture.RunningMessage, 1,
            TimeSpan.FromSeconds(60));

        using var igniteClient = CacheFactory.ConnectAsClient(
            IgniteServerFixture.ClientConfiguration(
                IgniteServerFixture.GetEndpoint(node1),
                IgniteServerFixture.UserName,
                IgniteServerFixture.Password
            )
        );
        var cluster = igniteClient.GetCluster();
        Assert.True(cluster.IsActive());
        Assert.Equal(2, cluster.ForServers().GetNodes().Count);
    }

    [SkippableFact]
    public async Task JoiningNode_RebalancesBeforeItIsReady()
    {
        Skip.IfNot(fixture.IsDockerAvailable, "Docker is not available.");
        await using var network = new NetworkBuilder().WithName($"ignite-{Guid.NewGuid():N}").Build();
        await network.CreateAsync();
        const string clusterEndpoints = "ignite-a:47500,ignite-b:47500";
        await using var nodeA = InMemoryNode(network, "ignite-a", clusterEndpoints);
        await nodeA.StartAsync();
        using (var igniteClient =
               CacheFactory.ConnectAsClient(
                   IgniteServerFixture.ClientConfiguration(IgniteServerFixture.GetEndpoint(nodeA))))
        {
            var cache = CacheFactory.GetOrCreateCacheClient<int, string>(igniteClient, "rebalanced");
            cache.PutAll(
                Enumerable.Range(0, 20_000).Select(i => new KeyValuePair<int, string>(i, new string('x', 100))));
        }

        await using var nodeB = InMemoryNode(network, "ignite-b", clusterEndpoints);
        await nodeB.StartAsync();

        // the new node receives its partitions (and their backups) and reports when it is done
        await TestContainerLogs.WaitForMessageAsync(nodeB, "Rebalancing of cache", 1, TimeSpan.FromSeconds(60));
        await TestContainerLogs.WaitForMessageAsync(nodeB, "finished in", 1, TimeSpan.FromSeconds(120));
        using HttpClient http = new();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        HttpStatusCode status;
        do
        {
            status = (await http.GetAsync(IgniteServerFixture.GetManagementUri(nodeB, "/health/readiness"))).StatusCode;
        } while (status != HttpStatusCode.OK && DateTime.UtcNow < deadline && await Delay());

        Assert.Equal(HttpStatusCode.OK, status);
        var metrics = await http.GetStringAsync(IgniteServerFixture.GetManagementUri(nodeB, "/metrics"));
        Assert.Matches(@"ignite_cache_local_entries\{cache=""rebalanced""\} [1-9]", metrics);
        Assert.Contains("ignite_node_rebalancing_caches 0", metrics);
    }

    [SkippableFact]
    public async Task LostPartitions_AreReset_AndTheCacheIsUsableAgain()
    {
        Skip.IfNot(fixture.IsDockerAvailable, "Docker is not available.");
        await using var network = new NetworkBuilder().WithName($"ignite-{Guid.NewGuid():N}").Build();
        await network.CreateAsync();
        const string clusterEndpoints = "ignite-x:47500,ignite-y:47500";
        await using var nodeX = InMemoryNode(network, "ignite-x", clusterEndpoints);
        await using var nodeY = InMemoryNode(network, "ignite-y", clusterEndpoints);
        await nodeX.StartAsync();
        await nodeY.StartAsync();
        // ready once the baseline auto-adjust added it: it then holds a share of the partitions
        using HttpClient http = new();
        await WaitUntilAsync(
            () => http.GetAsync(IgniteServerFixture.GetManagementUri(nodeY, "/health/readiness")).Result.StatusCode ==
                  HttpStatusCode.OK,
            TimeSpan.FromSeconds(90)
        );
        using var igniteClient =
            CacheFactory.ConnectAsClient(IgniteServerFixture.ClientConfiguration(IgniteServerFixture.GetEndpoint(nodeX)));
        // without backups, the partitions of a node that leaves are lost; the safe policy blocks them until reset
        var cache = igniteClient.GetOrCreateCache<int, int>(
            new CacheClientConfiguration("lossy") { Backups = 0, PartitionLossPolicy = PartitionLossPolicy.ReadWriteSafe });
        var entries = Enumerable.Range(0, 1000).Select(i => new KeyValuePair<int, int>(i, i)).ToList();
        cache.PutAll(entries);

        await nodeY.StopAsync();

        // the departed in-memory node is dropped from the baseline, so the partitions can be reset (empty)
        await TestContainerLogs.WaitForMessageAsync(nodeX, "Partition data lost", 1, TimeSpan.FromSeconds(60));
        await TestContainerLogs.WaitForMessageAsync(nodeX, "were reset; their data is gone", 1, TimeSpan.FromSeconds(60));
        await WaitUntilAsync(() => Succeeds(() => cache.PutAll(entries)), TimeSpan.FromSeconds(30));
        Assert.Equal(entries.Count, cache.GetSize());
    }

    [SkippableFact]
    public async Task LostPersistentPartitions_AreResetOnceTheirNodeReturns_WithTheirData()
    {
        Skip.IfNot(fixture.IsDockerAvailable, "Docker is not available.");
        await using var network = new NetworkBuilder().WithName($"ignite-{Guid.NewGuid():N}").Build();
        await network.CreateAsync();
        await using var volume = new VolumeBuilder().WithName($"ignite-{Guid.NewGuid():N}").Build();
        await volume.CreateAsync();
        const string clusterEndpoints = "ignite-p:47500,ignite-q:47500";
        await using var nodeP = PersistentNode(network, "ignite-p", clusterEndpoints, null)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Waiting for 2 server nodes"))
            .Build();
        await nodeP.StartAsync();
        List<KeyValuePair<int, int>> entries = [.. Enumerable.Range(0, 1000).Select(i => new KeyValuePair<int, int>(i, i))];
        await using (var nodeQ = PersistentNode(network, "ignite-q", clusterEndpoints, volume).Build())
        {
            await nodeQ.StartAsync();
            await TestContainerLogs.WaitForMessageAsync(nodeP, IgniteServerFixture.RunningMessage, 1,
                TimeSpan.FromSeconds(60));
            using var igniteClient =
                CacheFactory.ConnectAsClient(
                    IgniteServerFixture.ClientConfiguration(IgniteServerFixture.GetEndpoint(nodeP)));
            igniteClient.GetOrCreateCache<int, int>(
                    new CacheClientConfiguration("durable")
                        { Backups = 0, PartitionLossPolicy = PartitionLossPolicy.ReadWriteSafe })
                .PutAll(entries);
        }

        // the partitions of the departed node stay lost (its data is on its volume), and are not reset meanwhile
        await TestContainerLogs.WaitForMessageAsync(nodeP, "Partition data lost", 1, TimeSpan.FromSeconds(60));
        await TestContainerLogs.WaitForMessageAsync(nodeP, "once a node that owns them returns", 1,
            TimeSpan.FromSeconds(30));
        using HttpClient http = new();
        var lost = await http.GetStringAsync(IgniteServerFixture.GetManagementUri(nodeP, "/metrics"));
        Assert.Matches(@"ignite_cluster_lost_partitions [1-9]", lost);
        Assert.Matches(@"ignite_cache_lost_partitions\{cache=""durable""\} [1-9]", lost);
        await using var returned = PersistentNode(network, "ignite-q", clusterEndpoints, volume).Build();
        await returned.StartAsync();

        await TestContainerLogs.WaitForMessageAsync(nodeP, "with the data of the nodes that returned", 1,
            TimeSpan.FromSeconds(60));
        using var client =
            CacheFactory.ConnectAsClient(IgniteServerFixture.ClientConfiguration(IgniteServerFixture.GetEndpoint(nodeP)));
        var cache = client.GetCache<int, int>("durable");
        await WaitUntilAsync(() => Succeeds(() => cache.GetSize()), TimeSpan.FromSeconds(30));
        Assert.Equal(entries.Count, cache.GetSize());
        // a scrape may still see the snapshot of the second before
        await WaitUntilAsync(
            () => http.GetStringAsync(IgniteServerFixture.GetManagementUri(nodeP, "/metrics")).Result
                .Contains("ignite_cluster_lost_partitions 0", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10)
        );
    }

    private static bool Succeeds(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (IgniteClientException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"The condition was not met within {timeout}.");

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private static async Task<bool> Delay()
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        return true;
    }

    private ContainerBuilder PersistentNode(INetwork network, string alias, string clusterEndpoints, IVolume? volume)
    {
        var builder = fixture.CreateNodeBuilder()
            .WithNetwork(network)
            .WithNetworkAliases(alias)
            .WithEnvironment("ENABLE_AUTHENTICATION", "false")
            .WithEnvironment("PERSISTENCE_ENABLED", "true")
            .WithEnvironment("ACTIVATION_MIN_SERVER_NODES", "2")
            .WithEnvironment("DEFAULT_CONSISTENT_ID", alias)
            .WithEnvironment("CLUSTER_ENDPOINTS", clusterEndpoints)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(IgniteServerFixture.RunningMessage));
        return volume == null ? builder : builder.WithVolumeMount(volume, "/app/work");
    }

    private IContainer InMemoryNode(
        INetwork network,
        string alias,
        string clusterEndpoints
    )
    {
        return fixture.CreateNodeBuilder()
            .WithNetwork(network)
            .WithNetworkAliases(alias)
            .WithEnvironment("ENABLE_AUTHENTICATION", "false")
            .WithEnvironment("DEFAULT_CONSISTENT_ID", alias)
            .WithEnvironment("CLUSTER_ENDPOINTS", clusterEndpoints)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(IgniteServerFixture.RunningMessage))
            .Build();
    }
}