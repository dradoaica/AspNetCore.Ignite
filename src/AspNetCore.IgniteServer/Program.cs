using AspNetCore.IgniteServer;
using AspNetCore.IgniteServer.Utils;
using Microsoft.Extensions.CommandLineUtils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using NLog;
using NLog.Config;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SerilogLogger = Serilog.Core.Logger;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// fix for 'Stack smashing detected: dotnet terminated'
Environment.SetEnvironmentVariable("COMPlus_EnableAlternateStackCheck", "1");
// enable modules: Kubernetes, REST API, OpenCensus, etc.
Environment.SetEnvironmentVariable("OPTION_LIBS", "ignite-kubernetes,ignite-rest-http,ignite-json,ignite-opencensus");

var configuration = CreateIgniteConfiguration();
ConfigureIgniteLogging(configuration);
ConfigureGracefulShutdown();

CommandLineApplication commandLineApplication = new()
{
    Name = "AspNetCore.IgniteServer",
};
commandLineApplication.HelpOption("-?|-Help");
var configFileArgument = commandLineApplication.Option(
    "-ConfigFile",
    "XML configuration file. If no file is specified then the default configuration is used.",
    CommandOptionType.SingleValue
);
var offHeapArgument = commandLineApplication.Option(
    "-OffHeap",
    "Size of off-heap memory given in megabytes.",
    CommandOptionType.SingleValue
);
var onHeapArgument = commandLineApplication.Option(
    "-OnHeap",
    "Size of on-heap memory given in megabytes.",
    CommandOptionType.SingleValue
);
var serverPortArgument = commandLineApplication.Option(
    "-SpiPort",
    "Specifies port for Discovery Spi.",
    CommandOptionType.SingleValue
);
var clusterEndpointArgument = commandLineApplication.Option(
    "-Cluster",
    "Specifies IP address and port of a cluster node. Multiple nodes can be specified.",
    CommandOptionType.MultipleValue
);
var consistentIdArgument = commandLineApplication.Option(
    "-ConsistentId",
    "Specifies as a consistent id of the node. This value is used in topology.",
    CommandOptionType.SingleValue
);
var persistenceEnabled = commandLineApplication.Option(
    "-PersistenceEnabled",
    "If set, it enables persistence mode.",
    CommandOptionType.NoValue
);
commandLineApplication.OnExecute(async () =>
    {
        var defaultOnHeapMemory = GetInt(configuration, "DEFAULT_ON_HEAP_MEMORY", 1024);
        var defaultOffHeapMemory = GetInt(configuration, "DEFAULT_OFF_HEAP_MEMORY", 2048);
        var useTcpDiscoveryStaticIpFinder = GetBool(configuration, "USE_TCP_DISCOVERY_STATIC_IP_FINDER");
        var defaultConsistentId = configuration["DEFAULT_CONSISTENT_ID"];
        var enableAuthentication = GetBool(configuration, "ENABLE_AUTHENTICATION");
        var igniteUserPassword = configuration["IGNITE_USER_PASSWORD"];
        var useSsl = GetBool(configuration, "USE_SSL");
        var sslKeyStoreFilePath = configuration["SSL_KEY_STORE_FILE_PATH"];
        var sslKeyStorePassword = configuration["SSL_KEY_STORE_PASSWORD"];
        var sslTrustStoreFilePath = configuration["SSL_TRUST_STORE_FILE_PATH"];
        var sslTrustStorePassword = configuration["SSL_TRUST_STORE_PASSWORD"];
        var useClientSsl = GetBool(configuration, "USE_CLIENT_SSL");
        var sslClientCertificatePath = configuration["SSL_CLIENT_CERTIFICATE_PATH"];
        var sslClientCertificatePassword = configuration["SSL_CLIENT_CERTIFICATE_PASSWORD"];
        var metricsExpireTime = TimeSpan.FromHours(GetInt(configuration, "METRICS_EXPIRE_TIME_IN_HOURS", 24));
        var metricsLogFrequency =
            TimeSpan.FromMinutes(GetInt(configuration, "METRICS_LOG_FREQUENCY_IN_MINUTES", 5));
        var metricsUpdateFrequency =
            TimeSpan.FromSeconds(GetInt(configuration, "METRICS_UPDATE_FREQUENCY_IN_SECONDS", 60));
        var enableOffHeapMetrics = GetBool(configuration, "ENABLE_OFF_HEAP_METRICS");
        var activationMinServerNodes = Math.Max(1, GetInt(configuration, "ACTIVATION_MIN_SERVER_NODES", 1));
        var autoResetLostPartitions = GetBool(configuration, "AUTO_RESET_LOST_PARTITIONS", true);
        if (useSsl)
        {
            RequireSetting("SSL_KEY_STORE_FILE_PATH", sslKeyStoreFilePath);
            RequireSetting("SSL_TRUST_STORE_FILE_PATH", sslTrustStoreFilePath);
        }

        if (useClientSsl)
        {
            RequireSetting("SSL_KEY_STORE_FILE_PATH", sslKeyStoreFilePath);
            RequireSetting("SSL_TRUST_STORE_FILE_PATH", sslTrustStoreFilePath);
            RequireSetting("SSL_CLIENT_CERTIFICATE_PATH", sslClientCertificatePath);
        }

        if (enableAuthentication && string.IsNullOrWhiteSpace(igniteUserPassword))
        {
            Logger?.Warning(
                "Authentication is enabled but IGNITE_USER_PASSWORD is not set: the default 'ignite' user keeps its well-known password."
            );
        }

        var springConfigPath = await SpringConfigRenderer.RenderToFileAsync(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "config",
                    useClientSsl ? "spring-config-client-with-ssl.xml" : "spring-config.xml"
                ),
                new Dictionary<string, string?>
                {
                    ["K8S_NAMESPACE"] = configuration["K8S_NAMESPACE"],
                    ["K8S_SERVICE_NAME"] = configuration["K8S_SERVICE_NAME"],
                    ["SSL_KEY_STORE_FILE_PATH"] = sslKeyStoreFilePath,
                    ["SSL_KEY_STORE_PASSWORD"] = sslKeyStorePassword,
                    ["SSL_TRUST_STORE_FILE_PATH"] = sslTrustStoreFilePath,
                    ["SSL_TRUST_STORE_PASSWORD"] = sslTrustStorePassword,
                    ["OPEN_CENSUS_METRIC_EXPORTER_SPI_PERIOD"] =
                        metricsUpdateFrequency.TotalMilliseconds.ToString(CultureInfo.InvariantCulture),
                }
            )
            .ConfigureAwait(false);
        igniteServerRunner = new IgniteServerRunner(
            new IgniteServerRunnerOptions
            {
                MetricsExpireTime = metricsExpireTime,
                MetricsLogFrequency = metricsLogFrequency,
                MetricsUpdateFrequency = metricsUpdateFrequency,
                EnableOffHeapMetrics = enableOffHeapMetrics,
                AuthenticationEnabled = enableAuthentication,
                IgniteUserPassword = igniteUserPassword,
                ConfigurationFile = configFileArgument.HasValue() ? configFileArgument.Value() : null,
                SpringConfigPath = springConfigPath,
                ActivationMinServerNodes = activationMinServerNodes,
                AutoResetLostPartitions = autoResetLostPartitions,
                UseSsl = useSsl,
                SslKeyStoreFilePath = sslKeyStoreFilePath,
                SslKeyStorePassword = sslKeyStorePassword,
                SslTrustStoreFilePath = sslTrustStoreFilePath,
                SslTrustStorePassword = sslTrustStorePassword,
                UseClientSsl = useClientSsl,
                SslClientCertificatePath = sslClientCertificatePath,
                SslClientCertificatePassword = sslClientCertificatePassword,
            }
        );
        igniteServerRunner.SetOffHeapMemoryLimit(
            offHeapArgument.HasValue() ? int.Parse(offHeapArgument.Value()!, CultureInfo.InvariantCulture)
                : defaultOffHeapMemory
        );
        igniteServerRunner.SetOnHeapMemoryLimit(
            onHeapArgument.HasValue() ? int.Parse(onHeapArgument.Value()!, CultureInfo.InvariantCulture)
                : defaultOnHeapMemory
        );
        if (serverPortArgument.HasValue())
        {
            igniteServerRunner.SetServerPort(int.Parse(serverPortArgument.Value()!, CultureInfo.InvariantCulture));
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
            igniteServerRunner.SetConsistentId(consistentIdArgument.Value()!);
        }
        else if (!string.IsNullOrWhiteSpace(defaultConsistentId))
        {
            igniteServerRunner.SetConsistentId(defaultConsistentId);
        }

        if (persistenceEnabled.HasValue())
        {
            igniteServerRunner.SetPersistence(true);
        }

        List<IDisposable> fileWatchers = [];
        if (useSsl || useClientSsl)
        {
            fileWatchers.Add(WatchFile(sslKeyStoreFilePath!, OnSslFileCreatedOrChanged));
            fileWatchers.Add(WatchFile(sslTrustStoreFilePath!, OnSslFileCreatedOrChanged));
        }

        if (useClientSsl)
        {
            fileWatchers.Add(WatchFile(sslClientCertificatePath!, OnSslFileCreatedOrChanged));
        }

        try
        {
            while (shouldStart && !shuttingDown)
            {
                shouldStart = false;
                await igniteServerRunner.Run().ConfigureAwait(false);
            }
        }
        finally
        {
            fileWatchers.ForEach(fileWatcher => fileWatcher.Dispose());
        }

        return 0;
    }
);

