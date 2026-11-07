using AspNetCore.IgniteServer;
using AspNetCore.IgniteServer.Management;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Prometheus;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// cancelling the default SIGTERM action lets Main return its exit code (0 after a graceful stop) instead of the
// process being terminated (143); the host stops the node
using var sigTermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = true);
return await new ServerCommandLine(RunServerAsync).InvokeAsync(args).ConfigureAwait(false);

static async Task<int> RunServerAsync(IReadOnlyDictionary<string, string?> overrides)
{
    var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", false, true)
        .AddJsonFile("serilog.config.json", false, true)
        .AddEnvironmentVariables()
        .AddInMemoryCollection(overrides)
        .Build();
    Log.Logger = new LoggerConfiguration().ReadFrom.Configuration(configuration).CreateLogger();
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception: {ExceptionObject}", e.ExceptionObject);
    TaskScheduler.UnobservedTaskException += (_, e) =>
    {
        Log.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    };
    try
    {
        var settings = IgniteServerSettings.Load(configuration);
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

        using var host = settings.ManagementEnabled ? BuildWithManagement(configuration, settings)
            : BuildWithoutManagement(configuration, settings);
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

static IHost BuildWithoutManagement(IConfiguration configuration, IgniteServerSettings settings)
{
    HostApplicationBuilder builder = new(
        new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = AppContext.BaseDirectory,
        }
    );
    builder.Configuration.AddConfiguration(configuration);
    AddServerServices(builder.Services, settings);
    return builder.Build();
}

// /health/live, /health/ready and /metrics (Prometheus) on MANAGEMENT_PORT
static IHost BuildWithManagement(IConfiguration configuration, IgniteServerSettings settings)
{
    var builder = WebApplication.CreateEmptyBuilder(
        new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, }
    );
    builder.Configuration.AddConfiguration(configuration);
    builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel => kestrel.ListenAnyIP(settings.ManagementPort));
    builder.Services.AddRoutingCore();
    AddServerServices(builder.Services, settings);
    builder.Services.AddSingleton<IgniteNodeMetrics>();
    builder.Services.AddHealthChecks()
        .AddCheck<IgniteNodeHealthCheck>("ignite-node", tags: ["ready",])
        .ForwardToPrometheus();
    var app = builder.Build();
    // created eagerly, so its instruments exist before the first scrape
    app.Services.GetRequiredService<IgniteNodeMetrics>();
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, });
    app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready"), });
    app.MapMetrics("/metrics");
    return app;
}

static void AddServerServices(IServiceCollection services, IgniteServerSettings settings)
{
    services.Configure<HostOptions>(hostOptions =>
        hostOptions.ShutdownTimeout = TimeSpan.FromSeconds(settings.ShutdownTimeoutInSeconds)
    );
    services.AddSerilog(Log.Logger);
    services.AddSingleton(Log.Logger);
    services.AddSingleton(settings);
    services.AddSingleton<IgniteNodeState>();
    services.AddHostedService<IgniteServerHostedService>();
}
