using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AspNetCore.IgniteServer;
using AspNetCore.IgniteServer.Utils;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.CommandLineUtils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using NLog;
using NLog.Config;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// fix for 'Stack smashing detected: dotnet terminated'
Environment.SetEnvironmentVariable("COMPlus_EnableAlternateStackCheck", "1");
// enable modules: Kubernetes, REST API, OpenCensus, etc.
Environment.SetEnvironmentVariable("OPTION_LIBS", "ignite-kubernetes, ignite-rest-http, ignite-opencensus");

Configuration = CreateIgniteConfiguration();
SetupIgniteLogging();

CommandLineApplication commandLineApplication = new() {Name = "AspNetCore.IgniteServer"};
commandLineApplication.HelpOption("-?|-Help");
var configFileArgument = commandLineApplication.Option("-ConfigFile",
    "XML configuration file. If not file is specified then default configuration is used.",
    CommandOptionType.SingleValue);
var offHeapArgument = commandLineApplication.Option("-OffHeap",
    "Size of off-heap memory given in megabytes.", CommandOptionType.SingleValue);
var onHeapArgument = commandLineApplication.Option("-OnHeap",
    "Size of on-heap memory given in megabytes.", CommandOptionType.SingleValue);
var serverPortArgument = commandLineApplication.Option("-SpiPort",
    "Specifies port for Discovery Spi.", CommandOptionType.SingleValue);
var clusterEndpointArgument = commandLineApplication.Option("-Cluster",
    "Specifies IP address and port of a cluster node. Multiple nodes can be specified.",
    CommandOptionType.MultipleValue);
var consistentIdArgument = commandLineApplication.Option("-ConsistentId",
    "Specifies as a consistent id of the node. This value is used in topology.",
    CommandOptionType.SingleValue);
var persistenceEnabled = commandLineApplication.Option("-PersistenceEnabled",
    "If set, it enables persistence mode.", CommandOptionType.NoValue);
