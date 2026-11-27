using Apache.Ignite.Core;
using Apache.Ignite.Core.Configuration;
using Apache.Ignite.Core.Deployment;
using Apache.Ignite.Core.Discovery.Tcp;
using Apache.Ignite.Core.Discovery.Tcp.Static;
using Apache.Ignite.Core.Events;
using AspNetCore.IgniteServer;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Reflection;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class IgniteConfigurationFactoryTests
{
    private static readonly string ConfigDirectory = Path.Combine(AppContext.BaseDirectory, "config");

    [Theory]
    [InlineData("default-config.xml", false)]
    [InlineData("persistent-config.xml", true)]
    public void ShippedConfigurationFiles_KeepTheServerDefaults(string file, bool persistent)
    {
        var configuration = IgniteConfigurationFactory.Create(
            new IgniteServerSettings
            {
                EnableAuthentication = false,
                ConfigFile = Path.Combine(ConfigDirectory, file),
            },
            Logger.None,
            LocalAddress.Resolve
        );

        Assert.Contains(
            configuration.JvmOptions,
            option => option.StartsWith("--add-opens=", StringComparison.Ordinal)
        );
        Assert.Contains("-XX:MaxMetaspaceSize=256m", configuration.JvmOptions);
        Assert.Contains(EventType.CacheRebalancePartDataLost, configuration.IncludedEventTypes);
        Assert.Equal(TimeSpan.FromSeconds(30), configuration.FailureDetectionTimeout);
        // Ignite refuses to start otherwise (the bug of -ConfigFile)
        Assert.True(configuration.ClientFailureDetectionTimeout >= configuration.MetricsUpdateFrequency);
        Assert.Equal(
            persistent,
            configuration.DataStorageConfiguration?.DefaultDataRegionConfiguration?.PersistenceEnabled == true
        );
    }

    [Fact]
    public void ConfigurationFile_OverridesOnlyWhatItSets()
    {
        var configuration = IgniteConfigurationFactory.CreateDefault();
        CollectingSink sink = new();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        IgniteConfigurationFactory.ApplyConfigurationFile(
            configuration,
            "<igniteConfiguration networkTimeout='00:00:42' peerAssemblyLoadingMode='CurrentAppDomain'><jvmOptions><string>-Dx=1</string></jvmOptions></igniteConfiguration>",
            logger
        );

        Assert.Equal(TimeSpan.FromSeconds(42), configuration.NetworkTimeout);
        Assert.Equal(["-Dx=1",], configuration.JvmOptions);
        Assert.Equal(TimeSpan.FromSeconds(30), configuration.FailureDetectionTimeout);
        Assert.Equal(PeerAssemblyLoadingMode.Disabled, configuration.PeerAssemblyLoadingMode);
        Assert.Contains(
            sink.Events,
            logEvent => logEvent.Level == LogEventLevel.Warning &&
                        logEvent.RenderMessage().Contains("PEER_ASSEMBLY_LOADING")
        );
    }

    /// <summary>
    ///     ApplyConfigurationFile copies the properties whose names the file's XML uses. This fails when Ignite.NET's XML
    ///     names and property names ever differ, which would make a -ConfigFile setting be skipped without a word.
    /// </summary>
    [Fact]
    public void ConfigurationFile_AppliesEveryScalarPropertyThatIgniteWritesToXml()
    {
        IgniteConfiguration source = new();
        List<PropertyInfo> properties = [];
        foreach (var property in typeof(IgniteConfiguration).GetProperties())
        {
            if (property is not { CanRead: true, CanWrite: true, } ||
                property.GetCustomAttribute<ObsoleteAttribute>() != null ||
                property.Name == nameof(IgniteConfiguration.PeerAssemblyLoadingMode) ||
                NonDefaultValue(property.PropertyType) is not { } value)
            {
                continue;
            }

            property.SetValue(source, value);
            properties.Add(property);
        }

        var configuration = IgniteConfigurationFactory.CreateDefault();
        IgniteConfigurationFactory.ApplyConfigurationFile(configuration, source.ToXml(), Logger.None);

        Assert.True(properties.Count > 30, $"only {properties.Count} properties were checked");
        Assert.All(
            properties,
            property => Assert.True(
                Equals(property.GetValue(source), property.GetValue(configuration)),
                $"{property.Name} was not applied from the XML"
            )
        );
    }

    private static object? NonDefaultValue(Type type) => (Nullable.GetUnderlyingType(type) ?? type) switch
    {
        var t when t == typeof(bool) => true,
        var t when t == typeof(int) => 4242,
        var t when t == typeof(long) => 4242L,
        var t when t == typeof(TimeSpan) => TimeSpan.FromSeconds(4242),
        var t when t == typeof(string) => "value-4242",
        _ => null,
    };

    [Fact]
    public void ConfigurationFile_KeepsTheJvmOptionsAndEventTypesTheServerNeeds()
    {
        var file = Path.GetTempFileName();
        try
        {
            // a file that replaces both lists
            File.WriteAllText(
                file,
                $"<igniteConfiguration><jvmOptions><string>-Dx=1</string></jvmOptions><includedEventTypes><int>{EventType.CacheObjectPut}</int></includedEventTypes></igniteConfiguration>"
            );

            var configuration = IgniteConfigurationFactory.Create(
                new IgniteServerSettings
                {
                    EnableAuthentication = false,
                    ConfigFile = file,
                },
                Logger.None,
                LocalAddress.Resolve
            );

            Assert.Contains("-Dx=1", configuration.JvmOptions);
            // without the add-opens, Ignite fails to start on Java 17
            Assert.All(
                File.ReadLines(Path.Combine(ConfigDirectory, "jvm-add-opens.args"))
                    .Where(line => line.StartsWith("--", StringComparison.Ordinal)),
                option => Assert.Contains(option, configuration.JvmOptions)
            );
            Assert.Contains(IgniteConfigurationFactory.ReduceSignalUsageJvmOption, configuration.JvmOptions);
            Assert.Contains(EventType.CacheObjectPut, configuration.IncludedEventTypes);
            // without these, readiness stops waiting for rebalancing and lost partitions are never reset
            Assert.All(
                IgniteConfigurationFactory.RequiredEventTypes,
                eventType => Assert.Contains(eventType, configuration.IncludedEventTypes)
            );
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void EnsureConsistentTimeouts_RaisesTheClientFailureDetectionTimeout()
    {
        IgniteConfiguration configuration = new()
        {
            MetricsUpdateFrequency = TimeSpan.FromSeconds(60),
            ClientFailureDetectionTimeout = TimeSpan.FromSeconds(30),
        };

        IgniteConfigurationFactory.EnsureConsistentTimeouts(configuration, Logger.None);

        Assert.Equal(TimeSpan.FromSeconds(60), configuration.ClientFailureDetectionTimeout);
    }

    [Fact]
    public void Options_SetJvmOptionsTimeoutsPoolsAndStorage()
    {
        var configuration = IgniteConfigurationFactory.Create(
            new IgniteServerSettings
            {
                JvmMaxMetaspaceMb = 512,
                JvmMaxDirectMemoryMb = 2960,
                ExtraJvmOpts = "-Dfoo=bar -Xrs",
                NetworkTimeoutMs = 5000,
                PublicThreadPoolSize = 16,
                StripedThreadPoolSize = 8,
                StoragePath = "/storage",
                WalPath = "/wal",
                WalArchivePath = "/wal/archive",
                WalCompactionEnabled = true,
                WalMode = "LOG_ONLY",
            },
            Logger.None,
            LocalAddress.Resolve
        );

        Assert.Contains("-XX:MaxMetaspaceSize=512m", configuration.JvmOptions);
        Assert.DoesNotContain("-XX:MaxMetaspaceSize=256m", configuration.JvmOptions);
        Assert.Contains("-XX:MaxDirectMemorySize=2960m", configuration.JvmOptions);
        Assert.Contains("-Dfoo=bar", configuration.JvmOptions);
        Assert.Single(configuration.JvmOptions, option => option == "-Xrs");
        Assert.Equal(TimeSpan.FromSeconds(5), configuration.NetworkTimeout);
        Assert.Equal(16, configuration.PublicThreadPoolSize);
        Assert.Equal(8, configuration.StripedThreadPoolSize);
        var dataStorage = configuration.DataStorageConfiguration;
        Assert.Equal("/storage", dataStorage.StoragePath);
        Assert.Equal("/wal", dataStorage.WalPath);
        Assert.Equal("/wal/archive", dataStorage.WalArchivePath);
        Assert.True(dataStorage.WalCompactionEnabled);
        Assert.Equal(WalMode.LogOnly, dataStorage.WalMode);
    }

    [Fact]
    public void ExpandClasspath_ListsTheJarsOfDirectories()
    {
        var directory = Directory.CreateTempSubdirectory("extra-classpath");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "b.jar"), "");
            File.WriteAllText(Path.Combine(directory.FullName, "a.jar"), "");
            File.WriteAllText(Path.Combine(directory.FullName, "readme.txt"), "");

            var classpath = IgniteConfigurationFactory.ExpandClasspath($"{directory.FullName}:/opt/x.jar");

            Assert.Equal(
                string.Join(
                    Path.PathSeparator,
                    Path.Combine(directory.FullName, "a.jar"),
                    Path.Combine(directory.FullName, "b.jar"),
                    "/opt/x.jar"
                ),
                classpath
            );
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void SqlQueryHistorySize_AppliesOnlyWhenSet()
    {
        Assert.Equal(
            IgniteConfiguration.DefaultSqlQueryHistorySize,
            IgniteConfigurationFactory.Create(new IgniteServerSettings(), Logger.None, LocalAddress.Resolve)
                .SqlQueryHistorySize
        );
        Assert.Equal(
            0,
            IgniteConfigurationFactory.Create(
                    new IgniteServerSettings
                    {
                        SqlQueryHistorySize = 0,
                    },
                    Logger.None,
                    LocalAddress.Resolve
                )
                .SqlQueryHistorySize
        );
    }

    [Fact]
    public void DefaultConfiguration_LogsSegmentation() => Assert.Contains(
        EventType.NodeSegmented,
        IgniteConfigurationFactory.CreateDefault().IncludedEventTypes
    );

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
    [InlineData(null, new string[0])]
    [InlineData("  ", new string[0])]
    [InlineData(" -Xss1m   -Dfoo=bar ", new[] { "-Xss1m", "-Dfoo=bar", })]
    [InlineData("\"-Dapp.name=my app\" -Xss1m", new[] { "-Dapp.name=my app", "-Xss1m", })]
    [InlineData("-Dapp.name='my app' -Dq=\"it's\"", new[] { "-Dapp.name=my app", "-Dq=it's", })]
    [InlineData("-Dempty=\"\"", new[] { "-Dempty=", })]
    public void SplitJvmOptions_SplitsAtWhitespace_OutsideQuotes(string? options, string[] expected) =>
        Assert.Equal(expected, IgniteConfigurationFactory.SplitJvmOptions(options));

    [Fact]
    public void SplitJvmOptions_WithAnUnclosedQuote_IsNull_AndAValidationError()
    {
        Assert.Null(IgniteConfigurationFactory.SplitJvmOptions("\"-Dapp.name=my app"));
        Assert.Contains(
            "EXTRA_JVM_OPTS has an unclosed quote.",
            new IgniteServerSettings
            {
                ExtraJvmOpts = "-Dapp.name='my app",
            }.Validate()
        );
    }
}
