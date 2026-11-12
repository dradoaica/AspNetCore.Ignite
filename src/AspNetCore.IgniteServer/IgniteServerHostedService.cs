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
    private static readonly TimeSpan SslFileChangeDebounce = TimeSpan.FromSeconds(10);

    // Guards stopping and runner together: StopAsync either finds the runner and stops it, or sets stopping before
    // the runner exists, and RunAsync then does not start the node.
    private readonly Lock gate = new();
    private volatile bool restartRequested;
    private IgniteServerRunner? runner;
    private FileChangeWatcher? sslFileWatcher;
    private volatile bool stopping;

    // Ignition.Start blocks for a long time, so the node runs on a thread pool thread and the host starts at once.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(
        () => RunAsync(stoppingToken),
        CancellationToken.None
    );

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        IgniteServerRunner? current;
        lock (gate)
        {
            // Set before the node stops: RunAsync would otherwise report the stopped node as a failure.
            stopping = true;
            current = runner;
        }

        current?.Stop(TimeSpan.FromSeconds(settings.GracefulStopTimeoutInSeconds));
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        nodeState.SetRunner(null);
        runner?.Dispose();
        base.Dispose();
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (settings.EnableAuthentication && string.IsNullOrWhiteSpace(settings.IgniteUserPassword))
            {
                logger.Warning(
                    "Authentication is enabled but IGNITE_USER_PASSWORD is not set: the default 'ignite' user keeps " +
                    "its well-known password."
                );
            }

            if (!settings.UseSsl)
            {
                logger.Warning(
                    "USE_SSL is disabled: nodes join the cluster unauthenticated and unencrypted, so restrict the " +
                    "discovery and communication ports (47500, 47100) to the cluster's nodes, e.g., with a " +
                    "NetworkPolicy."
                );
            }

            if (settings.PeerAssemblyLoading)
            {
                logger.Warning(
                    "PEER_ASSEMBLY_LOADING is enabled: this node loads and runs .NET assemblies sent by any node of " +
                    "the cluster."
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
            IgniteServerRunner created = new(settings, logger, DnsUtils.GetLocalIpAddress, springConfigPath);
            lock (gate)
            {
                if (stopping)
                {
                    // The host is already stopping, so the node is not started at all.
                    created.Dispose();
                    return;
                }

                runner = created;
            }

            nodeState.SetRunner(created);
            sslFileWatcher = CreateSslFileWatcher();
            do
            {
                restartRequested = false;
                // WaitAsync lets the host stop even when the node itself cannot.
                await created.Run().WaitAsync(stoppingToken).ConfigureAwait(false);
            } while (restartRequested && !stopping && !stoppingToken.IsCancellationRequested);

            if (!stopping && !stoppingToken.IsCancellationRequested)
            {
                logger.Error("The Ignite node stopped unexpectedly, stopping the server.");
                Fail();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping; a node that is still running stops with the process.
        }
        catch (Exception ex) when (!stopping)
        {
            logger.Fatal(ex, "The Ignite server failed.");
            Fail();
        }
        finally
        {
            sslFileWatcher?.Dispose();
        }
    }

    /// <summary>
    ///     A broken SPRING_EXTRA_* fragment fails here, with the file named, rather than deep in the JVM.
    /// </summary>
    internal static void EnsureWellFormed(string springConfigPath)
    {
        try
        {
            XDocument.Load(springConfigPath);
        }
        catch (XmlException ex)
        {
            throw new InvalidOperationException(
                $"The Spring configuration is not well-formed XML (check SPRING_EXTRA_BEANS_FILE and " +
                $"SPRING_EXTRA_PROPERTIES_FILE): {ex.Message}",
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

        if (runner?.Node == null)
        {
            // The node is starting and may already have read the previous files. Asking the watcher again makes
            // this method run once more after the usual delay, when the node can be restarted.
            logger.Information("SSL files changed while the Ignite node starts: it restarts once started.");
            sslFileWatcher?.NotifyChanged();
            return;
        }

        logger.Information("SSL files changed, restarting the Ignite node...");
        restartRequested = true;
        var timeout = TimeSpan.FromSeconds(settings.GracefulStopTimeoutInSeconds);
        if (runner.Restart(timeout))
        {
            return;
        }

        // The graceful stop can take long (e.g., while another node restarts too). The server exits instead, and its
        // container restarts with the renewed files.
        logger.Error(
            "The Ignite node did not stop gracefully within {Timeout} to restart with the renewed SSL files; the " +
            "server stops so that its container restarts with them.",
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
