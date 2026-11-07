using DotNet.Testcontainers.Builders;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace AspNetCore.Ignite.IntegrationTests;

[Collection(IgniteServerCollection.Name)]
public sealed class ManagementAndLifecycleTests(IgniteServerFixture fixture)
{
    private const string DockerUnavailable = "Docker is not available.";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10), };

    [SkippableFact]
    public async Task ManagementEndpoints_ReportHealthAndMetrics()
    {
        Skip.IfNot(fixture.IsDockerAvailable, DockerUnavailable);

        var live = await Http.GetAsync(IgniteServerFixture.GetManagementUri(fixture.Node, "/health/liveness"));
        var started = await Http.GetAsync(IgniteServerFixture.GetManagementUri(fixture.Node, "/health/startup"));
        var ready = await Http.GetAsync(IgniteServerFixture.GetManagementUri(fixture.Node, "/health/readiness"));
        var metrics = await Http.GetStringAsync(IgniteServerFixture.GetManagementUri(fixture.Node, "/metrics"));

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Contains("ignite_node_ready 1", metrics);
        Assert.Contains("ignite_cluster_active 1", metrics);
        Assert.Contains("ignite_cluster_server_nodes 1", metrics);
        Assert.Matches(@"ignite_node_heap_used_bytes [1-9]", metrics);
        Assert.Contains("ignite_data_region_off_heap_size_bytes{region=\"default\"}", metrics);
    }

    [SkippableFact]
    public async Task Rest_IsOffByDefault_AndServesRequestsWhenEnabled()
    {
        Skip.IfNot(fixture.IsDockerAvailable, DockerUnavailable);
        await using var restNode = fixture.CreateNodeBuilder()
            .WithPortBinding(IgniteServerFixture.RestPort, true)
            .WithEnvironment("REST_ENABLED", "true")
            .WithEnvironment("ENABLE_AUTHENTICATION", "false")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(IgniteServerFixture.RunningMessage))
            .Build();
        await restNode.StartAsync();

        // the REST API (Jetty and the ignite-rest-http module) answers when enabled...
        var version = await Http.GetStringAsync(
            $"http://{restNode.Hostname}:{restNode.GetMappedPublicPort(IgniteServerFixture.RestPort)}/ignite?cmd=version"
        );
        Assert.Contains("\"successStatus\":0", version);

        // ...and nothing listens on its port by default
        var (exitCode, _, _) = await ExecAsync(fixture.Node, "bash", "-c", $"exec 3<>/dev/tcp/127.0.0.1/{IgniteServerFixture.RestPort}");
        Assert.NotEqual(0, exitCode);
    }

    [SkippableTheory]
    [InlineData("config/default-config.xml")]
    [InlineData("config/persistent-config.xml")]
    public async Task ShippedConfigurationFiles_Start(string configFile)
    {
        Skip.IfNot(fixture.IsDockerAvailable, DockerUnavailable);
        await using var node = fixture.CreateNodeBuilder()
            .WithEnvironment("CONFIG_FILE", configFile)
            .WithEnvironment("ENABLE_AUTHENTICATION", "false")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(IgniteServerFixture.RunningMessage))
            .Build();

        await node.StartAsync();

        var ready = await Http.GetAsync(IgniteServerFixture.GetManagementUri(node, "/health/readiness"));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [SkippableFact]
    public async Task SpringConfigurationFragments_ConfigureTheNode()
    {
        Skip.IfNot(fixture.IsDockerAvailable, DockerUnavailable);
        // the cacheConfiguration section of the Ignite chart, as is
        const string properties = """
            <property name="cacheConfiguration">
                <list>
                    <bean class="org.apache.ignite.configuration.CacheConfiguration">
                        <property name="name" value="fromSpring"/>
                        <property name="backups" value="1"/>
                    </bean>
                </list>
            </property>
            """;
        await using var node = fixture.CreateNodeBuilder()
            .WithEnvironment("ENABLE_AUTHENTICATION", "false")
            .WithEnvironment("SPRING_EXTRA_PROPERTIES_FILE", "/app/work/spring-extra-properties.xml")
            .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes(properties), "/app/work/spring-extra-properties.xml", 1001, 0)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(IgniteServerFixture.RunningMessage))
            .Build();
        await node.StartAsync();

        using var igniteClient = CacheFactory.ConnectAsClient(CacheFactory.GetIgniteClientConfiguration(IgniteServerFixture.GetEndpoint(node)));

        Assert.Contains("fromSpring", igniteClient.GetCacheNames());
    }

    [SkippableFact]
    public async Task GracefulStop_ExitsWithZero()
    {
        Skip.IfNot(fixture.IsDockerAvailable, DockerUnavailable);
        await using var node = fixture.CreateNodeBuilder()
            .WithEnvironment("ENABLE_AUTHENTICATION", "false")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(IgniteServerFixture.RunningMessage))
            .Build();
        await node.StartAsync();

        // docker stop: SIGTERM, then the host stops the node gracefully
        await node.StopAsync();

        Assert.Equal(0, await node.GetExitCodeAsync());
        var (stdout, _) = await node.GetLogsAsync();
        Assert.Contains("Ignite server runner stopped.", stdout);
    }

    [SkippableFact]
    public async Task InvalidSettings_ExitWithOne()
    {
        Skip.IfNot(fixture.IsDockerAvailable, DockerUnavailable);
        await using var node = fixture.CreateNodeBuilder()
            .WithEnvironment("DEFAULT_OFF_HEAP_MEMORY", "-5")
            .WithWaitStrategy(Wait.ForUnixContainer())
            .Build();

        await node.StartAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));

        // waits until the container exits
        Assert.Equal(1, await node.GetExitCodeAsync(timeout.Token));
        var (_, stderr) = await node.GetLogsAsync();
        Assert.Contains("DEFAULT_OFF_HEAP_MEMORY (-OffHeap) must be positive.", stderr);
    }

    private static async Task<(long ExitCode, string Stdout, string Stderr)> ExecAsync(
        DotNet.Testcontainers.Containers.IContainer node,
        params string[] command
    )
    {
        var result = await node.ExecAsync(command);
        return (result.ExitCode ?? -1, result.Stdout, result.Stderr);
    }
}
