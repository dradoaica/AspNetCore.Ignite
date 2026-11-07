using AspNetCore.IgniteServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Help;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

Option<string> configFileOption = new("-ConfigFile")
{
    Description = "XML configuration file. If no file is specified then the default configuration is used.",
};
Option<int?> offHeapOption = new("-OffHeap") { Description = "Size of off-heap memory given in megabytes.", };
Option<int?> onHeapOption = new("-OnHeap") { Description = "Size of on-heap memory given in megabytes.", };
Option<int?> spiPortOption = new("-SpiPort") { Description = "Specifies port for Discovery Spi.", };
Option<string[]> clusterOption = new("-Cluster")
{
    Description = "Specifies IP address and port of a cluster node. Multiple nodes can be specified.",
    Arity = ArgumentArity.ZeroOrMore,
};
Option<string> consistentIdOption = new("-ConsistentId")
{
    Description = "Specifies as a consistent id of the node. This value is used in topology.",
};
Option<bool> persistenceEnabledOption = new("-PersistenceEnabled")
{
    Description = "If set, it enables persistence mode.",
};
RootCommand rootCommand = new("AspNetCore.IgniteServer: Apache Ignite 2 server node.")
{
    configFileOption,
    offHeapOption,
    onHeapOption,
    spiPortOption,
    clusterOption,
    consistentIdOption,
    persistenceEnabledOption,
};
rootCommand.Options.OfType<HelpOption>().Single().Aliases.Add("-Help");
rootCommand.SetAction(async (parseResult, _) =>
    {
        // every option overrides the setting of the same meaning (appsettings.json < environment < command line)
        Dictionary<string, string?> overrides = [];
        AddOverride(overrides, "CONFIG_FILE", parseResult.GetValue(configFileOption));
        AddOverride(overrides, "DEFAULT_OFF_HEAP_MEMORY", parseResult.GetValue(offHeapOption));
        AddOverride(overrides, "DEFAULT_ON_HEAP_MEMORY", parseResult.GetValue(onHeapOption));
        AddOverride(overrides, "SPI_PORT", parseResult.GetValue(spiPortOption));
        AddOverride(overrides, "DEFAULT_CONSISTENT_ID", parseResult.GetValue(consistentIdOption));
        if (parseResult.GetValue(persistenceEnabledOption))
        {
            overrides["PERSISTENCE_ENABLED"] = "true";
        }

        var clusterEndpoints = parseResult.GetValue(clusterOption) ?? [];
        for (var i = 0; i < clusterEndpoints.Length; i++)
        {
            overrides[$"CLUSTER_ENDPOINTS:{i}"] = clusterEndpoints[i];
        }

        return await RunServerAsync(overrides).ConfigureAwait(false);
    }
);

// the host handles SIGTERM/CTRL+C (graceful shutdown), not the command line parser; cancelling the default SIGTERM
// action lets Main return its exit code (0 after a graceful stop) instead of the process being terminated (143)
using var sigTermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = true);
return await rootCommand.Parse(args)
    .InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = null, })
    .ConfigureAwait(false);

static void AddOverride<T>(Dictionary<string, string?> overrides, string key, T? value)
{
    if (value is not null && (value is not string text || !string.IsNullOrWhiteSpace(text)))
    {
        overrides[key] = Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}

static async Task<int> RunServerAsync(IReadOnlyDictionary<string, string?> overrides)
{
    HostApplicationBuilder builder = new(
        new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = AppContext.BaseDirectory,
        }
    );
    builder.Configuration.AddJsonFile("appsettings.json", false, true)
        .AddJsonFile("serilog.config.json", false, true)
        .AddEnvironmentVariables()
        .AddInMemoryCollection(overrides);
    Log.Logger = new LoggerConfiguration().ReadFrom.Configuration(builder.Configuration).CreateLogger();
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception: {ExceptionObject}", e.ExceptionObject);
    TaskScheduler.UnobservedTaskException += (_, e) =>
    {
        Log.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    };
    try
    {
        var settings = IgniteServerSettings.Load(builder.Configuration);
        var errors = settings.Validate();
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                Log.Error("Invalid configuration: {Error}", error);
                await Console.Error.WriteLineAsync($"ERROR: {error}").ConfigureAwait(false);
            }

            return 1;
        }

        builder.Services.Configure<HostOptions>(hostOptions =>
            hostOptions.ShutdownTimeout = TimeSpan.FromSeconds(settings.ShutdownTimeoutInSeconds)
        );
        builder.Services.AddSerilog(Log.Logger);
        builder.Services.AddSingleton(Log.Logger);
        builder.Services.AddSingleton(settings);
        builder.Services.AddHostedService<IgniteServerHostedService>();
        using var host = builder.Build();
        Log.Information("Starting Ignite server...");
        await host.RunAsync().ConfigureAwait(false);
        Log.Information("Ignite server stopped.");
        return Environment.ExitCode;
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "The Ignite server failed to start.");
        return 1;
    }
    finally
    {
        await Log.CloseAndFlushAsync().ConfigureAwait(false);
    }
}
