using Apache.Ignite.Core;
using Apache.Ignite.Core.Communication.Tcp;
using Apache.Ignite.Core.Configuration;
using Apache.Ignite.Core.Deployment;
using Apache.Ignite.Core.Discovery.Tcp;
using Apache.Ignite.Core.Discovery.Tcp.Static;
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

/// <summary>Creates the node configuration: the default one (or a -ConfigFile), adjusted by the settings.</summary>
internal static class IgniteConfigurationFactory
{
    /// <summary>
    ///     Lets the .NET host, not the JVM, handle SIGTERM/SIGINT (a JVM shutdown hook would stop the node behind the
    ///     host's back).
    /// </summary>
    internal const string ReduceSignalUsageJvmOption = "-Xrs";

    private const int DefaultDiscoveryPort = 47500;
    private const int WalSegmentSize = 256 * 1024 * 1024;

    /// <param name="settings">The server settings.</param>
    /// <param name="logger">Where the node logs.</param>
    /// <param name="getLocalIpAddress">
    ///     Resolves the address of this machine. It is called only when the node is its own static
    ///     discovery endpoint (USE_TCP_DISCOVERY_STATIC_IP_FINDER without CLUSTER_ENDPOINTS), so a failing DNS lookup does not
    ///     break nodes that do not need it.
    /// </param>
    /// <param name="springConfigPath">The rendered Spring configuration, if any.</param>
    public static IgniteConfiguration Create(
        IgniteServerSettings settings,
        ILogger logger,
        Func<string> getLocalIpAddress,
        string? springConfigPath = null
    )
    {
        var igniteConfiguration = CreateDefault();
        if (!string.IsNullOrWhiteSpace(settings.ConfigFile))
        {
            ApplyConfigurationFile(igniteConfiguration, File.ReadAllText(settings.ConfigFile), logger);
        }

        if (!string.IsNullOrWhiteSpace(springConfigPath))
        {
            igniteConfiguration.SpringConfigUrl = springConfigPath;
        }

        ApplyJvmOptions(igniteConfiguration, settings);
        ApplyTimeoutsAndThreadPools(igniteConfiguration, settings);
        igniteConfiguration.MetricsExpireTime = TimeSpan.FromHours(settings.MetricsExpireTimeInHours);
        igniteConfiguration.MetricsLogFrequency = TimeSpan.FromMinutes(settings.MetricsLogFrequencyInMinutes);
        igniteConfiguration.MetricsUpdateFrequency = TimeSpan.FromSeconds(settings.MetricsUpdateFrequencyInSeconds);
        EnsureConsistentTimeouts(igniteConfiguration, logger);
        ApplyStorage(igniteConfiguration, settings);
        ApplyDiscovery(igniteConfiguration, settings, getLocalIpAddress);
        if (!string.IsNullOrWhiteSpace(settings.ConsistentId))
        {
            igniteConfiguration.ConsistentId = settings.ConsistentId;
        }

        if (settings.SqlSchemas.Count > 0)
        {
            igniteConfiguration.SqlSchemas = [.. settings.SqlSchemas,];
        }

        igniteConfiguration.SqlQueryHistorySize =
            settings.SqlQueryHistorySize ?? igniteConfiguration.SqlQueryHistorySize;
        // Loading .NET assemblies sent by other nodes runs their code on this node, so it is opt-in.
        igniteConfiguration.PeerAssemblyLoadingMode = settings.PeerAssemblyLoading
            ? PeerAssemblyLoadingMode.CurrentAppDomain : PeerAssemblyLoadingMode.Disabled;
        if (settings.EnableAuthentication)
        {
            igniteConfiguration.AuthenticationEnabled = true;
        }

        if (settings.UseSsl)
        {
            igniteConfiguration.SslContextFactory = new SslContextFactory
            {
                KeyStoreFilePath = settings.SslKeyStoreFilePath,
                // Ignite requires both passwords, so an unset one becomes an empty one.
                KeyStorePassword = settings.SslKeyStorePassword ?? string.Empty,
                TrustStoreFilePath = settings.SslTrustStoreFilePath,
                TrustStorePassword = settings.SslTrustStorePassword ?? string.Empty,
                KeyStoreType = settings.SslKeyStoreType,
                TrustStoreType = settings.SslTrustStoreType,
                KeyAlgorithm = settings.SslKeyAlgorithm,
                Protocol = settings.SslProtocol,
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
            // As in the Ignite chart: a JVM that runs out of memory exits (and the pod restarts) instead of staying
            // half-dead in the process. It leaves a heap dump in /tmp.
            "-XX:+ExitOnOutOfMemoryError", "-XX:+HeapDumpOnOutOfMemoryError",
            "-XX:HeapDumpPath=/tmp/heapdump.hprof",
            .. File.ReadLines(Path.Combine(AppContext.BaseDirectory, "config", "jvm-add-opens.args"))
                .Where(line => line.StartsWith("--", StringComparison.Ordinal)),
        ],
        DataStorageConfiguration = new DataStorageConfiguration
        {
            WalSegmentSize = WalSegmentSize,
        },
        WorkDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "work"),
        // Ignite.NET sends every timeout of an SPI it configures. Ignite then ignores FailureDetectionTimeout for that
        // SPI and logs "Failure detection timeout will be ignored". This applies here and to a TcpDiscoverySpi
        // configured from .NET (e.g., by -Cluster): the timeouts below are the ones in effect.
        CommunicationSpi = new TcpCommunicationSpi
        {
            MessageQueueLimit = 2048,
            SocketWriteTimeout = 5000,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        },
        // These apply to the discovery of the Spring configuration (Kubernetes), which .NET does not configure.
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
    ///     <paramref name="igniteConfiguration" />. The server's defaults (JVM options, timeouts, event types, ...) stay
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
            "ClientFailureDetectionTimeout ({ClientFailureDetectionTimeout}) is raised to the metrics update " +
            "frequency ({MetricsUpdateFrequency}), as Ignite requires.",
            igniteConfiguration.ClientFailureDetectionTimeout,
            igniteConfiguration.MetricsUpdateFrequency
        );
        igniteConfiguration.ClientFailureDetectionTimeout = igniteConfiguration.MetricsUpdateFrequency;
    }

    private static void ApplyJvmOptions(IgniteConfiguration igniteConfiguration, IgniteServerSettings settings)
    {
        // The initial heap equals the maximum, as in the Ignite chart. With AlwaysPreTouch and guaranteed container
        // memory, the heap is allocated at the start and never grows at run time.
        igniteConfiguration.JvmInitialMemoryMb = settings.OnHeapMemory;
        igniteConfiguration.JvmMaxMemoryMb = settings.OnHeapMemory;
        List<string> jvmOptions = [.. igniteConfiguration.JvmOptions ?? [],];
        SetJvmOption(jvmOptions, "-XX:MaxMetaspaceSize=", settings.JvmMaxMetaspaceMb);
        SetJvmOption(jvmOptions, "-XX:ReservedCodeCacheSize=", settings.JvmReservedCodeCacheMb);
        SetJvmOption(jvmOptions, "-XX:MaxDirectMemorySize=", settings.JvmMaxDirectMemoryMb);
        SetJvmOption(jvmOptions, "-XX:G1HeapRegionSize=", settings.JvmG1HeapRegionMb);
        jvmOptions.AddRange(
            settings.ExtraJvmOpts?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ??
            []
        );
        if (!jvmOptions.Contains(ReduceSignalUsageJvmOption))
        {
            jvmOptions.Add(ReduceSignalUsageJvmOption);
        }

        igniteConfiguration.JvmOptions = jvmOptions;
        if (!string.IsNullOrWhiteSpace(settings.ExtraClasspath))
        {
            var classpath = ExpandClasspath(settings.ExtraClasspath);
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

    /// <summary>The settings that are not set keep the value of the default configuration (or of -ConfigFile).</summary>
    private static void ApplyTimeoutsAndThreadPools(
        IgniteConfiguration igniteConfiguration,
        IgniteServerSettings settings
    )
    {
        igniteConfiguration.FailureDetectionTimeout = Milliseconds(settings.FailureDetectionTimeoutMs) ??
                                                      igniteConfiguration.FailureDetectionTimeout;
        igniteConfiguration.ClientFailureDetectionTimeout = Milliseconds(settings.ClientFailureDetectionTimeoutMs) ??
                                                            igniteConfiguration.ClientFailureDetectionTimeout;
        igniteConfiguration.NetworkTimeout =
            Milliseconds(settings.NetworkTimeoutMs) ?? igniteConfiguration.NetworkTimeout;
        igniteConfiguration.PublicThreadPoolSize =
            settings.PublicThreadPoolSize ?? igniteConfiguration.PublicThreadPoolSize;
        igniteConfiguration.SystemThreadPoolSize =
            settings.SystemThreadPoolSize ?? igniteConfiguration.SystemThreadPoolSize;
        igniteConfiguration.QueryThreadPoolSize =
            settings.QueryThreadPoolSize ?? igniteConfiguration.QueryThreadPoolSize;
        igniteConfiguration.ServiceThreadPoolSize =
            settings.ServiceThreadPoolSize ?? igniteConfiguration.ServiceThreadPoolSize;
        igniteConfiguration.StripedThreadPoolSize =
            settings.StripedThreadPoolSize ?? igniteConfiguration.StripedThreadPoolSize;
        igniteConfiguration.DataStreamerThreadPoolSize =
            settings.DataStreamerThreadPoolSize ?? igniteConfiguration.DataStreamerThreadPoolSize;
    }

    private static TimeSpan? Milliseconds(int? value) => value.HasValue ? TimeSpan.FromMilliseconds(value.Value) : null;

    /// <summary>
    ///     Entries separated by ':' or ';' (only ';' on Windows, where ':' follows the drive letter); a directory stands
    ///     for the *.jar files in it.
    /// </summary>
    internal static string ExpandClasspath(string classpath) => string.Join(
        Path.PathSeparator,
        classpath.Split(
                OperatingSystem.IsWindows() ? [';',] : [':', ';',],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
            .SelectMany<string, string>(entry => Directory.Exists(entry)
                ? Directory.GetFiles(entry, "*.jar").Order(StringComparer.Ordinal) : [entry,]
            )
    );

    /// <summary>The default data region (off-heap memory, persistence, page eviction), the data and WAL storage.</summary>
    private static void ApplyStorage(IgniteConfiguration igniteConfiguration, IgniteServerSettings settings)
    {
        var region = GetDefaultDataRegion(igniteConfiguration);
        region.MaxSize = (long)settings.OffHeapMemory * 1024 * 1024;
        region.MetricsEnabled = settings.EnableOffHeapMetrics;
        // Persistence is only ever turned on here, so a -ConfigFile that enables it keeps it. Authentication needs
        // it, because Ignite stores its users on disk.
        if (settings.PersistenceEnabled || settings.EnableAuthentication)
        {
            region.PersistenceEnabled = true;
        }

        // An in-memory region evicts pages when it is full. A persistent region writes them to disk instead, and
        // Ignite warns about an eviction mode there.
        var inMemoryMode = settings.PageEvictionMode is { } mode
            ? IgniteServerSettings.ParseValidEnum<DataPageEvictionMode>("PAGE_EVICTION_MODE", mode)
            : DataPageEvictionMode.Random2Lru;
        region.PageEvictionMode = region.PersistenceEnabled ? DataPageEvictionMode.Disabled : inMemoryMode;

        var dataStorage = igniteConfiguration.DataStorageConfiguration;
        dataStorage.MetricsEnabled = settings.EnableOffHeapMetrics;
        dataStorage.StoragePath = NullIfBlank(settings.StoragePath) ?? dataStorage.StoragePath;
        dataStorage.WalPath = NullIfBlank(settings.WalPath) ?? dataStorage.WalPath;
        dataStorage.WalArchivePath = NullIfBlank(settings.WalArchivePath) ?? dataStorage.WalArchivePath;
        dataStorage.WalCompactionEnabled = settings.WalCompactionEnabled ?? dataStorage.WalCompactionEnabled;
        dataStorage.WalMode = settings.WalMode is { } walMode
            ? IgniteServerSettings.ParseValidEnum<WalMode>("WAL_MODE", walMode) : dataStorage.WalMode;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    ///     Configures static discovery from .NET, unless <see cref="IgniteServerSettings.UsesKubernetesDiscovery" />. It
    ///     then replaces the discovery of the Spring configuration (Kubernetes).
    /// </summary>
    private static void ApplyDiscovery(
        IgniteConfiguration igniteConfiguration,
        IgniteServerSettings settings,
        Func<string> getLocalIpAddress
    )
    {
        if (settings.UsesKubernetesDiscovery)
        {
            return;
        }

        // Without CLUSTER_ENDPOINTS, no other node is known, so the node is its own discovery endpoint (on its own
        // port). Other nodes join it by naming it in their CLUSTER_ENDPOINTS.
        var endpoints = settings.ClusterEndpoints.Count > 0 ? settings.ClusterEndpoints
            : settings.UseTcpDiscoveryStaticIpFinder
                ? [$"{getLocalIpAddress()}:{settings.SpiPort ?? DefaultDiscoveryPort}",] : [];

        if (igniteConfiguration.DiscoverySpi is not null and not TcpDiscoverySpi)
        {
            throw new InvalidOperationException(
                $"The discovery port and cluster endpoints need a TcpDiscoverySpi, the configuration uses " +
                $"{igniteConfiguration.DiscoverySpi.GetType().Name}."
            );
        }

        var discoverySpi = (TcpDiscoverySpi)(igniteConfiguration.DiscoverySpi ??= new TcpDiscoverySpi());
        discoverySpi.LocalPort = settings.SpiPort ?? discoverySpi.LocalPort;
        if (endpoints.Count > 0)
        {
            discoverySpi.IpFinder = new TcpDiscoveryStaticIpFinder
            {
                Endpoints = endpoints,
            };
        }
    }

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
