using DotNet.Testcontainers.Builders;
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
        await TestContainerLogs.WaitForMessageAsync(node1, IgniteServerFixture.RunningMessage, 1, TimeSpan.FromSeconds(60));

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
}
