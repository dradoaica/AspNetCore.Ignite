using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AspNetCore.IgniteServer;

/// <summary>
///     The server settings: appsettings.json, overridden by environment variables of the same name, overridden by the
///     command line options.
/// </summary>
internal sealed class IgniteServerSettings
{
    [ConfigurationKeyName("CONFIG_FILE")]
    public string? ConfigFile { get; set; }

    [ConfigurationKeyName("DEFAULT_ON_HEAP_MEMORY")]
    public int OnHeapMemory { get; set; } = 1024;

    [ConfigurationKeyName("DEFAULT_OFF_HEAP_MEMORY")]
    public int OffHeapMemory { get; set; } = 2048;

    [ConfigurationKeyName("SPI_PORT")]
    public int? SpiPort { get; set; }

    [ConfigurationKeyName("CLUSTER_ENDPOINTS")]
    public List<string> ClusterEndpoints { get; set; } = [];

    [ConfigurationKeyName("USE_TCP_DISCOVERY_STATIC_IP_FINDER")]
    public bool UseTcpDiscoveryStaticIpFinder { get; set; }

    [ConfigurationKeyName("DEFAULT_CONSISTENT_ID")]
    public string? ConsistentId { get; set; }

    [ConfigurationKeyName("PERSISTENCE_ENABLED")]
    public bool PersistenceEnabled { get; set; }

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

    [ConfigurationKeyName("USE_SSL")]
    public bool UseSsl { get; set; }

    [ConfigurationKeyName("SSL_PROTOCOL")]
    public string SslProtocol { get; set; } = "TLS";

    [ConfigurationKeyName("SSL_KEY_STORE_FILE_PATH")]
    public string? SslKeyStoreFilePath { get; set; }

    [ConfigurationKeyName("SSL_KEY_STORE_PASSWORD")]
    public string? SslKeyStorePassword { get; set; }

    [ConfigurationKeyName("SSL_TRUST_STORE_FILE_PATH")]
    public string? SslTrustStoreFilePath { get; set; }

    [ConfigurationKeyName("SSL_TRUST_STORE_PASSWORD")]
    public string? SslTrustStorePassword { get; set; }

    [ConfigurationKeyName("USE_CLIENT_SSL")]
    public bool UseClientSsl { get; set; }

    [ConfigurationKeyName("SSL_CLIENT_CERTIFICATE_PATH")]
    public string? SslClientCertificatePath { get; set; }

    [ConfigurationKeyName("SSL_CLIENT_CERTIFICATE_PASSWORD")]
    public string? SslClientCertificatePassword { get; set; }

    [ConfigurationKeyName("RESTART_ON_SSL_FILE_CHANGE")]
    public bool RestartOnSslFileChange { get; set; } = true;

    [ConfigurationKeyName("SSL_RESTART_MAX_JITTER_IN_SECONDS")]
    public int SslRestartMaxJitterInSeconds { get; set; } = 60;

    [ConfigurationKeyName("METRICS_EXPIRE_TIME_IN_HOURS")]
    public int MetricsExpireTimeInHours { get; set; } = 24;

    [ConfigurationKeyName("METRICS_LOG_FREQUENCY_IN_MINUTES")]
    public int MetricsLogFrequencyInMinutes { get; set; } = 5;

    [ConfigurationKeyName("METRICS_UPDATE_FREQUENCY_IN_SECONDS")]
    public int MetricsUpdateFrequencyInSeconds { get; set; } = 60;

    [ConfigurationKeyName("ENABLE_OFF_HEAP_METRICS")]
    public bool EnableOffHeapMetrics { get; set; }

    [ConfigurationKeyName("SHUTDOWN_TIMEOUT_IN_SECONDS")]
    public int ShutdownTimeoutInSeconds { get; set; } = 30;

    /// <summary>
    ///     Binds the settings; CLUSTER_ENDPOINTS is a list (CLUSTER_ENDPOINTS__0, ...) or a comma-separated value.
    /// </summary>
    public static IgniteServerSettings Load(IConfiguration configuration)
    {
        var settings = configuration.Get<IgniteServerSettings>() ?? new IgniteServerSettings();
        if (settings.ClusterEndpoints.Count == 0 && configuration["CLUSTER_ENDPOINTS"] is { } clusterEndpoints)
        {
            settings.ClusterEndpoints = clusterEndpoints.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                )
                .ToList();
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

        if (SslRestartMaxJitterInSeconds < 0)
        {
            errors.Add("SSL_RESTART_MAX_JITTER_IN_SECONDS must not be negative.");
        }

        if (ShutdownTimeoutInSeconds <= 0)
        {
            errors.Add("SHUTDOWN_TIMEOUT_IN_SECONDS must be positive.");
        }

        if (UseSsl || UseClientSsl)
        {
            RequireValue(errors, "SSL_KEY_STORE_FILE_PATH", SslKeyStoreFilePath);
            RequireValue(errors, "SSL_TRUST_STORE_FILE_PATH", SslTrustStoreFilePath);
        }

        if (UseClientSsl)
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
        SpringConfigPath = springConfigPath,
        ActivationMinServerNodes = ActivationMinServerNodes,
        BaselineAutoAdjustTimeout = TimeSpan.FromSeconds(BaselineAutoAdjustTimeoutInSeconds),
        AutoResetLostPartitions = AutoResetLostPartitions,
        UseSsl = UseSsl,
        SslProtocol = SslProtocol,
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
        ["OPEN_CENSUS_METRIC_EXPORTER_SPI_PERIOD"] =
            ((long)TimeSpan.FromSeconds(MetricsUpdateFrequencyInSeconds).TotalMilliseconds).ToString(
                System.Globalization.CultureInfo.InvariantCulture
            ),
    };

    private static void RequireValue(List<string> errors, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{key} must be set when SSL is enabled.");
        }
    }
}
