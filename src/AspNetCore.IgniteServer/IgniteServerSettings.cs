using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AspNetCore.IgniteServer;

/// <summary>
///     The server settings, with their defaults: overridden by appsettings.json (empty as shipped), by environment
///     variables of the same name, and by the command line options.
/// </summary>
internal sealed partial class IgniteServerSettings
{
    internal const string ConnectorConfigurationPlaceholder = "<!-- CONNECTOR_CONFIGURATION -->";
    internal const string ExtraBeansPlaceholder = "<!-- EXTRA_BEANS -->";
    internal const string ExtraPropertiesPlaceholder = "<!-- EXTRA_PROPERTIES -->";

    [ConfigurationKeyName("CONFIG_FILE")]
    public string? ConfigFile { get; set; }

    [ConfigurationKeyName("SPI_PORT")]
    public int? SpiPort { get; set; }

    [ConfigurationKeyName("CLUSTER_ENDPOINTS")]
    public List<string> ClusterEndpoints { get; set; } = [];

    [ConfigurationKeyName("USE_TCP_DISCOVERY_STATIC_IP_FINDER")]
    public bool UseTcpDiscoveryStaticIpFinder { get; set; } = true;

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
    ///     executorConfiguration, transactionConfiguration and the collision, deployment, failover and load balancing SPIs.
    ///     The .NET configuration wins for the properties it sets: sqlConfiguration (use SQL_SCHEMAS), encryptionSpi (use
    ///     -ConfigFile), the communication and discovery SPIs, data storage, the client connector.
    /// </summary>
    [ConfigurationKeyName("SPRING_EXTRA_PROPERTIES_FILE")]
    public string? SpringExtraPropertiesFile { get; set; }

    /// <summary>
    ///     SQL schemas created on start (a list, or comma-separated): the sqlSchemas of the Ignite chart's
    ///     sqlConfiguration, which the .NET configuration would override in a Spring fragment.
    /// </summary>
    [ConfigurationKeyName("SQL_SCHEMAS")]
    public List<string> SqlSchemas { get; set; } = [];

    /// <summary>
    ///     SQL queries each node keeps in SYS.SQL_QUERIES_HISTORY (Ignite's default when not set; 0 keeps none). The node
    ///     that applies a new IGNITE_USER_PASSWORD keeps its ALTER USER statement there, password included, until it restarts.
    /// </summary>
    [ConfigurationKeyName("SQL_QUERY_HISTORY_SIZE")]
    public int? SqlQueryHistorySize { get; set; }

    /// <summary>Java peer class loading (Zero Deployment): the Java counterpart of PEER_ASSEMBLY_LOADING.</summary>
    [ConfigurationKeyName("PEER_CLASS_LOADING_ENABLED")]
    public bool PeerClassLoadingEnabled { get; set; }

    [ConfigurationKeyName("PEER_ASSEMBLY_LOADING")]
    public bool PeerAssemblyLoading { get; set; }

    [ConfigurationKeyName("ENABLE_AUTHENTICATION")]
    public bool EnableAuthentication { get; set; } = true;

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

    /// <summary>When not set, 300 with persistence (a restarted node returns with its data) and 30 without.</summary>
    [ConfigurationKeyName("BASELINE_AUTO_ADJUST_TIMEOUT_IN_SECONDS")]
    public int? BaselineAutoAdjustTimeoutInSeconds { get; set; }

    [ConfigurationKeyName("AUTO_RESET_LOST_PARTITIONS")]
    public bool AutoResetLostPartitions { get; set; } = true;

    /// <summary>
    ///     No discovery is configured from .NET (no static endpoints, not even this node's own, and no discovery port),
    ///     so the nodes find each other through the Kubernetes API, as the Spring configuration says.
    /// </summary>
    internal bool UsesKubernetesDiscovery =>
        ClusterEndpoints.Count == 0 && !UseTcpDiscoveryStaticIpFinder && SpiPort is null;

    /// <summary>
    ///     Binds the settings. CLUSTER_ENDPOINTS and SQL_SCHEMAS are lists (CLUSTER_ENDPOINTS__0, ...) or
    ///     comma-separated values, taken from the source of the highest priority that sets them.
    /// </summary>
    public static IgniteServerSettings Load(IConfiguration configuration)
    {
        var settings = configuration.Get<IgniteServerSettings>() ?? new IgniteServerSettings();
        settings.ClusterEndpoints = GetList(configuration, "CLUSTER_ENDPOINTS");
        settings.SqlSchemas = GetList(configuration, "SQL_SCHEMAS");
        return settings;
    }

