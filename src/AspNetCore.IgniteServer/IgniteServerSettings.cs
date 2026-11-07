using Apache.Ignite.Core.Configuration;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AspNetCore.IgniteServer;

/// <summary>
///     The server settings: appsettings.json, overridden by environment variables of the same name, overridden by the
///     command line options.
/// </summary>
internal sealed partial class IgniteServerSettings
{
    [ConfigurationKeyName("CONFIG_FILE")]
    public string? ConfigFile { get; set; }

    [ConfigurationKeyName("SPI_PORT")]
    public int? SpiPort { get; set; }

    [ConfigurationKeyName("CLUSTER_ENDPOINTS")]
    public List<string> ClusterEndpoints { get; set; } = [];

    [ConfigurationKeyName("USE_TCP_DISCOVERY_STATIC_IP_FINDER")]
    public bool UseTcpDiscoveryStaticIpFinder { get; set; }

    [ConfigurationKeyName("DEFAULT_CONSISTENT_ID")]
    public string? ConsistentId { get; set; }

    /// <summary>
    ///     A Spring XML file with beans (e.g., data sources) next to the IgniteConfiguration bean, as extraBeans of the
    ///     Ignite chart.
    /// </summary>
    [ConfigurationKeyName("SPRING_EXTRA_BEANS_FILE")]
    public string? SpringExtraBeansFile { get; set; }

    /// <summary>
    ///     A Spring XML file with &lt;property&gt; elements of the IgniteConfiguration bean, as the configuration
    ///     sections of the Ignite chart. Verified on a node: userAttributes, binaryConfiguration, cacheConfiguration,
    ///     executorConfiguration, transactionConfiguration and the collision, deployment, failover and load balancing
    ///     SPIs. The .NET configuration wins for the properties it sets: sqlConfiguration (use SQL_SCHEMAS),
    ///     encryptionSpi (use -ConfigFile), the communication and discovery SPIs, data storage, the client connector.
    /// </summary>
    [ConfigurationKeyName("SPRING_EXTRA_PROPERTIES_FILE")]
    public string? SpringExtraPropertiesFile { get; set; }

    /// <summary>
    ///     SQL schemas created on start (a list, or comma-separated): the sqlSchemas of the Ignite chart's
    ///     sqlConfiguration, which the .NET configuration would override in a Spring fragment.
    /// </summary>
    [ConfigurationKeyName("SQL_SCHEMAS")]
    public List<string> SqlSchemas { get; set; } = [];

    /// <summary>Java peer class loading (Zero Deployment): the Java counterpart of PEER_ASSEMBLY_LOADING.</summary>
    [ConfigurationKeyName("PEER_CLASS_LOADING_ENABLED")]
    public bool PeerClassLoadingEnabled { get; set; }

    [ConfigurationKeyName("PEER_ASSEMBLY_LOADING")]
    public bool PeerAssemblyLoading { get; set; }

    [ConfigurationKeyName("ENABLE_AUTHENTICATION")]
    public bool EnableAuthentication { get; set; }

    [ConfigurationKeyName("IGNITE_USER_PASSWORD")]
    public string? IgniteUserPassword { get; set; }

    [ConfigurationKeyName("IGNITE_USER_PREVIOUS_PASSWORD")]
    public string? IgniteUserPreviousPassword { get; set; }

    [ConfigurationKeyName("K8S_NAMESPACE")]
    public string? K8sNamespace { get; set; }

    [ConfigurationKeyName("K8S_SERVICE_NAME")]
    public string? K8sServiceName { get; set; }

    [ConfigurationKeyName("ACTIVATION_MIN_SERVER_NODES")]
    public int ActivationMinServerNodes { get; set; } = 1;

    [ConfigurationKeyName("BASELINE_AUTO_ADJUST_TIMEOUT_IN_SECONDS")]
    public int BaselineAutoAdjustTimeoutInSeconds { get; set; } = 30;

    [ConfigurationKeyName("AUTO_RESET_LOST_PARTITIONS")]
    public bool AutoResetLostPartitions { get; set; } = true;

    // the settings below apply only when set: their defaults are those of the default configuration (or of -ConfigFile)

