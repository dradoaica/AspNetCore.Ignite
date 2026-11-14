using AspNetCore.IgniteServer.Utils;
using Microsoft.Extensions.Hosting;
using Serilog;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace AspNetCore.IgniteServer;

/// <summary>
///     Runs the Ignite node for the lifetime of the host: renders its Spring configuration, starts it, restarts it
///     when its SSL files change (<see cref="SslFileRestarter" />), stops it with the host, and sets the exit code.
/// </summary>
internal sealed class IgniteServerHostedService(
    IgniteServerSettings settings,
    ILogger logger,
    IHostApplicationLifetime lifetime,
    IgniteNodeState nodeState,
    INodeLauncher launcher,
    ServerExit exit
) : BackgroundService
{
    // Guards stopping and runner together: StopAsync either finds the runner and stops it, or sets stopping before
    // the runner exists, and RunAsync then does not start the node.
    private readonly Lock gate = new();
    private IgniteServerRunner? runner;
    private volatile bool stopping;

    private bool IsStopping => stopping || lifetime.ApplicationStopping.IsCancellationRequested;

    // Since .NET 10, the host runs all of ExecuteAsync on a thread pool thread: the long Ignition.Start does not
    // delay the start of the host.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunAsync(stoppingToken);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        IgniteServerRunner? current;
        lock (gate)
        {
            // Set before the node stops: RunAsync would otherwise report the stopped node as a failure.
            stopping = true;
            current = runner;
        }

        if (current != null)
        {
            try
            {
                // Off the host's thread, so that the host can stop waiting at its own timeout. Validation keeps
                // GRACEFUL_STOP_TIMEOUT_IN_SECONDS below it, so this happens only when the host is stopped otherwise.
                var graceful = await Task.Run(
                        () => current.Stop(TimeSpan.FromSeconds(settings.GracefulStopTimeoutInSeconds)),
                        CancellationToken.None
                    )
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!graceful)
                {
                    exit.SetIfUnset(ServerExit.NotGraceful);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.Warning("The host stopped waiting for the Ignite node to stop; it stops with the process.");
                exit.SetIfUnset(ServerExit.NotGraceful);
            }
        }

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
        string? springConfigPath = null;
        SslFileRestarter? sslFileRestarter = null;
        try
        {
            LogSecurityWarnings();
            springConfigPath = await SpringConfigRenderer.RenderToFileAsync(
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
            IgniteServerRunner created = new(settings, logger, DnsUtils.GetLocalIpAddress, springConfigPath, launcher);
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
            sslFileRestarter = SslFileRestarter.Create(settings, logger, created, () => IsStopping, Fail);
            // WaitAsync lets the host stop even when the node itself cannot.
            await created.Run().WaitAsync(stoppingToken).ConfigureAwait(false);

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
            sslFileRestarter?.Dispose();
            if (springConfigPath != null)
            {
                // Read by every (re)start of the node, so it is removed only now. It may contain keystore passwords.
                SpringConfigRenderer.DeleteRendered(springConfigPath);
            }
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
                $"The Spring configuration is not well-formed XML (check SPRING_EXTRA_BEANS_FILE and " +
                $"SPRING_EXTRA_PROPERTIES_FILE): {ex.Message}",
                ex
            );
        }
    }

    private void LogSecurityWarnings()
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
    }

    private void Fail()
    {
        exit.SetIfUnset(ServerExit.Failure);
        lifetime.StopApplication();
    }
}
