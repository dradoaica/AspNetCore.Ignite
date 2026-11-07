using Apache.Ignite.Core.Configuration;
using Apache.Ignite.Core.Deployment;
using Apache.Ignite.Core.Discovery.Tcp;
using Apache.Ignite.Core.Discovery.Tcp.Static;
using AspNetCore.IgniteServer;
using AspNetCore.IgniteServer.Management;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog.Core;
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
                SslKeyStoreFilePath = "server.jks"
            }
        );

        Assert.Equal("/tmp/spring-config.xml", runner.Configuration.SpringConfigUrl);
        Assert.NotNull(runner.Configuration.SslContextFactory);
        Assert.False(runner.Configuration.AuthenticationEnabled);
        // the host, not the JVM, handles SIGTERM
        Assert.Single(runner.Configuration.JvmOptions, option => option == "-Xrs");
    }

    [Fact]
    public void Constructor_AuthenticationEnablesPersistence()
    {
        using IgniteServerRunner runner = new(new IgniteServerRunnerOptions { AuthenticationEnabled = true });

        Assert.True(runner.Configuration.AuthenticationEnabled);
        Assert.True(runner.Configuration.DataStorageConfiguration.DefaultDataRegionConfiguration.PersistenceEnabled);
    }

    [Fact]
    public void SetOffHeapMemoryLimit_ConfiguresDefaultRegion()
    {
        using IgniteServerRunner runner = new(new IgniteServerRunnerOptions { EnableOffHeapMetrics = true });

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

        Assert.Equal(1024, runner.Configuration.JvmInitialMemoryMb);
        Assert.Equal(1024, runner.Configuration.JvmMaxMemoryMb);
    }

    [Fact]
    public void SetServerPortAndClusterEndpoints_ConfigureDiscovery()
    {
        using IgniteServerRunner runner = new(new IgniteServerRunnerOptions());

        runner.SetServerPort(47600);
        runner.SetClusterEndpoints(["10.0.0.1:47500", "10.0.0.2:47500"]);
        runner.SetConsistentId("node-1");

        var spi = Assert.IsType<TcpDiscoverySpi>(runner.Configuration.DiscoverySpi);
        Assert.Equal(47600, spi.LocalPort);
        Assert.Equal(
            ["10.0.0.1:47500", "10.0.0.2:47500"],
            Assert.IsType<TcpDiscoveryStaticIpFinder>(spi.IpFinder).Endpoints
        );
        Assert.Equal("node-1", runner.Configuration.ConsistentId);
    }

    [Fact]
    public void BuildAlterUserPasswordSql_EscapesQuotes()
    {
        Assert.Equal(
            "ALTER USER \"ig\"\"nite\" WITH PASSWORD 'it''s; DROP';",
            IgniteUserPasswordManager.BuildAlterUserPasswordSql("ig\"nite", "it's; DROP")
        );
    }

    [Fact]
    public void PeerAssemblyLoading_IsOptIn()
    {
        using IgniteServerRunner byDefault = new(new IgniteServerRunnerOptions());
        using IgniteServerRunner optedIn = new(new IgniteServerRunnerOptions { PeerAssemblyLoading = true });

        Assert.Equal(PeerAssemblyLoadingMode.Disabled, byDefault.Configuration.PeerAssemblyLoadingMode);
        Assert.Equal(PeerAssemblyLoadingMode.CurrentAppDomain, optedIn.Configuration.PeerAssemblyLoadingMode);
    }

    [Fact]
    public void PageEviction_OnlyForInMemoryRegions()
    {
        using IgniteServerRunner runner = new(new IgniteServerRunnerOptions());
        runner.SetOffHeapMemoryLimit(256);

        IgniteConfigurationFactory.SetPageEvictionMode(runner.Configuration);
        var region = runner.Configuration.DataStorageConfiguration.DefaultDataRegionConfiguration;
        Assert.Equal(DataPageEvictionMode.Random2Lru, region.PageEvictionMode);

        IgniteConfigurationFactory.SetPageEvictionMode(runner.Configuration, DataPageEvictionMode.RandomLru);
        Assert.Equal(DataPageEvictionMode.RandomLru, region.PageEvictionMode);

        runner.SetPersistence(true);
        IgniteConfigurationFactory.SetPageEvictionMode(runner.Configuration, DataPageEvictionMode.RandomLru);
        Assert.Equal(DataPageEvictionMode.Disabled, region.PageEvictionMode);
    }

    [Fact]
    public void SetServerPort_KeepsAnExistingTcpDiscoverySpi()
    {
        using IgniteServerRunner runner = new(new IgniteServerRunnerOptions());
        runner.Configuration.DiscoverySpi = new TcpDiscoverySpi();
        runner.SetServerPort(47600);

        Assert.Equal(47600, Assert.IsType<TcpDiscoverySpi>(runner.Configuration.DiscoverySpi).LocalPort);
    }

    [Theory]
    [InlineData(true, "ignite", "old")]
    [InlineData(false, "old", "ignite")]
    public void PasswordCandidates_MostLikelyFirst(bool newCluster, string first, string second)
    {
        IgniteUserPasswordManager manager = new(new IgniteServerRunnerOptions { IgniteUserPreviousPassword = "old" },
            Logger.None);

        Assert.Equal([first, second], manager.GetCurrentPasswordCandidates("new", newCluster));
    }

    [Fact]
    public void PasswordCandidates_NeverTheConfiguredPassword()
    {
        IgniteUserPasswordManager manager = new(new IgniteServerRunnerOptions(), Logger.None);

        Assert.Empty(manager.GetCurrentPasswordCandidates("ignite", true));
    }

    [Fact]
    public async Task StartedCheck_IsUnhealthyWithoutNode()
    {
        IgniteNodeStartedHealthCheck check = new(new IgniteNodeState());
        var registration = new HealthCheckRegistration("started", check, null, null);

        var result = await check.CheckHealthAsync(new HealthCheckContext { Registration = registration });

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}