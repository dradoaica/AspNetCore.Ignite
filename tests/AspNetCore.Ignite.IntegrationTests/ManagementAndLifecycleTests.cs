using System.Net;
using System.Text;
using Apache.Ignite.Core.Cache.Query;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace AspNetCore.Ignite.IntegrationTests;

[Collection(IgniteServerCollection.Name)]
public sealed class ManagementAndLifecycleTests(IgniteServerFixture fixture)
{
    private const string DockerUnavailable = "Docker is not available.";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

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
        var (exitCode, _, _) = await ExecAsync(fixture.Node, "bash", "-c",
            $"exec 3<>/dev/tcp/127.0.0.1/{IgniteServerFixture.RestPort}");
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

    /// <summary>
    ///     The configuration sections of the Ignite chart that the chart renders (SPRING_EXTRA_PROPERTIES_FILE), checked on
    ///     the node through what it reports to clients (attributes, metrics, caches), with SQL_SCHEMAS and Java peer
    ///     class loading.
    /// </summary>
    [SkippableFact]
    public async Task SpringConfigurationSections_ConfigureTheNode()
    {
        Skip.IfNot(fixture.IsDockerAvailable, DockerUnavailable);
        const string properties = """
                                  <property name="userAttributes">
                                      <map><entry key="stage" value="spring-test"/></map>
                                  </property>
                                  <property name="binaryConfiguration">
                                      <bean class="org.apache.ignite.configuration.BinaryConfiguration">
                                          <property name="compactFooter" value="false"/>
                                      </bean>
                                  </property>
                                  <property name="cacheConfiguration">
                                      <list>
                                          <bean class="org.apache.ignite.configuration.CacheConfiguration">
                                              <property name="name" value="fromSpring"/>
                                          </bean>
                                      </list>
                                  </property>
                                  <property name="executorConfiguration">
                                      <list>
                                          <bean class="org.apache.ignite.configuration.ExecutorConfiguration">
                                              <property name="name" value="myPool"/>
                                              <property name="size" value="3"/>
                                          </bean>
                                      </list>
                                  </property>
                                  <property name="transactionConfiguration">
                                      <bean class="org.apache.ignite.configuration.TransactionConfiguration">
                                          <property name="txSerializableEnabled" value="true"/>
                                      </bean>
                                  </property>
                                  <property name="collisionSpi"><bean class="org.apache.ignite.spi.collision.fifoqueue.FifoQueueCollisionSpi"/></property>
                                  <property name="deploymentSpi"><bean class="org.apache.ignite.spi.deployment.local.LocalDeploymentSpi"/></property>
                                  <property name="failoverSpi">
                                      <bean class="org.apache.ignite.spi.failover.always.AlwaysFailoverSpi">
                                          <property name="maximumFailoverAttempts" value="7"/>
                                      </bean>
                                  </property>
                                  <property name="loadBalancingSpi"><bean class="org.apache.ignite.spi.loadbalancing.roundrobin.RoundRobinLoadBalancingSpi"/></property>
                                  """;
        await using var node = fixture.CreateNodeBuilder()
            .WithEnvironment("ENABLE_AUTHENTICATION", "false")
            .WithEnvironment("SPRING_EXTRA_PROPERTIES_FILE", "/app/work/spring-extra-properties.xml")
            .WithEnvironment("SQL_SCHEMAS", "MY_SCHEMA, OTHER_SCHEMA")
            .WithEnvironment("PEER_CLASS_LOADING_ENABLED", "true")
            .WithResourceMapping(Encoding.UTF8.GetBytes(properties), "/app/work/spring-extra-properties.xml", 1001)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(IgniteServerFixture.RunningMessage))
            .Build();
        await node.StartAsync();

        using var igniteClient =
            CacheFactory.ConnectAsClient(
                CacheFactory.GetIgniteClientConfiguration(IgniteServerFixture.GetEndpoint(node)));
        var attributes = igniteClient.GetCluster().ForServers().GetNodes().Single().Attributes;
        var sql = igniteClient.GetCache<int, int>("fromSpring");

        IList<IList<object>> Query(string text)
        {
            return sql.Query(new SqlFieldsQuery(text) { Schema = "PUBLIC" }).GetAll();
        }

        Assert.Contains("fromSpring", igniteClient.GetCacheNames());
        Assert.Equal("spring-test", attributes["stage"]);
        Assert.Equal(false, attributes["org.apache.ignite.marshaller.compactFooter"]);
        Assert.Equal(true, attributes["org.apache.ignite.tx.serializable.enabled"]);
        Assert.Equal(true, attributes["org.apache.ignite.peer.classloading.enabled"]);
        Assert.Equal(7, Convert.ToInt32(attributes["AlwaysFailoverSpi.gg:failover:maxattempts"]));
        foreach (var spi in new[]
                 {
                     "fifoqueue.FifoQueueCollisionSpi", "deployment.local.LocalDeploymentSpi",
                     "loadbalancing.roundrobin.RoundRobinLoadBalancingSpi"
                 })
            Assert.Contains(attributes.Values,
                value => value is string text && text.EndsWith(spi, StringComparison.Ordinal));

        Assert.Contains(Query("SELECT NAME, VALUE FROM SYS.METRICS WHERE NAME = 'threadPools.myPool.CorePoolSize'"),
            row => Convert.ToInt32(row[1]) == 3);
        var schemas = Query("SELECT SCHEMA_NAME FROM SYS.SCHEMAS").Select(row => (string)row[0]).ToList();
        Assert.Contains("MY_SCHEMA", schemas);
        Assert.Contains("OTHER_SCHEMA", schemas);
    }

    [SkippableFact]
    public void PeerClassLoading_IsOffByDefault()
    {
        Skip.IfNot(fixture.IsDockerAvailable, DockerUnavailable);
        using var igniteClient = CacheFactory.ConnectAsClient(
            CacheFactory.GetIgniteClientConfiguration(fixture.Endpoint, IgniteServerFixture.UserName,
                IgniteServerFixture.Password)
        );

        Assert.Equal(false,
            igniteClient.GetCluster().ForServers().GetNodes().Single()
                .Attributes["org.apache.ignite.peer.classloading.enabled"]);
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
        IContainer node,
        params string[] command
    )
    {
        var result = await node.ExecAsync(command);
        return (result.ExitCode ?? -1, result.Stdout, result.Stderr);
    }
}