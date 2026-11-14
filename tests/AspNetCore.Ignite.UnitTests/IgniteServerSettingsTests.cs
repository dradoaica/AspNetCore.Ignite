using Apache.Ignite.Core.Configuration;
using AspNetCore.IgniteServer;
using AspNetCore.IgniteServer.Utils;
using Microsoft.Extensions.Configuration;
using Serilog.Core;
using System.Xml.Linq;
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
        Assert.Equal(TimeSpan.FromSeconds(5), settings.GetBaselineAutoAdjustTimeout(true));
    }

    [Theory]
    [InlineData(null, null, null, null, true)]
    [InlineData("prod", null, null, null, true)]
    [InlineData("prod", "ignite-svc", null, null, false)]
    [InlineData(null, null, "a:47500", null, false)]
    [InlineData(null, null, null, "/config/ignite.xml", false)]
    public void Validate_KubernetesDiscovery_NeedsItsNamespaceAndService(
        string? k8sNamespace,
        string? k8sServiceName,
        string? clusterEndpoints,
        string? configFile,
        bool invalid
    )
    {
        var settings = Load(
            new Dictionary<string, string?>
            {
                ["USE_TCP_DISCOVERY_STATIC_IP_FINDER"] = "false",
                ["K8S_NAMESPACE"] = k8sNamespace,
                ["K8S_SERVICE_NAME"] = k8sServiceName,
                ["CLUSTER_ENDPOINTS"] = clusterEndpoints,
                ["CONFIG_FILE"] = configFile,
            }
        );

        Assert.Equal(
            invalid,
            settings.Validate()
                .Any(error => error.StartsWith("K8S_NAMESPACE and K8S_SERVICE_NAME", StringComparison.Ordinal))
        );
    }

    [Theory]
    [InlineData("CLUSTER_ENDPOINTS:0", "cmd:47500")]
    [InlineData("CLUSTER_ENDPOINTS", "cmd:47500")]
    public void Load_Lists_ComeFromTheSourceOfTheHighestPriority(string key, string value)
    {
        // as the environment (CLUSTER_ENDPOINTS__0, __1) and then the command line (-Cluster)
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CLUSTER_ENDPOINTS:0"] = "env-a:47500",
                    ["CLUSTER_ENDPOINTS:1"] = "env-b:47500",
                    ["SQL_SCHEMAS"] = "ENV",
                }
            )
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [key] = value,
                }
            )
            .Build();

        var settings = IgniteServerSettings.Load(configuration);

        Assert.Equal([value,], settings.ClusterEndpoints);
        Assert.Equal(["ENV",], settings.SqlSchemas);
    }

    [Fact]
    public void Load_ClusterEndpoints_CommaSeparated() => Assert.Equal(
        ["a:47500", "b:47500",],
        Load(
                new Dictionary<string, string?>
                {
                    ["CLUSTER_ENDPOINTS"] = "a:47500, b:47500",
                }
            )
            .ClusterEndpoints
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpringConfigPlaceholders_CoverTheTemplates_AndRestIsOffByDefault(bool restEnabled)
    {
        var settings = Load(
            new Dictionary<string, string?>
            {
                ["REST_ENABLED"] = restEnabled ? "true" : null,
            }
        );
        var configDirectory = Path.Combine(AppContext.BaseDirectory, "config");

        foreach (var template in new[]
                 {
                     "spring-config.xml", "spring-config-client-with-ssl.xml",
                 })
        {
            var rendered = SpringConfigRenderer.Render(
                File.ReadAllText(Path.Combine(configDirectory, template)),
                settings.ToSpringConfigPlaceholders(),
                settings.ToSpringConfigXmlPlaceholders()
            );
            foreach (var placeholder in settings.ToSpringConfigPlaceholders()
                         .Keys.Concat(settings.ToSpringConfigXmlPlaceholders().Keys))
            {
                Assert.DoesNotContain(placeholder, rendered);
            }

            // a {{NAME}} of the template without a value in ToSpringConfigPlaceholders
            Assert.DoesNotContain("{{", rendered);

            // no connector configuration: neither the REST API nor the TCP binary protocol
            Assert.Equal(
                !restEnabled,
                rendered.Contains("<property name=\"connectorConfiguration\"><null/></property>")
            );
            Assert.DoesNotContain("OpenCensus", rendered);
            XDocument.Parse(rendered);
        }
    }

    [Fact]
    public void Defaults_SecureAndObservable()
    {
        var settings = Load([]);

        Assert.False(settings.PeerAssemblyLoading);
        Assert.False(settings.RestEnabled);
        Assert.True(settings.ManagementEnabled);
        Assert.Equal(9464, settings.ManagementPort);
        Assert.True(settings.EnableOffHeapMetrics);
        Assert.True(settings.EnableAuthentication);
    }

    [Theory]
    [InlineData("30", "30")]
    [InlineData("0", "30")]
    [InlineData("10", "abc")]
    public void Validate_GracefulStopMustEndBeforeTheShutdownTimeout(string gracefulStopTimeout, string shutdownTimeout)
    {
        IgniteServerSettings settings;
        try
        {
            settings = Load(
                new Dictionary<string, string?>
                {
                    ["GRACEFUL_STOP_TIMEOUT_IN_SECONDS"] = gracefulStopTimeout,
                    ["SHUTDOWN_TIMEOUT_IN_SECONDS"] = shutdownTimeout,
                }
            );
        }
        catch (InvalidOperationException)
        {
            // not a number: the binder rejects it, which also stops the server (exit code 1)
            return;
        }

        Assert.Contains(
            settings.Validate(),
            error => error.StartsWith("GRACEFUL_STOP_TIMEOUT_IN_SECONDS", StringComparison.Ordinal)
        );
    }

    [Theory]
    [InlineData("LOG_ONLY", WalMode.LogOnly)]
    [InlineData("logonly", WalMode.LogOnly)]
    [InlineData("Fsync", WalMode.Fsync)]
    public void WalMode_AcceptsBothNamingStyles(string value, WalMode expected) =>
        Assert.Equal(expected, IgniteServerSettings.ParseEnum<WalMode>(value));

    [Fact]
    public void Validate_JavaChartSettings()
    {
        var errors = Load(
                new Dictionary<string, string?>
                {
                    ["WAL_MODE"] = "sometimes",
                    ["JVM_G1_HEAP_REGION_MB"] = "3",
                    ["PUBLIC_THREAD_POOL_SIZE"] = "0",
                    ["CLIENT_FAILURE_DETECTION_TIMEOUT_MS"] = "30000",
                    ["METRICS_UPDATE_FREQUENCY_IN_SECONDS"] = "60",
                }
            )
            .Validate();

        Assert.Contains(errors, error => error.StartsWith("WAL_MODE", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("JVM_G1_HEAP_REGION_MB", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("PUBLIC_THREAD_POOL_SIZE", StringComparison.Ordinal));
        Assert.Contains(
            errors,
            error => error.StartsWith("CLIENT_FAILURE_DETECTION_TIMEOUT_MS", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void ClientSsl_WithoutClientAuth_NeedsNoClientCertificate()
    {
        var settings = Load(
            new Dictionary<string, string?>
            {
                ["USE_CLIENT_SSL"] = "true",
                ["SSL_CLIENT_AUTH"] = "false",
                ["SSL_KEY_STORE_FILE_PATH"] = "/certs/server.p12",
                ["SSL_TRUST_STORE_FILE_PATH"] = "/certs/trust.p12",
                ["SSL_KEY_STORE_TYPE"] = "PKCS12",
            }
        );

        Assert.Empty(settings.Validate());
        Assert.Equal("false", settings.ToSpringConfigPlaceholders()["SSL_CLIENT_AUTH"]);
        Assert.Equal("PKCS12", settings.SslKeyStoreType);
    }

    [Fact]
    public void UnsetJavaChartSettings_KeepTheDefaults()
    {
        var defaults = IgniteConfigurationFactory.CreateDefault();
        var configuration = IgniteConfigurationFactory.Create(Load([]), Logger.None, LocalAddress.Resolve);

        Assert.Equal(defaults.NetworkTimeout, configuration.NetworkTimeout);
        Assert.Equal(defaults.PublicThreadPoolSize, configuration.PublicThreadPoolSize);
        Assert.Equal(defaults.DataStorageConfiguration.WalPath, configuration.DataStorageConfiguration.WalPath);
        Assert.Equal(defaults.DataStorageConfiguration.WalMode, configuration.DataStorageConfiguration.WalMode);
        Assert.Equal([.. defaults.JvmOptions, "-Xrs",], configuration.JvmOptions);
    }

    [Fact]
    public void RebalanceReadiness_DefaultsAndValidation()
    {
        var settings = Load([]);

        Assert.True(settings.ReadyWhenRebalanced);
        Assert.Equal(600, settings.RebalanceReadinessMaxWaitInSeconds);
        Assert.Contains(
            Load(
                    new Dictionary<string, string?>
                    {
                        ["REBALANCE_READINESS_MAX_WAIT_IN_SECONDS"] = "0",
                    }
                )
                .Validate(),
            error => error.StartsWith("REBALANCE_READINESS_MAX_WAIT_IN_SECONDS", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void SpringFragments_GoInsideTheTemplates()
    {
        var directory = Directory.CreateTempSubdirectory("spring-fragments");
        try
        {
            var beans = Path.Combine(directory.FullName, "beans.xml");
            var properties = Path.Combine(directory.FullName, "properties.xml");
            File.WriteAllText(beans, "<bean id=\"extra\" class=\"java.lang.String\"/>");
            File.WriteAllText(
                properties,
                "<property name=\"cacheConfiguration\"><list><bean class=\"org.apache.ignite.configuration.CacheConfiguration\"><property name=\"name\" value=\"fromSpring\"/></bean></list></property>"
            );
            var settings = Load(
                new Dictionary<string, string?>
                {
                    ["SPRING_EXTRA_BEANS_FILE"] = beans,
                    ["SPRING_EXTRA_PROPERTIES_FILE"] = properties,
                    ["PEER_CLASS_LOADING_ENABLED"] = "true",
                }
            );

            Assert.Empty(settings.Validate());
            var rendered = XDocument.Parse(
                SpringConfigRenderer.Render(
                    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", "spring-config.xml")),
                    settings.ToSpringConfigPlaceholders(),
                    settings.ToSpringConfigXmlPlaceholders()
                )
            );
            XNamespace ns = "http://www.springframework.org/schema/beans";
            var root = rendered.Root!;
            var igniteBean = root.Elements(ns + "bean")
                .Single(bean =>
                    (string?)bean.Attribute("class") == "org.apache.ignite.configuration.IgniteConfiguration"
                );
            Assert.Contains(root.Elements(ns + "bean"), bean => (string?)bean.Attribute("id") == "extra");
            Assert.Contains(
                igniteBean.Elements(ns + "property"),
                property => (string?)property.Attribute("name") == "cacheConfiguration"
            );
            Assert.Contains(
                igniteBean.Elements(ns + "property"),
                property => (string?)property.Attribute("name") == "peerClassLoadingEnabled"
            );
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void SpringFragments_MustExist() => Assert.Contains(
        Load(
                new Dictionary<string, string?>
                {
                    ["SPRING_EXTRA_PROPERTIES_FILE"] = "/nonexistent/properties.xml",
                }
            )
            .Validate(),
        error => error.StartsWith("SPRING_EXTRA_PROPERTIES_FILE", StringComparison.Ordinal)
    );

    [Theory]
    [InlineData("true", true)]
    // without USE_CLIENT_SSL, the SSL passwords configure the node from .NET, not in the Spring configuration
    [InlineData("false", false)]
    public void SpringExpressions_AreRejectedInTheSpringConfiguration(string useClientSsl, bool rejected)
    {
        var errors = Load(
                new Dictionary<string, string?>
                {
                    ["USE_SSL"] = "true",
                    ["USE_CLIENT_SSL"] = useClientSsl,
                    ["SSL_CLIENT_AUTH"] = "false",
                    ["SSL_KEY_STORE_FILE_PATH"] = "server.jks",
                    ["SSL_TRUST_STORE_FILE_PATH"] = "trust.jks",
                    ["SSL_KEY_STORE_PASSWORD"] = "pa#{T(java.lang.System).exit(1)}ss",
                }
            )
            .Validate();

        Assert.Equal(
            rejected,
            errors.Any(error => error.StartsWith("SSL_KEY_STORE_PASSWORD must not contain", StringComparison.Ordinal))
        );
    }

    [Fact]
    public void MalformedSpringConfiguration_FailsWithAClearMessage()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "<beans><bean></beans>");

            var ex = Assert.Throws<InvalidOperationException>(() => IgniteServerHostedService.EnsureWellFormed(file));

            Assert.Contains("SPRING_EXTRA_PROPERTIES_FILE", ex.Message);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("RANDOM_2_LRU", DataPageEvictionMode.Random2Lru)]
    [InlineData("randomlru", DataPageEvictionMode.RandomLru)]
    [InlineData("Disabled", DataPageEvictionMode.Disabled)]
    public void PageEvictionMode_AcceptsBothNamingStyles(string value, DataPageEvictionMode expected) => Assert.Equal(
        expected,
        IgniteServerSettings.ParseEnum<DataPageEvictionMode>(value)
    );

    [Fact]
    public void SqlSchemas_ListOrCommaSeparated()
    {
        Assert.Equal(
            ["A", "B",],
            Load(
                    new Dictionary<string, string?>
                    {
                        ["SQL_SCHEMAS"] = "A, B",
                    }
                )
                .SqlSchemas
        );
        Assert.Equal(
            ["C",],
            Load(
                    new Dictionary<string, string?>
                    {
                        ["SQL_SCHEMAS:0"] = "C",
                    }
                )
                .SqlSchemas
        );

        var configuration = IgniteConfigurationFactory.Create(
            Load(
                new Dictionary<string, string?>
                {
                    ["SQL_SCHEMAS"] = "A, B",
                }
            ),
            Logger.None,
            LocalAddress.Resolve
        );

        Assert.Equal(["A", "B",], configuration.SqlSchemas);
    }

    [Fact]
    public void BaselineAutoAdjustTimeout_DependsOnPersistence_UnlessSet()
    {
        var settings = Load([]);

        Assert.Null(settings.BaselineAutoAdjustTimeoutInSeconds);
        // a restarted persistent node returns with its data: its data is not moved away (and back) meanwhile
        Assert.Equal(TimeSpan.FromMinutes(5), settings.GetBaselineAutoAdjustTimeout(true));
        Assert.Equal(TimeSpan.FromSeconds(30), settings.GetBaselineAutoAdjustTimeout(false));
        // an empty value (the Helm chart's default) is not set
        Assert.Null(
            Load(
                    new Dictionary<string, string?>
                    {
                        ["BASELINE_AUTO_ADJUST_TIMEOUT_IN_SECONDS"] = "",
                    }
                )
                .BaselineAutoAdjustTimeoutInSeconds
        );
        Assert.Equal(
            TimeSpan.Zero,
            Load(
                    new Dictionary<string, string?>
                    {
                        ["BASELINE_AUTO_ADJUST_TIMEOUT_IN_SECONDS"] = "0",
                    }
                )
                .GetBaselineAutoAdjustTimeout(true)
        );
    }

    [Fact]
    public void SqlQueryHistorySize_IsOptional_AndNotNegative()
    {
        Assert.Null(Load([]).SqlQueryHistorySize);
        Assert.Equal(
            0,
            Load(
                    new Dictionary<string, string?>
                    {
                        ["SQL_QUERY_HISTORY_SIZE"] = "0",
                    }
                )
                .SqlQueryHistorySize
        );
        Assert.Contains(
            Load(
                    new Dictionary<string, string?>
                    {
                        ["SQL_QUERY_HISTORY_SIZE"] = "-1",
                    }
                )
                .Validate(),
            error => error.StartsWith("SQL_QUERY_HISTORY_SIZE", StringComparison.Ordinal)
        );
    }
}
