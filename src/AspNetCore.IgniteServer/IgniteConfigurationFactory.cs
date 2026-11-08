using Apache.Ignite.Core;
using Apache.Ignite.Core.Communication.Tcp;
using Apache.Ignite.Core.Configuration;
using Apache.Ignite.Core.Deployment;
using Apache.Ignite.Core.Events;
using Apache.Ignite.Core.Ssl;
using AspNetCore.IgniteServer.Utils;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace AspNetCore.IgniteServer;

/// <summary>Creates the node configuration: the default one (or a -ConfigFile), adjusted by the runner options.</summary>
internal static class IgniteConfigurationFactory
{
    /// <summary>
    ///     Lets the .NET host, not the JVM, handle SIGTERM/SIGINT (a JVM shutdown hook would stop the node behind the
    ///     host's back).
    /// </summary>
    internal const string ReduceSignalUsageJvmOption = "-Xrs";

    private const int WalSegmentSize = 256 * 1024 * 1024;

    public static IgniteConfiguration Create(IgniteServerRunnerOptions options, ILogger logger)
    {
        var igniteConfiguration = CreateDefault();
        if (!string.IsNullOrWhiteSpace(options.ConfigurationFile))
        {
            ApplyConfigurationFile(igniteConfiguration, File.ReadAllText(options.ConfigurationFile), logger);
        }

        if (!string.IsNullOrWhiteSpace(options.SpringConfigPath))
        {
            igniteConfiguration.SpringConfigUrl = options.SpringConfigPath;
        }

        ApplyJvmOptions(igniteConfiguration, options);
        igniteConfiguration.FailureDetectionTimeout =
            options.FailureDetectionTimeout ?? igniteConfiguration.FailureDetectionTimeout;
        igniteConfiguration.ClientFailureDetectionTimeout = options.ClientFailureDetectionTimeout ??
                                                            igniteConfiguration.ClientFailureDetectionTimeout;
        igniteConfiguration.NetworkTimeout = options.NetworkTimeout ?? igniteConfiguration.NetworkTimeout;
        igniteConfiguration.PublicThreadPoolSize =
            options.PublicThreadPoolSize ?? igniteConfiguration.PublicThreadPoolSize;
        igniteConfiguration.SystemThreadPoolSize =
            options.SystemThreadPoolSize ?? igniteConfiguration.SystemThreadPoolSize;
        igniteConfiguration.QueryThreadPoolSize =
            options.QueryThreadPoolSize ?? igniteConfiguration.QueryThreadPoolSize;
        igniteConfiguration.ServiceThreadPoolSize =
            options.ServiceThreadPoolSize ?? igniteConfiguration.ServiceThreadPoolSize;
        igniteConfiguration.StripedThreadPoolSize =
            options.StripedThreadPoolSize ?? igniteConfiguration.StripedThreadPoolSize;
        igniteConfiguration.DataStreamerThreadPoolSize =
            options.DataStreamerThreadPoolSize ?? igniteConfiguration.DataStreamerThreadPoolSize;
        ApplyStorage(igniteConfiguration, options);
        if (options.SqlSchemas.Count > 0)
        {
            igniteConfiguration.SqlSchemas = [.. options.SqlSchemas,];
        }

        igniteConfiguration.SqlQueryHistorySize =
            options.SqlQueryHistorySize ?? igniteConfiguration.SqlQueryHistorySize;

        // loading .NET assemblies sent by other nodes runs their code on this node: opt-in only
        igniteConfiguration.PeerAssemblyLoadingMode = options.PeerAssemblyLoading
            ? PeerAssemblyLoadingMode.CurrentAppDomain : PeerAssemblyLoadingMode.Disabled;
        igniteConfiguration.MetricsExpireTime = options.MetricsExpireTime;
        igniteConfiguration.MetricsLogFrequency = options.MetricsLogFrequency;
        igniteConfiguration.MetricsUpdateFrequency = options.MetricsUpdateFrequency;
        EnsureConsistentTimeouts(igniteConfiguration, logger);
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
                KeyStoreType = options.SslKeyStoreType,
                TrustStoreType = options.SslTrustStoreType,
                KeyAlgorithm = options.SslKeyAlgorithm,
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
            "-XX:+UseG1GC", "-XX:MaxGCPauseMillis=200", "-XX:+UseStringDeduplication",
            "-XX:+ParallelRefProcEnabled",
            "-XX:+DisableExplicitGC", "-XX:-OmitStackTraceInFastThrow",
            // as the Ignite chart: an out-of-memory JVM exits (and the pod restarts) instead of running half-dead in
            // the process, and leaves a heap dump in /tmp
            "-XX:+ExitOnOutOfMemoryError", "-XX:+HeapDumpOnOutOfMemoryError",
            "-XX:HeapDumpPath=/tmp/heapdump.hprof",
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
            EventType.NodeJoined, EventType.NodeLeft, EventType.NodeFailed, EventType.NodeSegmented,
            EventType.CacheRebalancePartDataLost, EventType.CacheRebalanceStarted, EventType.CacheRebalanceStopped,
        ],
    };

    /// <summary>
    ///     Applies the properties the file sets (attributes and elements of its root) over
    ///     <paramref name="igniteConfiguration" />, so the server's defaults (JVM options, timeouts, event types, ...) stay
    ///     unless the file overrides them. Nested configurations are replaced as a whole (e.g., a dataStorageConfiguration
    ///     element replaces the default one).
    /// </summary>
    internal static void ApplyConfigurationFile(IgniteConfiguration igniteConfiguration, string xml, ILogger logger)
    {
        var fileConfiguration = IgniteConfiguration.FromXml(xml);
        var root = XElement.Parse(xml);
        var names = root.Attributes()
            .Where(attribute => !attribute.IsNamespaceDeclaration)
            .Select(attribute => attribute.Name.LocalName)
            .Concat(root.Elements().Select(element => element.Name.LocalName))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var property = typeof(IgniteConfiguration).GetProperty(
                name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase
            );
            if (property is not { CanRead: true, CanWrite: true, })
            {
                continue;
            }

            if (property.Name == nameof(IgniteConfiguration.PeerAssemblyLoadingMode))
            {
                logger.Warning(
                    "The configuration file sets {Property}, but PEER_ASSEMBLY_LOADING decides it.",
                    property.Name
                );
                continue;
            }

            property.SetValue(igniteConfiguration, property.GetValue(fileConfiguration));
        }
    }

    /// <summary>Ignite refuses to start when clientFailureDetectionTimeout is less than metricsUpdateFrequency.</summary>
    internal static void EnsureConsistentTimeouts(IgniteConfiguration igniteConfiguration, ILogger logger)
    {
        if (igniteConfiguration.ClientFailureDetectionTimeout >= igniteConfiguration.MetricsUpdateFrequency)
        {
            return;
        }

        logger.Warning(
            "ClientFailureDetectionTimeout ({ClientFailureDetectionTimeout}) is raised to the metrics update frequency ({MetricsUpdateFrequency}), as Ignite requires.",
            igniteConfiguration.ClientFailureDetectionTimeout,
            igniteConfiguration.MetricsUpdateFrequency
        );
        igniteConfiguration.ClientFailureDetectionTimeout = igniteConfiguration.MetricsUpdateFrequency;
    }

    private static void ApplyJvmOptions(IgniteConfiguration igniteConfiguration, IgniteServerRunnerOptions options)
    {
        List<string> jvmOptions = [.. igniteConfiguration.JvmOptions ?? [],];
        SetJvmOption(jvmOptions, "-XX:MaxMetaspaceSize=", options.JvmMaxMetaspaceMb);
        SetJvmOption(jvmOptions, "-XX:ReservedCodeCacheSize=", options.JvmReservedCodeCacheMb);
        SetJvmOption(jvmOptions, "-XX:MaxDirectMemorySize=", options.JvmMaxDirectMemoryMb);
        SetJvmOption(jvmOptions, "-XX:G1HeapRegionSize=", options.JvmG1HeapRegionMb);
        jvmOptions.AddRange(options.ExtraJvmOptions);
        if (!jvmOptions.Contains(ReduceSignalUsageJvmOption))
        {
            jvmOptions.Add(ReduceSignalUsageJvmOption);
        }

        igniteConfiguration.JvmOptions = jvmOptions;
        if (!string.IsNullOrWhiteSpace(options.ExtraClasspath))
        {
            var classpath = ExpandClasspath(options.ExtraClasspath);
            igniteConfiguration.JvmClasspath = string.IsNullOrEmpty(igniteConfiguration.JvmClasspath) ? classpath
                : $"{igniteConfiguration.JvmClasspath}{Path.PathSeparator}{classpath}";
        }
    }

    private static void SetJvmOption(List<string> jvmOptions, string prefix, int? megabytes)
    {
        if (megabytes is not { } value)
        {
            return;
        }

        jvmOptions.RemoveAll(option => option.StartsWith(prefix, StringComparison.Ordinal));
        jvmOptions.Add($"{prefix}{value}m");
    }

    /// <summary>Entries separated by ':' (or ';'); a directory stands for the *.jar files in it.</summary>
    internal static string ExpandClasspath(string classpath) => string.Join(
        Path.PathSeparator,
        classpath.Split([':', ';',], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany<string, string>(entry => Directory.Exists(entry)
                ? Directory.GetFiles(entry, "*.jar").Order(StringComparer.Ordinal) : [entry,]
            )
    );

    private static void ApplyStorage(IgniteConfiguration igniteConfiguration, IgniteServerRunnerOptions options)
    {
        if (options is
            {
                StoragePath: null, WalPath: null, WalArchivePath: null, WalCompactionEnabled: null, WalMode: null,
            })
        {
            return;
        }

        GetDefaultDataRegion(igniteConfiguration);
        var dataStorage = igniteConfiguration.DataStorageConfiguration;
        dataStorage.StoragePath = options.StoragePath ?? dataStorage.StoragePath;
        dataStorage.WalPath = options.WalPath ?? dataStorage.WalPath;
        dataStorage.WalArchivePath = options.WalArchivePath ?? dataStorage.WalArchivePath;
        dataStorage.WalCompactionEnabled = options.WalCompactionEnabled ?? dataStorage.WalCompactionEnabled;
        dataStorage.WalMode = options.WalMode ?? dataStorage.WalMode;
    }

    public static void SetOffHeapMemoryLimit(
        IgniteConfiguration igniteConfiguration,
        int megabytes,
        bool metricsEnabled
    )
    {
        var region = GetDefaultDataRegion(igniteConfiguration);
        region.MaxSize = (long)megabytes * 1024 * 1024;
        region.MetricsEnabled = metricsEnabled;
        igniteConfiguration.DataStorageConfiguration.MetricsEnabled = metricsEnabled;
    }

    /// <summary>
    ///     In-memory regions evict pages when full (Random2Lru); persistent regions write them to disk instead, and
    ///     Ignite warns that an eviction mode has no effect there.
    /// </summary>
    public static void SetPageEvictionMode(
        IgniteConfiguration igniteConfiguration,
        DataPageEvictionMode? inMemoryMode = null
    )
    {
        if (igniteConfiguration.DataStorageConfiguration?.DefaultDataRegionConfiguration is { } region)
        {
            region.PageEvictionMode = region.PersistenceEnabled ? DataPageEvictionMode.Disabled
                : inMemoryMode ?? DataPageEvictionMode.Random2Lru;
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
        return igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration ??=
            new DataRegionConfiguration
            {
                Name = "default",
            };
    }
}
