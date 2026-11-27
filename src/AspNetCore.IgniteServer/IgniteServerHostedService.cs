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
///     Runs the Ignite node for the lifetime of the host: renders its Spring configuration, starts it, restarts it when
///     its SSL files change (<see cref="SslFileRestarter" />), stops it with the host, and sets the exit code.
///     <para>
///         The constructor renders the Spring configuration to a file and creates the runner, so that the runner always
///         exists: a stop that comes before the node starts is handled by the runner itself
///         (<see cref="IgniteServerRunner.Run" /> then starts no node). An invalid configuration fails the construction,
///         and so the host's start, with exit code 1. The rendered file may contain keystore passwords: it is deleted
///         when the run ends (<see cref="RunAsync" />), when the construction fails, and in <see cref="Dispose" />.
///     </para>
///     <para>
///         Whether a stop was requested is the runner's to know: <see cref="IgniteServerRunner.Run" /> returns it.
///     </para>
/// </summary>
internal sealed class IgniteServerHostedService : BackgroundService
{
    private readonly ServerExit exit;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger logger;
    private readonly IgniteNodeState nodeState;
    private readonly IgniteServerRunner runner;
    private readonly IgniteServerSettings settings;

    // The rendered Spring configuration. Every (re)start of the node reads it, so it is deleted only once the node has
    // stopped for good.
    private readonly string springConfigPath;

    public IgniteServerHostedService(
        IgniteServerSettings settings,
        ILogger logger,
        IHostApplicationLifetime lifetime,
        IgniteNodeState nodeState,
        INodeLauncher launcher,
        ServerExit exit
    )
    {
        this.settings = settings;
        this.logger = logger.ForContext<IgniteServerHostedService>();
        this.lifetime = lifetime;
        this.nodeState = nodeState;
        this.exit = exit;
        LogSecurityWarnings();
        springConfigPath = SpringConfigRenderer.RenderToFile(
            Path.Combine(
                AppContext.BaseDirectory,
                "config",
                settings.UseClientSsl ? "spring-config-client-with-ssl.xml" : "spring-config.xml"
            ),
            settings.ToSpringConfigPlaceholders(),
            settings.ToSpringConfigXmlPlaceholders()
        );
        try
        {
            EnsureWellFormed(springConfigPath);
            runner = new IgniteServerRunner(settings, logger, DnsUtils.GetLocalIpAddress, springConfigPath, launcher);
        }
        catch
        {
            SpringConfigRenderer.DeleteRendered(springConfigPath);
            throw;
        }

        nodeState.SetRunner(runner);
    }

    // The host signals ApplicationStopping before it calls StopAsync.
    private bool IsStopping => lifetime.ApplicationStopping.IsCancellationRequested;

    // Since .NET 10, the host runs all of ExecuteAsync on a thread pool thread: the long Ignition.Start does not
    // delay the start of the host.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunAsync(stoppingToken);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            // On another thread, so that the host can give up at its own timeout. Validation keeps
            // GRACEFUL_STOP_TIMEOUT_IN_SECONDS below that timeout, so the host gives up first only when it was stopped
            // with a shorter one.
            var graceful = await Task.Run(
                    () => runner.Stop(TimeSpan.FromSeconds(settings.GracefulStopTimeoutInSeconds)),
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

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        nodeState.SetRunner(null);
        runner.Dispose();
        // RunAsync deletes it too; this covers a host that never ran the service.
        SpringConfigRenderer.DeleteRendered(springConfigPath);
        base.Dispose();
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var sslFileRestarter = SslFileRestarter.Create(settings, logger, runner, () => IsStopping, Fail);
        try
        {
            // WaitAsync lets the host stop even when the node itself cannot.
            var stopRequested = await runner.Run().WaitAsync(stoppingToken).ConfigureAwait(false);
            if (!stopRequested && !IsStopping)
            {
                logger.Error("The Ignite node stopped unexpectedly, stopping the server.");
                Fail();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping; a node that is still running stops with the process.
        }
        catch (Exception ex) when (!IsStopping)
        {
            logger.Fatal(ex, "The Ignite server failed.");
            Fail();
        }
        finally
        {
            sslFileRestarter?.Dispose();
            SpringConfigRenderer.DeleteRendered(springConfigPath);
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