    /// <summary>
    ///     The list of the source of the highest priority that sets <paramref name="key" />. The configuration would
    ///     merge the lists of several sources index by index: -Cluster a with CLUSTER_ENDPOINTS__0 and __1 in the
    ///     environment would be a and the second endpoint of the environment.
    /// </summary>
    private static List<string> GetList(IConfiguration configuration, string key)
    {
        if (configuration is not IConfigurationRoot root)
        {
            // a section: its sources are not known, so its values are taken as they are merged
            return configuration.GetSection(key).Get<List<string>>() is { Count: > 0, } merged ? merged
                : configuration[key] is { } scalar ? SplitList(scalar) : [];
        }

        foreach (var provider in root.Providers.Reverse())
        {
            var indexes = provider.GetChildKeys([], key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (indexes.Count > 0)
            {
                return indexes.OrderBy(index => int.TryParse(index, out var number) ? number : int.MaxValue)
                    .Select(index => provider.TryGet($"{key}:{index}", out var item) ? item?.Trim() : null)
                    .Where(item => !string.IsNullOrEmpty(item))
                    .Select(item => item!)
                    .ToList();
            }

            if (provider.TryGet(key, out var value) && value != null)
            {
                return SplitList(value);
            }
        }

        return [];
    }

    /// <summary>Returns the configuration errors (empty when the settings are valid).</summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> errors = [];
        ValidateCluster(errors);
        ValidateJvm(errors);
        ValidateStorage(errors);
        ValidateSsl(errors);
        ValidateManagement(errors);
        ValidateSpringConfig(errors);
        return errors;
    }

    private void ValidateCluster(List<string> errors)
    {
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

        if (SqlQueryHistorySize < 0)
        {
            errors.Add("SQL_QUERY_HISTORY_SIZE must not be negative.");
        }

        // A -ConfigFile may configure the discovery instead.
        if (UsesKubernetesDiscovery &&
            string.IsNullOrWhiteSpace(ConfigFile) &&
            (string.IsNullOrWhiteSpace(K8sNamespace) || string.IsNullOrWhiteSpace(K8sServiceName)))
        {
            errors.Add(
                "K8S_NAMESPACE and K8S_SERVICE_NAME must be set for Kubernetes discovery (without CLUSTER_ENDPOINTS " +
                "and with USE_TCP_DISCOVERY_STATIC_IP_FINDER=false)."
            );
        }
    }

    private void ValidateSpringConfig(List<string> errors)
    {
        RequireExistingFile(errors, "SPRING_EXTRA_BEANS_FILE", SpringExtraBeansFile);
        RequireExistingFile(errors, "SPRING_EXTRA_PROPERTIES_FILE", SpringExtraPropertiesFile);

        // Spring evaluates "#{...}" (SpEL) in the values inserted into its configuration. The SSL values are inserted
        // only with USE_CLIENT_SSL, because USE_SSL alone configures the node from .NET.
        foreach (var (key, value) in ToSpringConfigPlaceholders())
        {
            var inserted = UseClientSsl || !key.StartsWith("SSL_", StringComparison.Ordinal);
            if (inserted && value?.Contains("#{", StringComparison.Ordinal) == true)
            {
                errors.Add($"{key} must not contain '#{{' (Spring would evaluate it as an expression).");
            }
        }
    }

    /// <summary>
    ///     How long the baseline waits for a stable topology before following it; when not set, 5 minutes with
    ///     persistence (a restarted node returns with its data, which is then not moved away and back) and 30 seconds without.
    /// </summary>
    public TimeSpan GetBaselineAutoAdjustTimeout(bool persistenceEnabled) =>
        TimeSpan.FromSeconds(BaselineAutoAdjustTimeoutInSeconds ?? (persistenceEnabled ? 300 : 30));

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
        // Without a connector configuration, Ignite starts neither the REST API (Jetty) nor the TCP binary protocol.
        [ConnectorConfigurationPlaceholder] =
            RestEnabled ? null : "<property name=\"connectorConfiguration\"><null/></property>",
        [ExtraBeansPlaceholder] = ReadFile(SpringExtraBeansFile),
        [ExtraPropertiesPlaceholder] = (PeerClassLoadingEnabled
                                           ? "<property name=\"peerClassLoadingEnabled\" value=\"true\"/>\n" : "") +
                                       ReadFile(SpringExtraPropertiesFile),
    };

    private static List<string> SplitList(string value) => value.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        )
        .ToList();

    private static string ReadFile(string? path) => string.IsNullOrWhiteSpace(path) ? "" : File.ReadAllText(path);

    /// <summary>The enum value of that name, in either naming style (LogOnly or LOG_ONLY); null when there is none.</summary>
    internal static T? ParseEnum<T>(string value)
        where T : struct, Enum =>
        Enum.TryParse<T>(value.Replace("_", string.Empty, StringComparison.Ordinal), true, out var parsed) &&
        Enum.IsDefined(parsed) ? parsed : null;

    /// <summary>The enum value of a setting that <see cref="Validate" /> accepts; throws for one it reports.</summary>
    internal static T ParseValidEnum<T>(string key, string value)
        where T : struct, Enum =>
        ParseEnum<T>(value) ?? throw new InvalidOperationException($"{key} '{value}' is not valid (see Validate).");

    private static void RequirePositive(List<string> errors, string key, int? value)
    {
        if (value <= 0)
        {
            errors.Add($"{key} must be positive.");
        }
    }

    private static void RequireExistingFile(List<string> errors, string key, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path))
        {
            errors.Add($"{key} ({path}) does not exist.");
        }
    }
}
