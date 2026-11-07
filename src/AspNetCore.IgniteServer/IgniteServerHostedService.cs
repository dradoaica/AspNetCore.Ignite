using AspNetCore.IgniteServer.Utils;
using Microsoft.Extensions.Hosting;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCore.IgniteServer;

/// <summary>Runs the Ignite node for the lifetime of the host, restarting it when its SSL files change.</summary>
internal sealed class IgniteServerHostedService(
    IgniteServerSettings settings,
    ILogger logger,
    IHostApplicationLifetime lifetime
) : BackgroundService
{
    private const int DefaultDiscoveryPort = 47500;
    private static readonly TimeSpan SslFileChangeDebounce = TimeSpan.FromSeconds(10);

    private volatile bool restartRequested;
    private volatile bool stopping;
    private IgniteServerRunner? runner;

    // Ignition.Start blocks, so run the node off the host's startup path
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => RunAsync(stoppingToken), CancellationToken.None);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // before stopping the node, so that RunAsync does not take the stop for a failure
        stopping = true;
        runner?.Stop();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        runner?.Dispose();
        base.Dispose();
    }

    /// <summary>Applies the memory, discovery, consistent id and persistence settings to the runner.</summary>
    internal static void ApplySettings(
        IgniteServerRunner igniteServerRunner,
        IgniteServerSettings settings,
        Func<string> getLocalIpAddress
    )
    {
        igniteServerRunner.SetOffHeapMemoryLimit(settings.OffHeapMemory);
        igniteServerRunner.SetOnHeapMemoryLimit(settings.OnHeapMemory);
        if (settings.SpiPort.HasValue)
        {
            igniteServerRunner.SetServerPort(settings.SpiPort.Value);
        }

        if (settings.ClusterEndpoints.Count > 0)
        {
            igniteServerRunner.SetClusterEndpoints(settings.ClusterEndpoints);
        }
        else if (settings.UseTcpDiscoveryStaticIpFinder)
        {
            // resolved only when needed: a failing DNS lookup must not break nodes that do not use it
            igniteServerRunner.SetClusterEndpoints([$"{getLocalIpAddress()}:{DefaultDiscoveryPort}",]);
        }

        if (!string.IsNullOrWhiteSpace(settings.ConsistentId))
        {
            igniteServerRunner.SetConsistentId(settings.ConsistentId);
        }

        if (settings.PersistenceEnabled)
        {
            igniteServerRunner.SetPersistence(true);
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (settings.EnableAuthentication && string.IsNullOrWhiteSpace(settings.IgniteUserPassword))
            {
                logger.Warning(
                    "Authentication is enabled but IGNITE_USER_PASSWORD is not set: the default 'ignite' user keeps its well-known password."
                );
            }

            var springConfigPath = await SpringConfigRenderer.RenderToFileAsync(
                    Path.Combine(
                        AppContext.BaseDirectory,
                        "config",
                        settings.UseClientSsl ? "spring-config-client-with-ssl.xml" : "spring-config.xml"
                    ),
                    settings.ToSpringConfigPlaceholders()
                )
                .ConfigureAwait(false);
            runner = new IgniteServerRunner(settings.ToRunnerOptions(springConfigPath), logger);
            ApplySettings(runner, settings, DnsUtils.GetLocalIpAddress);
            using var sslFileWatcher = CreateSslFileWatcher();
            do
            {
                restartRequested = false;
                await runner.Run().ConfigureAwait(false);
            } while (restartRequested && !stopping && !stoppingToken.IsCancellationRequested);

            if (!stopping && !stoppingToken.IsCancellationRequested)
            {
                logger.Error("The Ignite node stopped unexpectedly, stopping the server.");
                Fail();
            }
        }
        catch (Exception ex) when (!stopping)
        {
            logger.Fatal(ex, "The Ignite server failed.");
            Fail();
        }
    }

    private FileChangeWatcher? CreateSslFileWatcher()
    {
        List<string> sslFiles = [];
        if (settings.UseSsl || settings.UseClientSsl)
        {
            sslFiles.Add(settings.SslKeyStoreFilePath!);
            sslFiles.Add(settings.SslTrustStoreFilePath!);
        }

        if (settings.UseClientSsl)
        {
            sslFiles.Add(settings.SslClientCertificatePath!);
        }

        if (sslFiles.Count == 0)
        {
            return null;
        }

        if (!settings.RestartOnSslFileChange)
        {
            logger.Information(
                "RESTART_ON_SSL_FILE_CHANGE is disabled: renewed SSL files are used after the next restart only."
            );
            return null;
        }

        return new FileChangeWatcher(
            sslFiles,
            OnSslFilesChanged,
            SslFileChangeDebounce,
            TimeSpan.FromSeconds(settings.SslRestartMaxJitterInSeconds)
        );
    }

    private void OnSslFilesChanged()
    {
        if (stopping || lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return;
        }

        logger.Information("SSL files changed, restarting the Ignite node...");
        restartRequested = true;
        runner?.Restart();
    }

    private void Fail()
    {
        Environment.ExitCode = 1;
        lifetime.StopApplication();
    }
}