commandLineApplication.OnExecute(async () =>
{
    if (!int.TryParse(Configuration["DEFAULT_ON_HEAP_MEMORY"], out var defaultOnHeapMemory))
    {
        defaultOnHeapMemory = 1024;
    }

    if (!int.TryParse(Configuration["DEFAULT_OFF_HEAP_MEMORY"], out var defaultOffHeapMemory))
    {
        defaultOffHeapMemory = 2048;
    }

    var useTcpDiscoveryStaticIpFinder = "true".Equals(Configuration["USE_TCP_DISCOVERY_STATIC_IP_FINDER"],
        StringComparison.OrdinalIgnoreCase);
    var defaultConsistentId = Configuration["WEBSITE_INSTANCE_ID"];
    var enableAuthentication = "true".Equals(Configuration["ENABLE_AUTHENTICATION"],
        StringComparison.OrdinalIgnoreCase);
    var k8sNamespace = Configuration["K8S_NAMESPACE"];
    var k8sServiceName = Configuration["K8S_SERVICE_NAME"];
    var igniteUserPassword = Configuration["IGNITE_USER_PASSWORD"];
    var useSsl = "true".Equals(Configuration["USE_SSL"], StringComparison.OrdinalIgnoreCase);
    var sslKeyStoreFilePath = Configuration["SSL_KEY_STORE_FILE_PATH"];
    var sslKeyStorePassword = Configuration["SSL_KEY_STORE_PASSWORD"];
    var sslTrustStoreFilePath = Configuration["SSL_TRUST_STORE_FILE_PATH"];
    var sslTrustStorePassword = Configuration["SSL_TRUST_STORE_PASSWORD"];
    var useClientSsl = "true".Equals(Configuration["USE_CLIENT_SSL"],
        StringComparison.OrdinalIgnoreCase);
    var sslClientCertificatePath = Configuration["SSL_CLIENT_CERTIFICATE_PATH"];
    var sslClientCertificatePassword = Configuration["SSL_CLIENT_CERTIFICATE_PASSWORD"];
    var springConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config",
        useClientSsl ? "spring-config-client-with-ssl.xml" : "spring-config.xml");
    var metricsExpireTime = TimeSpan.FromHours(24);
    if (int.TryParse(Configuration["METRICS_EXPIRE_TIME_IN_HOURS"], out var metricsExpireTimeInHours))
    {
        metricsExpireTime = TimeSpan.FromHours(metricsExpireTimeInHours);
    }

    var metricsLogFrequency = TimeSpan.FromMinutes(5);
    if (int.TryParse(Configuration["METRICS_LOG_FREQUENCY_IN_MINUTES"],
            out var metricsLogFrequencyInMinutes))
    {
        metricsLogFrequency = TimeSpan.FromMinutes(metricsLogFrequencyInMinutes);
    }

    var metricsUpdateFrequency = TimeSpan.FromSeconds(60);
    if (int.TryParse(Configuration["METRICS_UPDATE_FREQUENCY_IN_SECONDS"],
            out var metricsUpdateFrequencyInSeconds))
    {
        metricsUpdateFrequency = TimeSpan.FromSeconds(metricsUpdateFrequencyInSeconds);
    }

    var enableOffHeapMetrics = "true".Equals(Configuration["ENABLE_OFF_HEAP_METRICS"],
        StringComparison.OrdinalIgnoreCase);
    var springConfigText =
        await File.ReadAllTextAsync(springConfigPath, Encoding.UTF8).ConfigureAwait(false);
    springConfigText = springConfigText?.Replace("K8S_NAMESPACE", k8sNamespace)
        ?.Replace("K8S_SERVICE_NAME", k8sServiceName);
    if (useClientSsl)
    {
        springConfigText = springConfigText?.Replace("SSL_KEY_STORE_FILE_PATH", sslKeyStoreFilePath)
            ?.Replace("SSL_KEY_STORE_PASSWORD", sslKeyStorePassword)
            ?.Replace("SSL_TRUST_STORE_FILE_PATH", sslTrustStoreFilePath)
            ?.Replace("SSL_TRUST_STORE_PASSWORD", sslTrustStorePassword);
    }

    springConfigText = springConfigText?.Replace("OPEN_CENSUS_METRIC_EXPORTER_SPI_PERIOD",
        metricsUpdateFrequency.TotalMilliseconds.ToString(CultureInfo.InvariantCulture));
    await File.WriteAllTextAsync(springConfigPath, springConfigText, Encoding.UTF8).ConfigureAwait(false);
    var configFile = configFileArgument.HasValue() ? configFileArgument.Value() : null;
    igniteServerRunner = new IgniteServerRunner(metricsExpireTime, metricsLogFrequency,
        metricsUpdateFrequency,
        enableOffHeapMetrics, enableAuthentication, igniteUserPassword, configFile, useSsl,
        sslKeyStoreFilePath, sslKeyStorePassword, sslTrustStoreFilePath, sslTrustStorePassword,
        useClientSsl, sslClientCertificatePath, sslClientCertificatePassword);
    igniteServerRunner.SetOffHeapMemoryLimit(offHeapArgument.HasValue()
        ? int.Parse(offHeapArgument.Value())
        : defaultOffHeapMemory);

    igniteServerRunner.SetOnHeapMemoryLimit(onHeapArgument.HasValue()
        ? int.Parse(onHeapArgument.Value())
        : defaultOnHeapMemory);

    if (serverPortArgument.HasValue())
    {
        igniteServerRunner.SetServerPort(int.Parse(serverPortArgument.Value()));
    }

    if (clusterEndpointArgument.HasValue())
    {
        igniteServerRunner.SetClusterEndpoints(clusterEndpointArgument.Values);
    }
    else if (useTcpDiscoveryStaticIpFinder)
    {
        igniteServerRunner.SetClusterEndpoints(DefaultClusterEndpoints);
    }

    if (consistentIdArgument.HasValue())
    {
        igniteServerRunner.SetConsistentId(consistentIdArgument.Value());
    }
    else if (!string.IsNullOrWhiteSpace(defaultConsistentId))
    {
        igniteServerRunner.SetConsistentId(defaultConsistentId);
    }

    if (persistenceEnabled.HasValue())
    {
        igniteServerRunner.SetPersistence(true);
    }

    IDisposable sslKeyStoreFsw = null;
    IDisposable sslTrustStoreFsw = null;
    IDisposable sslClientCertificateFsw = null;
    if (useSsl || useClientSsl)
    {
        sslKeyStoreFsw = WatchFile(Path.GetFileName(sslKeyStoreFilePath),
            Path.GetDirectoryName(Path.GetFullPath(sslKeyStoreFilePath)),
            state => OnSslFileCreatedOrChanged());
        sslTrustStoreFsw = WatchFile(Path.GetFileName(sslTrustStoreFilePath),
            Path.GetDirectoryName(Path.GetFullPath(sslTrustStoreFilePath)),
            state => OnSslFileCreatedOrChanged());
        sslClientCertificateFsw = WatchFile(Path.GetFileName(sslClientCertificatePath),
            Path.GetDirectoryName(Path.GetFullPath(sslClientCertificatePath)),
            state => OnSslFileCreatedOrChanged());
    }

    try
    {
        while (shouldStart)
        {
            shouldStart = false;
            await igniteServerRunner.Run().ConfigureAwait(false);
        }
    }
    finally
    {
        if (useSsl || useClientSsl)
        {
            sslKeyStoreFsw?.Dispose();
            sslTrustStoreFsw?.Dispose();
            sslClientCertificateFsw?.Dispose();
        }
    }

    return 0;
});

