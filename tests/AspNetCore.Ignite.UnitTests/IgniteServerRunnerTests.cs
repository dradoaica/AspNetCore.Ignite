using Apache.Ignite.Core.Discovery.Tcp;
using Apache.Ignite.Core.Discovery.Tcp.Static;
using AspNetCore.IgniteServer;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class IgniteServerRunnerTests
{
    [Fact]
    public void Constructor_AppliesOptions()
    {
        using IgniteServerRunner runner = new(
            new IgniteServerRunnerOptions
            {
                SpringConfigPath = "/tmp/spring-config.xml",
                UseSsl = true,
                SslKeyStoreFilePath = "server.jks",
            }
        );

        Assert.Equal("/tmp/spring-config.xml", runner.Configuration.SpringConfigUrl);
        Assert.NotNull(runner.Configuration.SslContextFactory);
        Assert.False(runner.Configuration.AuthenticationEnabled);
    }

    [Fact]
    public void Constructor_AuthenticationEnablesPersistence()
    {
        using IgniteServerRunner runner = new(new IgniteServerRunnerOptions { AuthenticationEnabled = true, });

        Assert.True(runner.Configuration.AuthenticationEnabled);
        Assert.True(runner.Configuration.DataStorageConfiguration.DefaultDataRegionConfiguration.PersistenceEnabled);
    }

    [Fact]
    public void SetOffHeapMemoryLimit_ConfiguresDefaultRegion()
    {
        using IgniteServerRunner runner = new(new IgniteServerRunnerOptions { EnableOffHeapMetrics = true, });

        runner.SetOffHeapMemoryLimit(2048);
        runner.SetPersistence(true);

        var region = runner.Configuration.DataStorageConfiguration.DefaultDataRegionConfiguration;
        Assert.Equal(2048L * 1024 * 1024, region.MaxSize);
        Assert.True(region.MetricsEnabled);
        Assert.True(region.PersistenceEnabled);
    }

    [Fact]
    public void SetOnHeapMemoryLimit_SetsJvmMemory()
    {
        using IgniteServerRunner runner = new(new IgniteServerRunnerOptions());

        runner.SetOnHeapMemoryLimit(1024);

        Assert.Equal(512, runner.Configuration.JvmInitialMemoryMb);
        Assert.Equal(1024, runner.Configuration.JvmMaxMemoryMb);
    }

    [Fact]
    public void SetServerPortAndClusterEndpoints_ConfigureDiscovery()
    {
        using IgniteServerRunner runner = new(new IgniteServerRunnerOptions());

        runner.SetServerPort(47600);
        runner.SetClusterEndpoints(["10.0.0.1:47500", "10.0.0.2:47500",]);
        runner.SetConsistentId("node-1");

        var spi = Assert.IsType<TcpDiscoverySpi>(runner.Configuration.DiscoverySpi);
        Assert.Equal(47600, spi.LocalPort);
        Assert.Equal(
            ["10.0.0.1:47500", "10.0.0.2:47500",],
            Assert.IsType<TcpDiscoveryStaticIpFinder>(spi.IpFinder).Endpoints
        );
        Assert.Equal("node-1", runner.Configuration.ConsistentId);
    }

    [Fact]
    public void BuildAlterUserPasswordSql_EscapesQuotes() =>
        Assert.Equal(
            "ALTER USER \"ig\"\"nite\" WITH PASSWORD 'it''s; DROP';",
            IgniteServerRunner.BuildAlterUserPasswordSql("ig\"nite", "it's; DROP")
        );
}