try
{
    Logger?.Information("Starting Ignite server...");
    commandLineApplication.Execute(args);
}
catch (CommandParsingException ex)
{
    commandLineApplication.Error.WriteLine($"ERROR: {ex.Message}");
    commandLineApplication.ShowHelp();
}
catch (Exception ex) when (ex.GetBaseException() is ArgumentException argumentException)
{
    // OnExecute is async, so its exceptions arrive wrapped in an AggregateException
    commandLineApplication.Error.WriteLine($"ERROR: {argumentException.Message}");
    commandLineApplication.ShowHelp();
}
finally
{
    igniteServerRunner?.Dispose();
}

return 0;

static IConfiguration CreateIgniteConfiguration()
{
    var configurationBuilder = new ConfigurationBuilder().AddJsonFile("appsettings.json", false, true)
        .AddJsonFile("serilog.config.json", false, true)
        .AddEnvironmentVariables();
    return configurationBuilder.Build();
}

static void ConfigureIgniteLogging(IConfiguration configuration)
{
    // Configure NLog
    // Step 1. Create configuration object
    LoggingConfiguration config = new();
    // Step 2. Create targets
    SerilogTarget serilogTarget = new("serilog");
    config.AddTarget(serilogTarget);
    // Step 3. Define rules
    config.AddRule(LogLevel.Info, LogLevel.Fatal, serilogTarget); // all to serilog
    // Step 4. Activate the configuration
    LogManager.Configuration = config;

    // Configure Serilog
    var loggerConfiguration = new LoggerConfiguration().ReadFrom.Configuration(configuration);
    Logger = loggerConfiguration.CreateLogger();
    Log.Logger = Logger;

    AppDomain.CurrentDomain.UnhandledException += CurrentDomainUnhandledException;
    TaskScheduler.UnobservedTaskException += TaskSchedulerUnobservedTaskException;
}

