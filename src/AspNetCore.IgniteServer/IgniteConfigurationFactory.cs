using Apache.Ignite.Core;
using Apache.Ignite.Core.Communication.Tcp;
using Apache.Ignite.Core.Configuration;
using Apache.Ignite.Core.Deployment;
using Apache.Ignite.Core.Events;
using Apache.Ignite.Core.Ssl;
using AspNetCore.IgniteServer.Utils;
using Serilog;
using System;
using System.IO;

namespace AspNetCore.IgniteServer;

/// <summary>Creates the node configuration: the default one (or a -ConfigFile), adjusted by the runner options.</summary>
internal static class IgniteConfigurationFactory
{
    /// <summary>Lets the .NET host, not the JVM, handle SIGTERM/SIGINT (a JVM shutdown hook would stop the node behind the host's back).</summary>
    internal const string ReduceSignalUsageJvmOption = "-Xrs";

    private const int WalSegmentSize = 256 * 1024 * 1024;

    public static IgniteConfiguration Create(IgniteServerRunnerOptions options, ILogger logger)
    {
        var igniteConfiguration = string.IsNullOrWhiteSpace(options.ConfigurationFile) ? CreateDefault()
            : IgniteConfiguration.FromXml(File.ReadAllText(options.ConfigurationFile));
        if (!string.IsNullOrWhiteSpace(options.SpringConfigPath))
        {
            igniteConfiguration.SpringConfigUrl = options.SpringConfigPath;
        }

        if (igniteConfiguration.JvmOptions?.Contains(ReduceSignalUsageJvmOption) != true)
        {
            igniteConfiguration.JvmOptions = [..igniteConfiguration.JvmOptions ?? [], ReduceSignalUsageJvmOption,];
        }

        // loading .NET assemblies sent by other nodes runs their code on this node: opt-in only
        igniteConfiguration.PeerAssemblyLoadingMode = options.PeerAssemblyLoading ? PeerAssemblyLoadingMode.CurrentAppDomain
            : PeerAssemblyLoadingMode.Disabled;
        igniteConfiguration.MetricsExpireTime = options.MetricsExpireTime;
        igniteConfiguration.MetricsLogFrequency = options.MetricsLogFrequency;
        igniteConfiguration.MetricsUpdateFrequency = options.MetricsUpdateFrequency;
        if (options.AuthenticationEnabled)
        {
            igniteConfiguration.AuthenticationEnabled = true;
            SetPersistence(igniteConfiguration, true);
        }

        if (options.UseSsl)
        {
            igniteConfiguration.SslContextFactory = new SslContextFactory
            {
                KeyStoreFilePath = options.SslKeyStoreFilePath,
                KeyStorePassword = options.SslKeyStorePassword,
                TrustStoreFilePath = options.SslTrustStoreFilePath,
                TrustStorePassword = options.SslTrustStorePassword,
                Protocol = options.SslProtocol,
            };
        }

        igniteConfiguration.Logger = new SerilogIgniteLogger(logger);
        return igniteConfiguration;
    }