try
{
    commandLineApplication.Execute(args);
}
catch (CommandParsingException e)
{
    commandLineApplication.Error.WriteLine($"ERROR: {e.Message}");
    commandLineApplication.ShowHelp();
}
catch (ArgumentException e)
{
    commandLineApplication.Error.WriteLine($"ERROR: {e.Message}");
    commandLineApplication.ShowHelp();
}
finally
{
    igniteServerRunner?.Dispose();
}

static IDisposable WatchFile(string filter, string path, Action<object> action)
{
    using PhysicalFileProvider physicalFileProvider = new(path) {UseActivePolling = true, UsePollingFileWatcher = true};
    var changeToken = physicalFileProvider.Watch(filter);
    return changeToken.RegisterChangeCallback(action, default);
}

static void OnSslFileCreatedOrChanged()
{
    TaskCompletionSource<bool> taskCompletionSource = new();
    const int absoluteExpirationRelativeToNowInSeconds = 10;
    CancellationChangeToken expirationToken =
        new(new CancellationTokenSource(TimeSpan.FromSeconds(absoluteExpirationRelativeToNowInSeconds + .01))
            .Token);
    MemoryCacheEntryOptions memoryCacheEntryOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(absoluteExpirationRelativeToNowInSeconds)
    };
    memoryCacheEntryOptions.AddExpirationToken(expirationToken);
    memoryCacheEntryOptions.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration
    {
        EvictionCallback = (k, _, r, __) =>
        {
            if (k is nameof(OnSslFileCreatedOrChanged) &&
                r is EvictionReason.Expired or EvictionReason.TokenExpired && !shouldStart)
            {
                shouldStart = true;
                igniteServerRunner?.Terminate();
            }
        }
    });
    MemoryCache.Set(nameof(OnSslFileCreatedOrChanged), taskCompletionSource, memoryCacheEntryOptions);
}

static void SetupIgniteLogging()
{
    // Step 1. Create configuration object
    LoggingConfiguration config = new();
    // Step 2. Create targets
    SerilogTarget serilogTarget = new("serilog");
    config.AddTarget(serilogTarget);
    // Step 3. Define rules
    config.AddRule(LogLevel.Info, LogLevel.Fatal, serilogTarget); // all to serilog
    // Step 4. Activate the configuration
    LogManager.Configuration = config;
}

static IConfiguration CreateIgniteConfiguration()
{
    var configurationBuilder = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", false, true)
        .AddJsonFile(Path.Combine("config", "serilog.config.json"), false, true)
        .AddEnvironmentVariables();
    var configuration = configurationBuilder.Build();
    return configuration;
}

public static partial class Program
{
    private static readonly List<string> DefaultClusterEndpoints = new() {$"{DnsUtils.GetLocalIpAddress()}:47500"};

    private static readonly MemoryCache MemoryCache =
        new(new MemoryCacheOptions {ExpirationScanFrequency = TimeSpan.FromSeconds(5)});

    private static volatile bool shouldStart = true;
    private static IgniteServerRunner? igniteServerRunner;
    public static IConfiguration? Configuration { get; private set; }
}
