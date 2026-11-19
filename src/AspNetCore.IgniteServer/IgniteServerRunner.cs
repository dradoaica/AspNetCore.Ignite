using Apache.Ignite.Core;
using Apache.Ignite.Core.Cluster;
using Apache.Ignite.Core.Common;
using Apache.Ignite.Core.Discovery.Tcp;
using Apache.Ignite.Core.Events;
using AspNetCore.IgniteServer.Listeners;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCore.IgniteServer;

/// <summary>
///     Starts, activates and stops one Ignite server node, configured by the settings.
///     <para>
///         Concurrency: <see cref="Run" /> runs on one thread, while <see cref="Restart" />, <see cref="Stop" /> and
///         the node's Stopped event come from others. All lifecycle state is guarded by one lock, <c>gate</c>. Ignite
///         itself is never called under that lock, because starting, stopping and reading the cluster can take long,
///         and Ignite's threads call back into this class.
///     </para>
/// </summary>
internal sealed class IgniteServerRunner : IDisposable
{
    /// <summary>The outcome of <see cref="Restart" />.</summary>
    public enum RestartResult
    {
        /// <summary>The node stopped, and <see cref="Run" /> starts it again.</summary>
        Restarting,

        /// <summary>No node is running (it is starting or stopping), so nothing was done.</summary>
        NotRunning,

        /// <summary>The node did not stop within the timeout. It still stops, but is not started again.</summary>
        TimedOut,
    }

    private static readonly TimeSpan ActivationPollInterval = TimeSpan.FromSeconds(5);

    private readonly Lock gate = new();
    private readonly INodeLauncher launcher;
    private readonly ILogger logger;
    private readonly IgniteServerSettings settings;

    // ---- Lifecycle state: read and written only under gate. ----

    // Set when this node, as the cluster's last server node, deactivated the cluster in order to restart. The next
    // run then activates the cluster again without waiting for ACTIVATION_MIN_SERVER_NODES.
    private bool deactivatedToRestart;
    private bool disposed;

    // The started node, until a stop begins (or it stops on its own). Null while it starts and once it stops.
    private IIgnite? node;

    // Completes when the current Ignition.Start returns. Already complete when no start is in progress.
    private Task nodeStart = Task.CompletedTask;

    // The latest stop. Ignite stops a node only once, so a later Stop() waits for this one rather than start another.
    private Task<bool>? nodeStop;

    // True from the cluster's activation until a stop begins.
    private bool ready;

    // What Restart() or Stop() asked for. Stop is final. Run consumes Restart once the node has stopped.
    private Request request;

    public IgniteServerRunner(
        IgniteServerSettings settings,
        ILogger logger,
        Func<string> getLocalIpAddress,
        string? springConfigPath = null,
        INodeLauncher? launcher = null
    )
    {
        this.settings = settings;
        this.logger = logger.ForContext<IgniteServerRunner>();
        this.launcher = launcher ?? IgnitionNodeLauncher.Instance;
        Configuration = IgniteConfigurationFactory.Create(settings, logger, getLocalIpAddress, springConfigPath);
        RebalanceTracker = new RebalanceTracker(
            logger,
            TimeSpan.FromSeconds(settings.RebalanceReadinessMaxWaitInSeconds)
        );
        // Registered in the configuration, not with LocalListen after the start, because rebalancing can begin while
        // the node is still joining.
        Configuration.LocalEventListeners =
        [
            .. Configuration.LocalEventListeners ?? [],
            new LocalEventListener<CacheRebalancingEvent>
            {
                EventTypes = [EventType.CacheRebalanceStarted, EventType.CacheRebalanceStopped,],
                Listener = RebalanceTracker,
            },
        ];
    }

    /// <summary>The caches this node is rebalancing.</summary>
    public RebalanceTracker RebalanceTracker { get; }

    internal IgniteConfiguration Configuration { get; }

    /// <summary>The running node, or null while it is (re)starting or stopped.</summary>
    public IIgnite? Node
    {
        get
        {
            lock (gate)
            {
                return node;
            }
        }
    }

    /// <summary>
    ///     True once the node is running and its cluster is active, until it stops. With READY_WHEN_REBALANCED, the node
    ///     must also have caught up after its start (received its partitions).
    /// </summary>
    public bool IsReady
    {
        get
        {
            IIgnite? readyNode;
            lock (gate)
            {
                readyNode = ready ? node : null;
            }

            return readyNode != null &&
                   !(settings.ReadyWhenRebalanced && RebalanceTracker.IsCatchingUp(() => IsInBaseline(readyNode)));
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            request = Request.Stop;
        }

        // A node that is still running is stopped, cancelling its jobs. A stop that began earlier (e.g., one that timed
        // out) is not waited for: that node stops with the process.
        var stop = StopRunningNode(true);
        var timeout = TimeSpan.FromSeconds(settings.GracefulStopTimeoutInSeconds);
        if (stop != null && !stop.Wait(timeout))
        {
            logger.Warning("The Ignite node did not stop within {Timeout}; it stops with the process.", timeout);
        }
    }

