using AspNetCore.IgniteServer;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class IgniteServerSettingsTests
{
    private static IgniteServerSettings Load(Dictionary<string, string?> values) =>
        IgniteServerSettings.Load(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void Load_Defaults_AreValid()
    {
        var settings = Load([]);

        Assert.Empty(settings.Validate());
        Assert.Equal(1024, settings.OnHeapMemory);
        Assert.Equal(2048, settings.OffHeapMemory);
        Assert.True(settings.RestartOnSslFileChange);
        Assert.Equal("TLS", settings.SslProtocol);
        Assert.Empty(settings.ClusterEndpoints);
    }

    [Fact]
    public void Load_BindsKeys()
    {
        var settings = Load(
            new Dictionary<string, string?>
            {
                ["DEFAULT_OFF_HEAP_MEMORY"] = "256",
                ["SPI_PORT"] = "47600",
                ["ENABLE_AUTHENTICATION"] = "True",
                ["CLUSTER_ENDPOINTS:0"] = "a:47500",
                ["CLUSTER_ENDPOINTS:1"] = "b:47500",
                ["BASELINE_AUTO_ADJUST_TIMEOUT_IN_SECONDS"] = "5",
            }
        );

        Assert.Equal(256, settings.OffHeapMemory);
        Assert.Equal(47600, settings.SpiPort);
        Assert.True(settings.EnableAuthentication);
        Assert.Equal(["a:47500", "b:47500",], settings.ClusterEndpoints);
        Assert.Equal(TimeSpan.FromSeconds(5), settings.ToRunnerOptions("spring.xml").BaselineAutoAdjustTimeout);
    }

    [Fact]
    public void Load_ClusterEndpoints_CommaSeparated() =>
        Assert.Equal(
            ["a:47500", "b:47500",],
            Load(new Dictionary<string, string?> { ["CLUSTER_ENDPOINTS"] = "a:47500, b:47500", }).ClusterEndpoints
        );

    [Fact]
    public void Validate_ReportsInvalidSettings()
    {
        var errors = Load(
                new Dictionary<string, string?>
                {
                    ["DEFAULT_ON_HEAP_MEMORY"] = "0",
                    ["SPI_PORT"] = "70000",
                    ["ACTIVATION_MIN_SERVER_NODES"] = "0",
                    ["USE_CLIENT_SSL"] = "true",
                }
            )
            .Validate();

        Assert.Contains(errors, error => error.StartsWith("DEFAULT_ON_HEAP_MEMORY", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("SPI_PORT", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("ACTIVATION_MIN_SERVER_NODES", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("SSL_KEY_STORE_FILE_PATH", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("SSL_CLIENT_CERTIFICATE_PATH", StringComparison.Ordinal));
    }

    [Fact]
    public void ToSpringConfigPlaceholders_CoversTheTemplates()
    {
        var placeholders = Load(new Dictionary<string, string?> { ["METRICS_UPDATE_FREQUENCY_IN_SECONDS"] = "30", })
            .ToSpringConfigPlaceholders();
        var configDirectory = Path.Combine(AppContext.BaseDirectory, "config");

        Assert.Equal("30000", placeholders["OPEN_CENSUS_METRIC_EXPORTER_SPI_PERIOD"]);
        foreach (var template in new[] { "spring-config.xml", "spring-config-client-with-ssl.xml", })
        {
            var rendered = AspNetCore.IgniteServer.Utils.SpringConfigRenderer.Render(
                File.ReadAllText(Path.Combine(configDirectory, template)),
                placeholders
            );
            foreach (var placeholder in placeholders.Keys)
            {
                Assert.DoesNotContain(placeholder, rendered);
            }
        }
    }

    [Fact]
    public void ApplySettings_ResolvesLocalAddressOnlyWhenUsed()
    {
        var resolved = 0;
        string GetLocalIpAddress()
        {
            resolved++;
            return "10.1.2.3";
        }

        using IgniteServerRunner withEndpoints = new(new IgniteServerRunnerOptions());
        IgniteServerHostedService.ApplySettings(
            withEndpoints,
            Load(new Dictionary<string, string?> { ["CLUSTER_ENDPOINTS:0"] = "a:47500", ["USE_TCP_DISCOVERY_STATIC_IP_FINDER"] = "true", }),
            GetLocalIpAddress
        );
        Assert.Equal(0, resolved);

        using IgniteServerRunner withStaticIpFinder = new(new IgniteServerRunnerOptions());
        IgniteServerHostedService.ApplySettings(
            withStaticIpFinder,
            Load(new Dictionary<string, string?> { ["USE_TCP_DISCOVERY_STATIC_IP_FINDER"] = "true", ["PERSISTENCE_ENABLED"] = "true", }),
            GetLocalIpAddress
        );
        Assert.Equal(1, resolved);
        var spi = Assert.IsType<Apache.Ignite.Core.Discovery.Tcp.TcpDiscoverySpi>(withStaticIpFinder.Configuration.DiscoverySpi);
        Assert.Equal(
            ["10.1.2.3:47500",],
            Assert.IsType<Apache.Ignite.Core.Discovery.Tcp.Static.TcpDiscoveryStaticIpFinder>(spi.IpFinder).Endpoints
        );
        Assert.True(withStaticIpFinder.Configuration.DataStorageConfiguration.DefaultDataRegionConfiguration.PersistenceEnabled);
    }
}