    /// <summary>
    ///     Binds the settings; CLUSTER_ENDPOINTS is a list (CLUSTER_ENDPOINTS__0, ...) or a comma-separated value.
    /// </summary>
    public static IgniteServerSettings Load(IConfiguration configuration)
    {
        var settings = configuration.Get<IgniteServerSettings>() ?? new IgniteServerSettings();
        if (settings.ClusterEndpoints.Count == 0 && configuration["CLUSTER_ENDPOINTS"] is { } clusterEndpoints)
        {
            settings.ClusterEndpoints = SplitList(clusterEndpoints);
        }

        if (settings.SqlSchemas.Count == 0 && configuration["SQL_SCHEMAS"] is { } sqlSchemas)
        {
            settings.SqlSchemas = SplitList(sqlSchemas);
        }

        return settings;
    }

    /// <summary>Returns the configuration errors (empty when the settings are valid).</summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> errors = [];
        if (OnHeapMemory <= 0)
        {
            errors.Add("DEFAULT_ON_HEAP_MEMORY (-OnHeap) must be positive.");
        }

        if (OffHeapMemory <= 0)
        {
            errors.Add("DEFAULT_OFF_HEAP_MEMORY (-OffHeap) must be positive.");
        }

        if (SpiPort is <= 0 or > 65535)
        {
            errors.Add("SPI_PORT (-SpiPort) must be a valid port.");
        }

        if (ActivationMinServerNodes < 1)
        {
            errors.Add("ACTIVATION_MIN_SERVER_NODES must be at least 1.");
        }

        if (BaselineAutoAdjustTimeoutInSeconds < 0)
        {
            errors.Add("BASELINE_AUTO_ADJUST_TIMEOUT_IN_SECONDS must not be negative.");
        }

        if (RebalanceReadinessMaxWaitInSeconds <= 0)
        {
            errors.Add("REBALANCE_READINESS_MAX_WAIT_IN_SECONDS must be positive.");
        }

        if (SslRestartMaxJitterInSeconds < 0)
        {
            errors.Add("SSL_RESTART_MAX_JITTER_IN_SECONDS must not be negative.");
        }

        if (ShutdownTimeoutInSeconds <= 0)
        {
            errors.Add("SHUTDOWN_TIMEOUT_IN_SECONDS must be positive.");
        }

        if (GracefulStopTimeoutInSeconds <= 0 || GracefulStopTimeoutInSeconds >= ShutdownTimeoutInSeconds)
        {
            errors.Add(
                "GRACEFUL_STOP_TIMEOUT_IN_SECONDS must be positive and less than SHUTDOWN_TIMEOUT_IN_SECONDS (the host waits for the stop)."
            );
        }

        if (ManagementPort is <= 0 or > 65535)
        {
            errors.Add("MANAGEMENT_PORT must be a valid port.");
        }

        foreach (var (key, value) in new (string, int?)[]
                 {
                     ("JVM_MAX_METASPACE_MB", JvmMaxMetaspaceMb), ("JVM_RESERVED_CODE_CACHE_MB", JvmReservedCodeCacheMb),
                     ("JVM_MAX_DIRECT_MEMORY_MB", JvmMaxDirectMemoryMb), ("JVM_G1_HEAP_REGION_MB", JvmG1HeapRegionMb),
                     ("FAILURE_DETECTION_TIMEOUT_MS", FailureDetectionTimeoutMs),
                     ("CLIENT_FAILURE_DETECTION_TIMEOUT_MS", ClientFailureDetectionTimeoutMs),
                     ("NETWORK_TIMEOUT_MS", NetworkTimeoutMs), ("PUBLIC_THREAD_POOL_SIZE", PublicThreadPoolSize),
                     ("SYSTEM_THREAD_POOL_SIZE", SystemThreadPoolSize), ("QUERY_THREAD_POOL_SIZE", QueryThreadPoolSize),
                     ("SERVICE_THREAD_POOL_SIZE", ServiceThreadPoolSize), ("STRIPED_THREAD_POOL_SIZE", StripedThreadPoolSize),
                     ("DATA_STREAMER_THREAD_POOL_SIZE", DataStreamerThreadPoolSize),
                 })
        {
            if (value <= 0)
            {
                errors.Add($"{key} must be positive.");
            }
        }

        if (JvmG1HeapRegionMb is { } regionMb && (regionMb > 32 || (regionMb & (regionMb - 1)) != 0))
        {
            errors.Add("JVM_G1_HEAP_REGION_MB must be a power of 2 from 1 to 32.");
        }

