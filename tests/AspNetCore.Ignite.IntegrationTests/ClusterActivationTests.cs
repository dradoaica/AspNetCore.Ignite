using System.Net;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Xunit;

namespace AspNetCore.Ignite.IntegrationTests;

[Collection(IgniteServerCollection.Name)]
public sealed class ClusterActivationTests(IgniteServerFixture fixture)
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
            CacheFactory.GetIgniteClientConfiguration(
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
                   CacheFactory.GetIgniteClientConfiguration(IgniteServerFixture.GetEndpoint(nodeA))))
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

    private static async Task<bool> Delay()
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        return true;
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