    public static IgniteConfiguration CreateDefault() => new()
    {
        AutoGenerateIgniteInstanceName = true,
        JvmOptions =
        [
                "-Djava.net.preferIPv4Stack=true", "-DIGNITE_QUIET=false", "-DIGNITE_WAL_MMAP=false",
                "-XX:MaxMetaspaceSize=256m", "-XX:ReservedCodeCacheSize=240m", "-XX:+AlwaysPreTouch",
                "-XX:+UseG1GC", "-XX:+DisableExplicitGC",
                "--add-opens=java.base/jdk.internal.access=ALL-UNNAMED",
                "--add-opens=java.base/jdk.internal.misc=ALL-UNNAMED", "--add-opens=java.base/sun.nio.ch=ALL-UNNAMED",
                "--add-opens=java.base/sun.util.calendar=ALL-UNNAMED",
                "--add-opens=java.management/com.sun.jmx.mbeanserver=ALL-UNNAMED",
                "--add-opens=jdk.internal.jvmstat/sun.jvmstat.monitor=ALL-UNNAMED",
                "--add-opens=java.base/sun.reflect.generics.reflectiveObjects=ALL-UNNAMED",
                "--add-opens=jdk.management/com.sun.management.internal=ALL-UNNAMED",
                "--add-opens=java.base/java.io=ALL-UNNAMED", "--add-opens=java.base/java.nio=ALL-UNNAMED",
                "--add-opens=java.base/java.net=ALL-UNNAMED", "--add-opens=java.base/java.util=ALL-UNNAMED",
                "--add-opens=java.base/java.util.concurrent=ALL-UNNAMED",
                "--add-opens=java.base/java.util.concurrent.locks=ALL-UNNAMED",
                "--add-opens=java.base/java.util.concurrent.atomic=ALL-UNNAMED",
                "--add-opens=java.base/java.lang=ALL-UNNAMED", "--add-opens=java.base/java.lang.invoke=ALL-UNNAMED",
                "--add-opens=java.base/java.math=ALL-UNNAMED", "--add-opens=java.sql/java.sql=ALL-UNNAMED",
                "--add-opens=java.base/java.lang.reflect=ALL-UNNAMED", "--add-opens=java.base/java.time=ALL-UNNAMED",
                "--add-opens=java.base/java.text=ALL-UNNAMED", "--add-opens=java.management/sun.management=ALL-UNNAMED",
                "--add-opens=java.desktop/java.awt.font=ALL-UNNAMED",
        ],
        DataStorageConfiguration = new DataStorageConfiguration
        {
            WalSegmentSize = WalSegmentSize,
        },
        WorkDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "work"),
        // Ignite.NET sends every timeout of an SPI it configures, so Ignite ignores FailureDetectionTimeout for this
        // SPI (and for a TcpDiscoverySpi configured from .NET, e.g., by -Cluster), logging "Failure detection timeout
        // will be ignored": these timeouts govern instead
        CommunicationSpi = new TcpCommunicationSpi
        {
            MessageQueueLimit = 2048,
            SocketWriteTimeout = 5000,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        },
        // governs the discovery of the Spring configuration (Kubernetes)
        FailureDetectionTimeout = TimeSpan.FromSeconds(30),
        ClientFailureDetectionTimeout = TimeSpan.FromSeconds(60),
        NetworkTimeout = TimeSpan.FromSeconds(10),
        IncludedEventTypes =
        [
            EventType.NodeJoined, EventType.NodeLeft, EventType.NodeFailed, EventType.CacheRebalancePartDataLost,
        ],
    };

    public static void SetOffHeapMemoryLimit(IgniteConfiguration igniteConfiguration, int megabytes, bool metricsEnabled)
    {
        var region = GetDefaultDataRegion(igniteConfiguration);
        region.MaxSize = (long)megabytes * 1024 * 1024;
        region.MetricsEnabled = metricsEnabled;
        igniteConfiguration.DataStorageConfiguration.MetricsEnabled = metricsEnabled;
    }

    /// <summary>
    ///     In-memory regions evict pages when full (Random2Lru); persistent regions write them to disk instead, and Ignite
    ///     warns that an eviction mode has no effect there.
    /// </summary>
    public static void SetPageEvictionMode(IgniteConfiguration igniteConfiguration)
    {
        if (igniteConfiguration.DataStorageConfiguration?.DefaultDataRegionConfiguration is { } region)
        {
            region.PageEvictionMode = region.PersistenceEnabled ? DataPageEvictionMode.Disabled
                : DataPageEvictionMode.Random2Lru;
        }
    }

    public static void SetPersistence(IgniteConfiguration igniteConfiguration, bool enabled) =>
        GetDefaultDataRegion(igniteConfiguration).PersistenceEnabled = enabled;

    private static DataRegionConfiguration GetDefaultDataRegion(IgniteConfiguration igniteConfiguration)
    {
        igniteConfiguration.DataStorageConfiguration ??= new DataStorageConfiguration
        {
            WalSegmentSize = WalSegmentSize,
        };
        return igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration ??= new DataRegionConfiguration
        {
            Name = "default",
        };
    }
}
