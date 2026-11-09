using AspNetCore.IgniteServer.Utils;
using Microsoft.Extensions.Hosting;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace AspNetCore.IgniteServer;

/// <summary>Runs the Ignite node for the lifetime of the host, restarting it when its SSL files change.</summary>
internal sealed class IgniteServerHostedService(
    IgniteServerSettings settings,
    ILogger logger,
    IHostApplicationLifetime lifetime,
    IgniteNodeState nodeState
) : BackgroundService
{
    private const int DefaultDiscoveryPort = 47500;
    private static readonly TimeSpan SslFileChangeDebounce = TimeSpan.FromSeconds(10);

    private volatile bool restartRequested;
    private IgniteServerRunner? runner;
    private volatile bool stopping;

    // Ignition.Start blocks, so run the node off the host's startup path
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(
        () => RunAsync(stoppingToken),
        CancellationToken.None
    );

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // before stopping the node, so that RunAsync does not take the stop for a failure
        stopping = true;
        runner?.Stop(TimeSpan.FromSeconds(settings.GracefulStopTimeoutInSeconds));
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        nodeState.SetRunner(null);
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
            // resolved only when needed: a failing DNS lookup must not break nodes that do not use it
        {
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

            if (!settings.UseSsl)
            {
                logger.Warning(
                    "USE_SSL is disabled: nodes join the cluster unauthenticated and unencrypted, so restrict the discovery and communication ports (47500, 47100) to the cluster's nodes, e.g., with a NetworkPolicy."
                );
            }

            if (settings.PeerAssemblyLoading)
            {
                logger.Warning(
                    "PEER_ASSEMBLY_LOADING is enabled: this node loads and runs .NET assemblies sent by any node of the cluster."
                );
            }

            var springConfigPath = await SpringConfigRenderer.RenderToFileAsync(
                    Path.Combine(
                        AppContext.BaseDirectory,
                        "config",
                        settings.UseClientSsl ? "spring-config-client-with-ssl.xml" : "spring-config.xml"
                    ),
                    settings.ToSpringConfigPlaceholders(),
                    settings.ToSpringConfigXmlPlaceholders()
                )
                .ConfigureAwait(false);
            EnsureWellFormed(springConfigPath);
            runner = new IgniteServerRunner(settings.ToRunnerOptions(springConfigPath), logger);
            ApplySettings(runner, settings, DnsUtils.GetLocalIpAddress);
            nodeState.SetRunner(runner);
            using var sslFileWatcher = CreateSslFileWatcher();
            do
            {
                restartRequested = false;
                // a node that cannot stop must not keep the host from stopping
                await runner.Run().WaitAsync(stoppingToken).ConfigureAwait(false);
            } while (restartRequested && !stopping && !stoppingToken.IsCancellationRequested);

            if (!stopping && !stoppingToken.IsCancellationRequested)
            {
                logger.Error("The Ignite node stopped unexpectedly, stopping the server.");
                Fail();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // the host stops, and the node with the process
        }
        catch (Exception ex) when (!stopping)
        {
            logger.Fatal(ex, "The Ignite server failed.");
            Fail();
        }
    }

    /// <summary>A broken SPRING_EXTRA_* fragment fails here, with the file named, rather than deep in the JVM.</summary>
    internal static void EnsureWellFormed(string springConfigPath)
    {
        try
        {
            XDocument.Load(springConfigPath);
        }
        catch (XmlException ex)
        {
            throw new InvalidOperationException(
                $"The Spring configuration is not well-formed XML (check SPRING_EXTRA_BEANS_FILE and SPRING_EXTRA_PROPERTIES_FILE): {ex.Message}",
                ex
            );
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
        var timeout = TimeSpan.FromSeconds(settings.GracefulStopTimeoutInSeconds);
        if (runner?.Restart(timeout) != false)
        {
            return;
        }

        // it may wait long (e.g., for another node that restarts too): the server exits instead, and its container
        // restarts with the renewed files
        logger.Error(
            "The Ignite node did not stop gracefully within {Timeout} to restart with the renewed SSL files; the server stops so that its container restarts with them.",
            timeout
        );
        Fail();
    }

    private void Fail()
    {
        Environment.ExitCode = 1;
        lifetime.StopApplication();
    }
}
