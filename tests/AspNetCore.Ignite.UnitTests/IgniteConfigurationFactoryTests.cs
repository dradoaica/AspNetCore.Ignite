using Apache.Ignite.Core;
using Apache.Ignite.Core.Configuration;
using Apache.Ignite.Core.Deployment;
using Apache.Ignite.Core.Events;
using AspNetCore.IgniteServer;
using Serilog;
using Serilog.Core;
using Serilog.Events;
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
            new IgniteServerRunnerOptions { ConfigurationFile = Path.Combine(ConfigDirectory, file) },
            Logger.None
        );

        Assert.Contains(configuration.JvmOptions,
            option => option.StartsWith("--add-opens=", StringComparison.Ordinal));
        Assert.Contains("-XX:MaxMetaspaceSize=256m", configuration.JvmOptions);
        Assert.Contains(EventType.CacheRebalancePartDataLost, configuration.IncludedEventTypes);
        Assert.Equal(TimeSpan.FromSeconds(30), configuration.FailureDetectionTimeout);
        // Ignite refuses to start otherwise (the bug of -ConfigFile)
        Assert.True(configuration.ClientFailureDetectionTimeout >= configuration.MetricsUpdateFrequency);
        Assert.Equal(persistent,
            configuration.DataStorageConfiguration?.DefaultDataRegionConfiguration?.PersistenceEnabled == true);
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
        Assert.Equal(["-Dx=1"], configuration.JvmOptions);
        Assert.Equal(TimeSpan.FromSeconds(30), configuration.FailureDetectionTimeout);
        Assert.Equal(PeerAssemblyLoadingMode.Disabled, configuration.PeerAssemblyLoadingMode);
        Assert.Contains(sink.Events,
            logEvent => logEvent.Level == LogEventLevel.Warning &&
                        logEvent.RenderMessage().Contains("PEER_ASSEMBLY_LOADING"));
    }

    [Fact]
    public void EnsureConsistentTimeouts_RaisesTheClientFailureDetectionTimeout()
    {
        IgniteConfiguration configuration = new()
        {
            MetricsUpdateFrequency = TimeSpan.FromSeconds(60),
            ClientFailureDetectionTimeout = TimeSpan.FromSeconds(30)
        };

        IgniteConfigurationFactory.EnsureConsistentTimeouts(configuration, Logger.None);

        Assert.Equal(TimeSpan.FromSeconds(60), configuration.ClientFailureDetectionTimeout);
    }

    [Fact]
    public void Options_SetJvmOptionsTimeoutsPoolsAndStorage()
    {
        var configuration = IgniteConfigurationFactory.Create(
            new IgniteServerRunnerOptions
            {
                JvmMaxMetaspaceMb = 512,
                JvmMaxDirectMemoryMb = 2960,
                ExtraJvmOptions = ["-Dfoo=bar", "-Xrs"],
                NetworkTimeout = TimeSpan.FromSeconds(5),
                PublicThreadPoolSize = 16,
                StripedThreadPoolSize = 8,
                StoragePath = "/storage",
                WalPath = "/wal",
                WalArchivePath = "/wal/archive",
                WalCompactionEnabled = true,
                WalMode = WalMode.LogOnly
            },
            Logger.None
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
                string.Join(Path.PathSeparator, Path.Combine(directory.FullName, "a.jar"),
                    Path.Combine(directory.FullName, "b.jar"), "/opt/x.jar"),
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
            IgniteConfigurationFactory.Create(new IgniteServerRunnerOptions(), Logger.None).SqlQueryHistorySize
        );
        Assert.Equal(
            0,
            IgniteConfigurationFactory.Create(new IgniteServerRunnerOptions { SqlQueryHistorySize = 0 }, Logger.None)
                .SqlQueryHistorySize
        );
    }

    [Fact]
    public void DefaultConfiguration_LogsSegmentation()
    {
        Assert.Contains(EventType.NodeSegmented, IgniteConfigurationFactory.CreateDefault().IncludedEventTypes);
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent)
        {
            Events.Add(logEvent);
        }
    }
}