    /// <summary>
    ///     Starts the node and activates its cluster, again after each <see cref="Restart" />. Returns once the node has
    ///     stopped for any other reason (<see cref="Stop" />, or on its own).
    /// </summary>
    public async Task Run()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }

        while (true)
        {
            await RunOnce().ConfigureAwait(false);
            lock (gate)
            {
                if (request != Request.Restart)
                {
                    return;
                }

                request = Request.None;
            }
        }
    }

    private async Task RunOnce()
    {
        // Stop() waits for this start, so that it can stop the node gracefully. Otherwise it would report a stop that
        // only the process exit then makes.
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            // Checked together with publishing the start: Stop() either sees the start and waits for it, or the start
            // sees the stop and does not happen.
            if (request == Request.Stop)
            {
                return;
            }

            nodeStart = started.Task;
        }

        logger.Information("Starting Ignite server runner...");
        RebalanceTracker.Reset();
        TaskCompletionSource<string?> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IIgnite startedNode;
        bool stopRequested;
        try
        {
            startedNode = launcher.Start(Configuration);
            startedNode.Stopped += (s, _) =>
            {
                OnNodeStopped(startedNode);
                stopped.TrySetResult(s?.ToString());
            };
            // From here on, Stop() and Restart() find this node and stop it.
            lock (gate)
            {
                node = startedNode;
                stopRequested = request == Request.Stop;
            }
        }
        finally
        {
            started.TrySetResult();
        }

        if (stopRequested)
        {
            // Stop() was called while the node was starting. Stop() stops the node now that it has started, unless its
            // timeout ran out first; then this stops it.
            (StopRunningNode(false) ?? LatestStop())?.Wait();
            logger.Information("'{IgniteInstance}' stopped.", startedNode.Name);
            return;
        }

        var persistent = Configuration.DataStorageConfiguration?.DefaultDataRegionConfiguration?.PersistenceEnabled ==
                         true;
        if (!await ActivateCluster(startedNode, persistent, stopped.Task).ConfigureAwait(false))
        {
            logger.Information("'{IgniteInstance}' stopped before activation.", startedNode.Name);
            return;
        }

        // Lives as long as this run: it is disposed when the node has stopped.
        using LostPartitionsListener lostPartitions = new(
            startedNode,
            logger,
            settings.AutoResetLostPartitions,
            persistent
        );
        var events = startedNode.GetEvents();
        events.LocalListen<CacheRebalancingEvent>(lostPartitions, EventType.CacheRebalancePartDataLost);
        events.LocalListen<DiscoveryEvent>(
            lostPartitions,
            EventType.NodeJoined,
            EventType.NodeLeft,
            EventType.NodeFailed
        );
        events.LocalListen(
            new DiscoveryEventListener(logger),
            EventType.NodeJoined,
            EventType.NodeLeft,
            EventType.NodeFailed,
            EventType.NodeSegmented
        );

        lock (gate)
        {
            // Not ready when a stop began during the activation.
            ready = ReferenceEquals(node, startedNode);
        }

        RebalanceTracker.OnRunning();
        logger.Information(
            "Ignite server runner is running (DiscoverySpi LocalPort={DiscoverySpiLocalPort}), press CTRL+C to " +
            "terminate.",
            (startedNode.GetConfiguration().DiscoverySpi as TcpDiscoverySpi)?.LocalPort
        );
        var stoppedInstance = await stopped.Task.ConfigureAwait(false);
        logger.Information("'{IgniteInstance}' stopped.", stoppedInstance);
    }

    /// <summary>
    ///     The node's Stopped event. A node that stops on its own (e.g., segmented) is still the running node here; one
    ///     stopped by <see cref="StopRunningNode" /> no longer is.
    /// </summary>
    private void OnNodeStopped(IIgnite stoppedNode)
    {
        lock (gate)
        {
            if (ReferenceEquals(node, stoppedNode))
            {
                node = null;
                ready = false;
            }
        }
    }

    /// <summary>
    ///     Stops the running node gracefully so that <see cref="Run" /> starts it again (e.g., with renewed
    ///     certificates), within <paramref name="timeout" /> (see <see cref="StopNodeGracefully" />).
    /// </summary>
    public RestartResult Restart(TimeSpan timeout)
    {
        lock (gate)
        {
            if (node == null || request == Request.Stop)
            {
                return RestartResult.NotRunning;
            }

            request = Request.Restart;
        }

        logger.Information("Restarting Ignite server runner...");
        if (StopNodeGracefully(timeout, true))
        {
            return RestartResult.Restarting;
        }

        lock (gate)
        {
            switch (request)
            {
                case Request.Restart:
                    // The node still stops, later. Withdrawing the request keeps Run from starting it again.
                    request = Request.None;
                    return RestartResult.TimedOut;
                case Request.None:
                    // Run already took the request: the node stopped just after the timeout and is starting again.
                    return RestartResult.Restarting;
                default:
                    return RestartResult.NotRunning;
            }
        }
    }

    /// <summary>
    ///     Stops the node gracefully; afterward, <see cref="Run" /> does not start it again. Returns false when the node
    ///     did not stop within <paramref name="timeout" /> (see <see cref="StopNodeGracefully" />); it then stops with the
    ///     process.
    /// </summary>
    public bool Stop(TimeSpan timeout)
    {
        lock (gate)
        {
            request = Request.Stop;
            ready = false;
        }

        logger.Information("Stopping Ignite server runner...");
        if (StopNodeGracefully(timeout, false))
        {
            logger.Information("Ignite server runner stopped.");
            return true;
        }

        logger.Error(
            "The Ignite node did not stop gracefully within {Timeout} (it waits for other nodes to take over its " +
            "partitions, or it was still starting); it stops with the process.",
            timeout
        );
        return false;
    }

    /// <summary>
    ///     Activates the cluster (a persistent one once enough server nodes have joined), lets its baseline follow the
    ///     topology and applies IGNITE_USER_PASSWORD. Returns false when the node stopped before its cluster was
    ///     activated.
    /// </summary>
    private async Task<bool> ActivateCluster(IIgnite startedNode, bool persistent, Task stopped)
    {
        var cluster = startedNode.GetCluster();
        // Before its restart, this node was the cluster's only server node (e.g., the others are down), so it must not
        // wait for ACTIVATION_MIN_SERVER_NODES now, as a cluster that starts for the first time does.
        bool reactivate;
        lock (gate)
        {
            reactivate = deactivatedToRestart;
            deactivatedToRestart = false;
        }

        if (persistent && !reactivate)
        {
            await WaitForServerNodes(cluster, stopped).ConfigureAwait(false);
        }

        if (stopped.IsCompleted)
        {
            return false;
        }

        // A persistent cluster without a baseline was never activated, so its users still have their default passwords.
        var newCluster = persistent && GetBaselineTopology(cluster) == null;
        if (!cluster.IsActive())
        {
            cluster.SetActive(true);
            logger.Information(
                "The cluster was activated{Again:l} ({ServerNodes} server nodes).",
                reactivate ? " again after the restart of its last server node" : "",
                cluster.ForServers().GetNodes().Count
            );
        }

        cluster.SetBaselineAutoAdjustEnabledFlag(true);
        cluster.SetBaselineAutoAdjustTimeout((long)settings.GetBaselineAutoAdjustTimeout(persistent).TotalMilliseconds);
        // Users exist only with authentication. Without it, there is no password to set.
        if (Configuration.AuthenticationEnabled && !string.IsNullOrWhiteSpace(settings.IgniteUserPassword))
        {
            try
            {
                new IgniteUserPasswordManager(settings, logger).Apply(
                    startedNode,
                    settings.IgniteUserPassword,
                    newCluster
                );
            }
            catch (Exception ex)
            {
                // The node keeps running with its current password, as it would without IGNITE_USER_PASSWORD.
                logger.Error(ex, "IGNITE_USER_PASSWORD was not applied.");
            }
        }

        return true;
    }

    /// <summary>
    ///     Stops the node within <paramref name="timeout" />, or waits for a stop already in progress (e.g., of a restart
    ///     that timed out).
    ///     <para>
    ///         The GRACEFUL shutdown policy waits until the node's partitions have copies on other nodes. That can take
    ///         long (e.g., while another node restarts too), and never happens for the cluster's last server node. So the
    ///         last server node deactivates the cluster first (<see cref="DeactivateIfLastServerNode" />), and when
    ///         <paramref name="restarting" />, the next run activates it again.
    ///     </para>
    /// </summary>
    private bool StopNodeGracefully(TimeSpan timeout, bool restarting)
    {
        var elapsed = Stopwatch.StartNew();
        Task start;
        lock (gate)
        {
            start = nodeStart;
        }

        // A node that is still starting is stopped once started, within the same timeout.
        if (!start.Wait(timeout))
        {
            return false;
        }

        if (DeactivateIfLastServerNode(Remaining(timeout, elapsed)) && restarting)
        {
            lock (gate)
            {
                deactivatedToRestart = true;
            }
        }

        var stop = StopRunningNode(false) ?? LatestStop();
        return stop?.Wait(Remaining(timeout, elapsed)) ?? true;
    }

    private static TimeSpan Remaining(TimeSpan timeout, Stopwatch elapsed) =>
        timeout > elapsed.Elapsed ? timeout - elapsed.Elapsed : TimeSpan.Zero;

    /// <summary>
    ///     The cluster's last server node deactivates the cluster before it stops, so that it can stop at once (a
    ///     persistent one checkpoints its data, and the next start activates it again). Returns true when it deactivated
    ///     the cluster.
    /// </summary>
    private bool DeactivateIfLastServerNode(TimeSpan timeout)
    {
        if (Node is not { } runningNode)
        {
            return false;
        }

        try
        {
            var cluster = runningNode.GetCluster();
            if (!cluster.IsActive() || cluster.ForServers().GetNodes().Count > 1)
            {
                return false;
            }

            logger.Information(
                "The node is the cluster's last server node: deactivating the cluster so that it stops at once."
            );
            if (Task.Run(() => cluster.SetActive(false)).Wait(timeout))
            {
                return true;
            }

            logger.Warning("The cluster was not deactivated within {Timeout}.", timeout);
        }
        catch (Exception ex) when (ex is IgniteException or InvalidOperationException or AggregateException)
        {
            // Not fatal: the graceful stop that follows may still succeed, or time out.
            logger.Warning(ex, "The cluster was not deactivated before the stop.");
        }

        return false;
    }

    /// <summary>
    ///     Begins to stop the running node, cancelling its jobs or waiting for them, and returns that stop. Returns null
    ///     when no node is running.
    /// </summary>
    private Task<bool>? StopRunningNode(bool cancel)
    {
        lock (gate)
        {
            ready = false;
            if (node is not { } runningNode)
            {
                return null;
            }

            node = null;
            nodeStop = Task.Run(() => launcher.Stop(runningNode.Name, cancel));
            return nodeStop;
        }
    }

    private Task<bool>? LatestStop()
    {
        lock (gate)
        {
            return nodeStop;
        }
    }

    /// <summary>
    ///     A persistent cluster records its baseline topology on activation, so wait for the configured number of server
    ///     nodes before activating it (unless it is already active, e.g., when resuming).
    /// </summary>
    private async Task WaitForServerNodes(ICluster cluster, Task stopped)
    {
        while (!stopped.IsCompleted && !cluster.IsActive())
        {
            var serverNodes = cluster.ForServers().GetNodes();
            if (serverNodes.Count >= settings.ActivationMinServerNodes)
            {
                return;
            }

            logger.Information(
                "Waiting for {ActivationMinServerNodes} server nodes before activating the cluster ({ServerNodes} " +
                "joined: {JoinedNodes})...",
                settings.ActivationMinServerNodes,
                serverNodes.Count,
                string.Join(", ", serverNodes.Select(serverNode => serverNode.ConsistentId))
            );
            await Task.WhenAny(stopped, Task.Delay(ActivationPollInterval)).ConfigureAwait(false);
        }
    }

    /// <summary>Whether the node's consistent id is in the baseline (false when it cannot tell, e.g., while stopping).</summary>
    internal static bool IsInBaseline(IIgnite ignite)
    {
        try
        {
            var cluster = ignite.GetCluster();
            var consistentId = cluster.GetLocalNode().ConsistentId;
            return GetBaselineTopology(cluster)?.Any(baselineNode => Equals(baselineNode.ConsistentId, consistentId)) ==
                   true;
        }
        catch (Exception ex) when (ex is IgniteException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    ///     The baseline topology, or null when none was set yet (Ignite.NET documents null but throws a Java
    ///     NullPointerException instead).
    /// </summary>
    internal static ICollection<IBaselineNode>? GetBaselineTopology(ICluster cluster)
    {
        try
        {
            return cluster.GetBaselineTopology();
        }
        catch (IgniteException ex) when (ex.Message.Contains("NullPointerException", StringComparison.Ordinal))
        {
            return null;
        }
    }

    private enum Request
    {
        None,
        Restart,
        Stop,
    }
}