static void ConfigureGracefulShutdown()
{
    AppDomain.CurrentDomain.ProcessExit += CurrentDomainProcessExit;
}

static void CurrentDomainProcessExit(object? sender, EventArgs e)
{
    shuttingDown = true;
    shouldStart = false;
    Logger?.Information("Stopping Ignite server...");
    igniteServerRunner?.Stop();
    Logger?.Information("Ignite server stopped.");
}

static void CurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
{
    if (e.ExceptionObject is Exception ex)
    {
        Logger?.Error(ex, "UnhandledException");
    }
    else
    {
        var msg = e.ExceptionObject.ToString();
        var exCode = Marshal.GetLastWin32Error();
        if (exCode != 0)
        {
            msg += " ErrorCode: " + exCode.ToString("X16", CultureInfo.InvariantCulture);
        }

        Logger?.Error("Unhandled External Exception: {Message}", msg);
    }
}

static void TaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
{
    Logger?.Error(e.Exception, "ERROR: UNOBSERVED TASK EXCEPTION");
    e.SetObserved();
}

static int GetInt(IConfiguration configuration, string key, int defaultValue) =>
    int.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value
        : defaultValue;

static bool GetBool(IConfiguration configuration, string key, bool defaultValue = false) =>
    bool.TryParse(configuration[key], out var value) ? value : defaultValue;

static void RequireSetting(string key, string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new ArgumentException($"{key} must be set when SSL is enabled.");
    }
}

/// <summary>Invokes <paramref name="onChange" /> on every change of the file until the returned handle is disposed.</summary>
static IDisposable WatchFile(string filePath, Action onChange)
{
    var fullPath = Path.GetFullPath(filePath);
    PhysicalFileProvider physicalFileProvider = new(Path.GetDirectoryName(fullPath)!)
    {
        UseActivePolling = true,
        UsePollingFileWatcher = true,
    };
    var changeRegistration = ChangeToken.OnChange(() => physicalFileProvider.Watch(Path.GetFileName(fullPath)), onChange);
    return new FileWatcher(physicalFileProvider, changeRegistration);
}

static void OnSslFileCreatedOrChanged() =>
    // debounce: certificate rotation usually touches several files, restart once they have settled
    SslFileChangeDebounceTimer.Change(SslFileChangeDebounceInterval, Timeout.InfiniteTimeSpan);

public static partial class Program
{
    private static readonly List<string> DefaultClusterEndpoints = [$"{DnsUtils.GetLocalIpAddress()}:47500",];

    private static readonly TimeSpan SslFileChangeDebounceInterval = TimeSpan.FromSeconds(10);

    private static readonly Timer SslFileChangeDebounceTimer = new(
        _ => RestartAfterSslFileChange(),
        null,
        Timeout.Infinite,
        Timeout.Infinite
    );

    private static volatile bool shouldStart = true;
    private static volatile bool shuttingDown;
    private static IgniteServerRunner? igniteServerRunner;
    internal static SerilogLogger? Logger { get; private set; }

    private static void RestartAfterSslFileChange()
    {
        if (shuttingDown || shouldStart)
        {
            return;
        }

        Logger?.Information("SSL files changed, restarting Ignite server...");
        shouldStart = true;
        igniteServerRunner?.Terminate();
    }

    private sealed class FileWatcher(PhysicalFileProvider physicalFileProvider, IDisposable changeRegistration)
        : IDisposable
    {
        public void Dispose()
        {
            changeRegistration.Dispose();
            physicalFileProvider.Dispose();
        }
    }
}