        foreach (var (key, path) in new[] { ("SPRING_EXTRA_BEANS_FILE", SpringExtraBeansFile), ("SPRING_EXTRA_PROPERTIES_FILE", SpringExtraPropertiesFile), })
        {
            if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path))
            {
                errors.Add($"{key} ({path}) does not exist.");
            }
        }

        if (PageEvictionMode != null && ParsePageEvictionMode(PageEvictionMode) == null)
        {
            errors.Add("PAGE_EVICTION_MODE must be one of Disabled, RandomLru, Random2Lru.");
        }

        if (WalMode != null && ParseWalMode(WalMode) == null)
        {
            errors.Add("WAL_MODE must be one of Fsync, LogOnly, Background, None.");
        }

        if (ClientFailureDetectionTimeoutMs is { } clientTimeout && clientTimeout < MetricsUpdateFrequencyInSeconds * 1000L)
        {
            errors.Add(
                "CLIENT_FAILURE_DETECTION_TIMEOUT_MS must not be less than METRICS_UPDATE_FREQUENCY_IN_SECONDS (Ignite requires it)."
            );
        }

        if (UseSsl || UseClientSsl)
        {
            RequireValue(errors, "SSL_KEY_STORE_FILE_PATH", SslKeyStoreFilePath);
            RequireValue(errors, "SSL_TRUST_STORE_FILE_PATH", SslTrustStoreFilePath);
        }

        // the server's own loopback client (password change) needs a certificate when client authentication is on
        if (UseClientSsl && SslClientAuth)
        {
            RequireValue(errors, "SSL_CLIENT_CERTIFICATE_PATH", SslClientCertificatePath);
        }

        return errors;
    }

    public IgniteServerRunnerOptions ToRunnerOptions(string springConfigPath) => new()
    {
        MetricsExpireTime = TimeSpan.FromHours(MetricsExpireTimeInHours),
        MetricsLogFrequency = TimeSpan.FromMinutes(MetricsLogFrequencyInMinutes),
        MetricsUpdateFrequency = TimeSpan.FromSeconds(MetricsUpdateFrequencyInSeconds),
        EnableOffHeapMetrics = EnableOffHeapMetrics,
        AuthenticationEnabled = EnableAuthentication,
        IgniteUserPassword = IgniteUserPassword,
        IgniteUserPreviousPassword = IgniteUserPreviousPassword,
        ConfigurationFile = string.IsNullOrWhiteSpace(ConfigFile) ? null : ConfigFile,
        PeerAssemblyLoading = PeerAssemblyLoading,
        SqlSchemas = SqlSchemas,
        SpringConfigPath = springConfigPath,
        ActivationMinServerNodes = ActivationMinServerNodes,
        BaselineAutoAdjustTimeout = TimeSpan.FromSeconds(BaselineAutoAdjustTimeoutInSeconds),
        AutoResetLostPartitions = AutoResetLostPartitions,
        ReadyWhenRebalanced = ReadyWhenRebalanced,
        RebalanceReadinessMaxWait = TimeSpan.FromSeconds(RebalanceReadinessMaxWaitInSeconds),
        UseSsl = UseSsl,
        SslProtocol = SslProtocol,
        SslKeyStoreType = SslKeyStoreType,
        SslTrustStoreType = SslTrustStoreType,
        SslKeyAlgorithm = SslKeyAlgorithm,
        JvmMaxMetaspaceMb = JvmMaxMetaspaceMb,
        JvmReservedCodeCacheMb = JvmReservedCodeCacheMb,
        JvmMaxDirectMemoryMb = JvmMaxDirectMemoryMb,
        JvmG1HeapRegionMb = JvmG1HeapRegionMb,
        ExtraJvmOptions = SplitOptions(ExtraJvmOpts),
        ExtraClasspath = ExtraClasspath,
        FailureDetectionTimeout = Milliseconds(FailureDetectionTimeoutMs),
        ClientFailureDetectionTimeout = Milliseconds(ClientFailureDetectionTimeoutMs),
        NetworkTimeout = Milliseconds(NetworkTimeoutMs),
        PublicThreadPoolSize = PublicThreadPoolSize,
        SystemThreadPoolSize = SystemThreadPoolSize,
        QueryThreadPoolSize = QueryThreadPoolSize,
        ServiceThreadPoolSize = ServiceThreadPoolSize,
        StripedThreadPoolSize = StripedThreadPoolSize,
        DataStreamerThreadPoolSize = DataStreamerThreadPoolSize,
        StoragePath = Blank(StoragePath),
        WalPath = Blank(WalPath),
        WalArchivePath = Blank(WalArchivePath),
        WalCompactionEnabled = WalCompactionEnabled,
        WalMode = WalMode == null ? null : ParseWalMode(WalMode),
        PageEvictionMode = PageEvictionMode == null ? null : ParsePageEvictionMode(PageEvictionMode),
        SslKeyStoreFilePath = SslKeyStoreFilePath,
        SslKeyStorePassword = SslKeyStorePassword,
        SslTrustStoreFilePath = SslTrustStoreFilePath,
        SslTrustStorePassword = SslTrustStorePassword,
        UseClientSsl = UseClientSsl,
        SslClientCertificatePath = SslClientCertificatePath,
        SslClientCertificatePassword = SslClientCertificatePassword,
    };

    /// <summary>Values for the placeholders of the Spring configuration templates.</summary>
    public IReadOnlyDictionary<string, string?> ToSpringConfigPlaceholders() => new Dictionary<string, string?>
    {
        ["K8S_NAMESPACE"] = K8sNamespace,
        ["K8S_SERVICE_NAME"] = K8sServiceName,
        ["SSL_PROTOCOL"] = SslProtocol,
        ["SSL_KEY_STORE_FILE_PATH"] = SslKeyStoreFilePath,
        ["SSL_KEY_STORE_PASSWORD"] = SslKeyStorePassword,
        ["SSL_TRUST_STORE_FILE_PATH"] = SslTrustStoreFilePath,
        ["SSL_TRUST_STORE_PASSWORD"] = SslTrustStorePassword,
        ["SSL_KEY_STORE_TYPE"] = SslKeyStoreType,
        ["SSL_TRUST_STORE_TYPE"] = SslTrustStoreType,
        ["SSL_KEY_ALGORITHM"] = SslKeyAlgorithm,
        ["SSL_CLIENT_AUTH"] = SslClientAuth ? "true" : "false",
    };

    /// <summary>XML elements inserted verbatim (not escaped) at the placeholders of the Spring configuration templates.</summary>
    public IReadOnlyDictionary<string, string?> ToSpringConfigXmlPlaceholders() => new Dictionary<string, string?>
    {
        // without a connector configuration Ignite starts neither the REST API (Jetty) nor the TCP binary protocol
        [ConnectorConfigurationPlaceholder] = RestEnabled ? null
            : "<property name=\"connectorConfiguration\"><null/></property>",
        [ExtraBeansPlaceholder] = ReadFile(SpringExtraBeansFile),
        [ExtraPropertiesPlaceholder] = (PeerClassLoadingEnabled ? "<property name=\"peerClassLoadingEnabled\" value=\"true\"/>\n" : "") +
                                       ReadFile(SpringExtraPropertiesFile),
    };

    internal const string ConnectorConfigurationPlaceholder = "<!-- CONNECTOR_CONFIGURATION -->";
    internal const string ExtraBeansPlaceholder = "<!-- EXTRA_BEANS -->";
    internal const string ExtraPropertiesPlaceholder = "<!-- EXTRA_PROPERTIES -->";

    private static List<string> SplitList(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string ReadFile(string? path) => string.IsNullOrWhiteSpace(path) ? "" : File.ReadAllText(path);

    internal static DataPageEvictionMode? ParsePageEvictionMode(string value) =>
        Enum.TryParse<DataPageEvictionMode>(value.Replace("_", string.Empty, StringComparison.Ordinal), true, out var mode) &&
        Enum.IsDefined(mode) ? mode : null;

    internal static WalMode? ParseWalMode(string value) =>
        Enum.TryParse<WalMode>(value.Replace("_", string.Empty, StringComparison.Ordinal), true, out var walMode) &&
        Enum.IsDefined(walMode) ? walMode : null;

    private static string[] SplitOptions(string? value) =>
        value?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    private static TimeSpan? Milliseconds(int? value) => value.HasValue ? TimeSpan.FromMilliseconds(value.Value) : null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static void RequireValue(List<string> errors, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{key} must be set when SSL is enabled.");
        }
    }
}
