using AspNetCore.IgniteServer;
using AspNetCore.IgniteServer.Management;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Prometheus;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// By default SIGTERM terminates the process with exit code 143. Cancelling that default lets Main return its own exit
// code (0 after a graceful stop). The token stops the host, also when the signal arrives before the host has started.
using CancellationTokenSource sigTerm = new();
using var sigTermRegistration = PosixSignalRegistration.Create(
    PosixSignal.SIGTERM,
    context =>
    {
        context.Cancel = true;
        sigTerm.Cancel();
    }
);
return await new ServerCommandLine(overrides => RunServerAsync(overrides, sigTerm.Token)).InvokeAsync(args)
    .ConfigureAwait(false);

static async Task<int> RunServerAsync(IReadOnlyDictionary<string, string?> overrides, CancellationToken stopToken)
{
    var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", false, true)
        .AddJsonFile("serilog.config.json", false, true)
        .AddEnvironmentVariables()
        .AddInMemoryCollection(overrides)
        .Build();
    Log.Logger = new LoggerConfiguration().ReadFrom.Configuration(configuration).CreateLogger();
    AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Fatal(
        e.ExceptionObject as Exception,
        "Unhandled exception: {ExceptionObject}",
        e.ExceptionObject
    );
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
        if (stopToken.IsCancellationRequested)
        {
            Log.Information("Stopped before the Ignite server started.");
            return 0;
        }

        Log.Information("Starting Ignite server...");
        await host.RunAsync(stopToken).ConfigureAwait(false);
        Log.Information("Ignite server stopped.");
        return Environment.ExitCode;
    }
    catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
    {
        // SIGTERM arrived while the host was starting.
        Log.Information("Ignite server stopped while it started.");
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

// /health/liveness, /health/startup, /health/readiness and /metrics (Prometheus) on MANAGEMENT_PORT
static IHost BuildWithManagement(IConfiguration configuration, IgniteServerSettings settings)
{
    var builder = WebApplication.CreateEmptyBuilder(
        new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        }
    );
    builder.Configuration.AddConfiguration(configuration);
    builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel => kestrel.ListenAnyIP(settings.ManagementPort));
    builder.Services.AddRoutingCore();
    AddServerServices(builder.Services, settings);
    builder.Services.AddSingleton(sp => new IgniteNodeMetrics(
            sp.GetRequiredService<IgniteNodeState>(),
            settings.MetricsPerCache,
            logger: Log.Logger
        )
    );
    // With an OTLP endpoint, the same instruments are also pushed to an OpenTelemetry collector (OTEL_* settings).
    if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    {
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource =>
                resource.AddService(configuration["OTEL_SERVICE_NAME"] ?? "aspnetcore-ignite-server")
            )
            .WithMetrics(metrics => metrics.AddMeter(IgniteNodeMetrics.MeterName).AddOtlpExporter());
    }

    builder.Services.AddHealthChecks()
        .AddCheck<IgniteNodeHealthCheck>("ignite-node", tags: ["ready",])
        .AddCheck<IgniteNodeStartedHealthCheck>("ignite-node-started", tags: ["started",])
        .ForwardToPrometheus();
    var app = builder.Build();
    // Resolved here so that the instruments exist before the first scrape.
    app.Services.GetRequiredService<IgniteNodeMetrics>();
    app.MapHealthChecks(
        "/health/liveness",
        new HealthCheckOptions
        {
            Predicate = _ => false,
        }
    );
    app.MapHealthChecks(
        "/health/startup",
        new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("started"),
        }
    );
    app.MapHealthChecks(
        "/health/readiness",
        new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
        }
    );
    app.MapMetrics();
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
