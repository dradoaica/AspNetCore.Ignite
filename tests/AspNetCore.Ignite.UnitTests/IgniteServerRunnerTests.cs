using Apache.Ignite.Core.Client;
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
    public void Constructor_AppliesSettings()
    {
        using IgniteServerRunner runner = new(
            new IgniteServerSettings
            {
                EnableAuthentication = false,
                UseSsl = true,
                SslKeyStoreFilePath = "server.jks",
            },
            Logger.None,
            LocalAddress.Resolve,
            "/tmp/spring-config.xml"
        );

        Assert.Equal("/tmp/spring-config.xml", runner.Configuration.SpringConfigUrl);
        Assert.NotNull(runner.Configuration.SslContextFactory);
        Assert.False(runner.Configuration.AuthenticationEnabled);
        // the host, not the JVM, handles SIGTERM
        Assert.Single(runner.Configuration.JvmOptions, option => option == "-Xrs");
    }

    [Fact]
    public void Authentication_EnablesPersistence()
    {
        var configuration = IgniteConfigurationFactory.Create(
            new IgniteServerSettings(),
            Logger.None,
            LocalAddress.Resolve
        );

        Assert.True(configuration.AuthenticationEnabled);
        Assert.True(configuration.DataStorageConfiguration.DefaultDataRegionConfiguration.PersistenceEnabled);
    }

    [Fact]
    public void Memory_ConfiguresTheDefaultRegionAndTheJvm()
    {
        var configuration = IgniteConfigurationFactory.Create(
            new IgniteServerSettings
            {
                EnableAuthentication = false,
                OffHeapMemory = 4096,
                OnHeapMemory = 512,
                PersistenceEnabled = true,
            },
            Logger.None,
            LocalAddress.Resolve
        );

        var region = configuration.DataStorageConfiguration.DefaultDataRegionConfiguration;
        Assert.Equal(4096L * 1024 * 1024, region.MaxSize);
        Assert.True(region.MetricsEnabled);
        Assert.True(region.PersistenceEnabled);
        Assert.Equal(512, configuration.JvmInitialMemoryMb);
        Assert.Equal(512, configuration.JvmMaxMemoryMb);
    }

    [Fact]
    public void SpiPortAndClusterEndpoints_ConfigureDiscovery()
    {
        var configuration = IgniteConfigurationFactory.Create(
            new IgniteServerSettings
            {
                SpiPort = 47600,
                ClusterEndpoints = ["10.0.0.1:47500", "10.0.0.2:47500",],
                ConsistentId = "node-1",
            },
            Logger.None,
            LocalAddress.Resolve
        );

        var spi = Assert.IsType<TcpDiscoverySpi>(configuration.DiscoverySpi);
        Assert.Equal(47600, spi.LocalPort);
        Assert.Equal(
            ["10.0.0.1:47500", "10.0.0.2:47500",],
            Assert.IsType<TcpDiscoveryStaticIpFinder>(spi.IpFinder).Endpoints
        );
        Assert.Equal("node-1", configuration.ConsistentId);
    }

    [Theory]
    [InlineData(null, 47500)]
    [InlineData(47600, 47600)]
    public void WithoutClusterEndpoints_TheNodeIsItsOwnStaticDiscoveryEndpoint(int? spiPort, int endpointPort)
    {
        var configuration = IgniteConfigurationFactory.Create(
            new IgniteServerSettings
            {
                SpiPort = spiPort,
            },
            Logger.None,
            LocalAddress.Resolve
        );

        var spi = Assert.IsType<TcpDiscoverySpi>(configuration.DiscoverySpi);
        Assert.Equal(
            [$"{LocalAddress.Value}:{endpointPort}",],
            Assert.IsType<TcpDiscoveryStaticIpFinder>(spi.IpFinder).Endpoints
        );
    }

    [Fact]
    public void LocalAddress_IsResolvedOnlyWhenNeeded()
    {
        // a failing DNS lookup must not break the nodes that do not use the local address
        static string Fail()
        {
            throw new InvalidOperationException("Local IP Address Not Found!");
        }

        IgniteConfigurationFactory.Create(
            new IgniteServerSettings
            {
                ClusterEndpoints = ["a:47500",],
            },
            Logger.None,
            Fail
        );
        var withoutStaticIpFinder = IgniteConfigurationFactory.Create(
            new IgniteServerSettings
            {
                UseTcpDiscoveryStaticIpFinder = false,
            },
            Logger.None,
            Fail
        );

        // the discovery of the Spring configuration (Kubernetes) stays
        Assert.Null(withoutStaticIpFinder.DiscoverySpi);
    }

    [Fact]
    public void BuildAlterUserPasswordSql_EscapesQuotes() => Assert.Equal(
        "ALTER USER \"ig\"\"nite\" WITH PASSWORD 'it''s; DROP';",
        IgniteUserPasswordManager.BuildAlterUserPasswordSql("ig\"nite", "it's; DROP")
    );

    [Fact]
    public void PeerAssemblyLoading_IsOptIn()
    {
        var byDefault = IgniteConfigurationFactory.Create(
            new IgniteServerSettings(),
            Logger.None,
            LocalAddress.Resolve
        );
        var optedIn = IgniteConfigurationFactory.Create(
            new IgniteServerSettings
            {
                PeerAssemblyLoading = true,
            },
            Logger.None,
            LocalAddress.Resolve
        );

        Assert.Equal(PeerAssemblyLoadingMode.Disabled, byDefault.PeerAssemblyLoadingMode);
        Assert.Equal(PeerAssemblyLoadingMode.CurrentAppDomain, optedIn.PeerAssemblyLoadingMode);
    }

    [Theory]
    [InlineData(false, null, DataPageEvictionMode.Random2Lru)]
    [InlineData(false, "RANDOM_LRU", DataPageEvictionMode.RandomLru)]
    [InlineData(true, "RANDOM_LRU", DataPageEvictionMode.Disabled)]
    public void PageEviction_OnlyForInMemoryRegions(bool persistent, string? mode, DataPageEvictionMode expected)
    {
        var configuration = IgniteConfigurationFactory.Create(
            new IgniteServerSettings
            {
                EnableAuthentication = false,
                PersistenceEnabled = persistent,
                PageEvictionMode = mode,
            },
            Logger.None,
            LocalAddress.Resolve
        );

        Assert.Equal(expected, configuration.DataStorageConfiguration.DefaultDataRegionConfiguration.PageEvictionMode);
    }

    [Theory]
    [InlineData(true, "ignite", "old")]
    [InlineData(false, "old", "ignite")]
    public void PasswordCandidates_MostLikelyFirst(bool newCluster, string first, string second)
    {
        IgniteUserPasswordManager manager = new(
            new IgniteServerSettings
            {
                IgniteUserPreviousPassword = "old",
            },
            Logger.None
        );

        Assert.Equal([first, second,], manager.GetCurrentPasswordCandidates("new", newCluster));
    }

    [Fact]
    public void PasswordClient_ConnectsToTheLocalNodeOnly()
    {
        IgniteUserPasswordManager manager = new(
            new IgniteServerSettings
            {
                UseClientSsl = true,
                SslClientCertificatePath = "client.pfx",
            },
            Logger.None
        );

        var cfg = manager.GetClientConfiguration("127.0.0.1:10800", "secret");

        // the other nodes would get the password too, over connections whose certificates are not validated
        Assert.False(cfg.EnableClusterDiscovery);
        Assert.Equal(["127.0.0.1:10800",], cfg.Endpoints);
        Assert.Equal(IgniteUserPasswordManager.DefaultUserName, cfg.UserName);
        Assert.Equal("secret", cfg.Password);
        Assert.Equal("client.pfx", Assert.IsType<SslStreamFactory>(cfg.SslStreamFactory).CertificatePath);
    }

    [Fact]
    public void PasswordCandidates_NeverTheConfiguredPassword()
    {
        IgniteUserPasswordManager manager = new(new IgniteServerSettings(), Logger.None);

        Assert.Empty(manager.GetCurrentPasswordCandidates("ignite", true));
    }

    [Fact]
    public async Task StartedCheck_IsUnhealthyWithoutNode()
    {
        IgniteNodeStartedHealthCheck check = new(new IgniteNodeState());
        var registration = new HealthCheckRegistration("started", check, null, null);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext
            {
                Registration = registration,
            }
        );

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public void DdlCacheName_IsStablePerNode()
    {
        var name = IgniteUserPasswordManager.GetDdlCacheName("ignite-0");

        // nodes that apply the password at the same time do not share (and destroy) one cache
        Assert.Equal(name, IgniteUserPasswordManager.GetDdlCacheName("ignite-0"));
        Assert.NotEqual(name, IgniteUserPasswordManager.GetDdlCacheName("ignite-1"));
        Assert.Matches("^alterUserSqlDdlCommand-[0-9a-f]{16}$", name);
    }
}
