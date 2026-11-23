using Apache.Ignite.Core;
using Apache.Ignite.Core.Discovery.Tcp;
using Apache.Ignite.Core.Events;
using AspNetCore.IgniteServer.Listeners;
using Serilog;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCore.IgniteServer;

/// <summary>
///     Starts, activates and stops one Ignite server node, configured by the settings.
///     <para>
///         Each run of a node moves through the phases of <see cref="Phase" />, which docs/architecture.md draws as a
///         state diagram: Starting, Activating and Running, then Restarting (and Starting again) or Stopping.
///     </para>
///     <para>
///         Concurrency: <see cref="Run" /> runs on one thread. <see cref="Restart" />, <see cref="Stop" /> and the node's
///         Stopped event come from other threads. The phase and the rest of the lifecycle state are guarded by one lock,
///         <c>gate</c>. Ignite is never called while that lock is held: starting, stopping and reading the cluster can
///         take long, and Ignite's threads call back into this class. Activating the cluster, and deactivating it when the
///         last server node stops, take turns in <see cref="ClusterActivator" />.
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

    /// <summary>The phases of a run, as in the state diagram of docs/architecture.md.</summary>
    internal enum Phase
    {
        /// <summary>Before the first start, and between the stop and the start of a restart.</summary>
        Idle,

        /// <summary>Ignition.Start runs. A restart cannot stop the node yet; a stop waits until it has started.</summary>
        Starting,

        /// <summary>
        ///     The node has started, and <see cref="ClusterActivator" /> activates its cluster. A persistent cluster that
        ///     starts for the first time waits for ACTIVATION_MIN_SERVER_NODES first.
        /// </summary>
        Activating,

        /// <summary>The cluster is active. The node is ready once it has caught up after its start (RebalanceTracker).</summary>
        Running,

        /// <summary>A restart stops the node. Run starts it again once it has stopped.</summary>
        Restarting,

        /// <summary>A stop stops the node, or does once it has started. Run starts no node again.</summary>
        Stopping,

        /// <summary>Run has returned: the node was stopped, or stopped on its own.</summary>
        Stopped,
    }

    private readonly ClusterActivator activator;
    private readonly Lock gate = new();
    private readonly INodeLauncher launcher;
    private readonly ILogger logger;
    private readonly bool persistent;
    private readonly IgniteServerSettings settings;

    // ---- Lifecycle state: read and written only under gate. ----

    // Set when this node, as the cluster's last server node, deactivated the cluster in order to restart. The next
    // run then activates the cluster again without waiting for ACTIVATION_MIN_SERVER_NODES.
    private bool deactivatedToRestart;
    private bool disposed;

    // The started node, from the end of its start until a stop takes it or it stops on its own.
    private IIgnite? node;

    // Completes when the current Ignition.Start returns. Already complete when no start is in progress.
    private Task nodeStart = Task.CompletedTask;

    // The latest stop. Ignite stops a node only once, so a later Stop() waits for this one rather than start another.
    private Task<bool>? nodeStop;

    private Phase phase;

    // Completes when the node of the current run begins to stop or restart, or stops on its own: its activation then
    // ends (ClusterActivator).
    private TaskCompletionSource? runStopping;

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
        persistent = Configuration.DataStorageConfiguration?.DefaultDataRegionConfiguration?.PersistenceEnabled == true;
        activator = new ClusterActivator(settings, logger, persistent, Configuration.AuthenticationEnabled);
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

    /// <summary>The phase of the current run.</summary>
    internal Phase CurrentPhase
    {
        get
        {
            lock (gate)
            {
                return phase;
            }
        }
    }

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
    ///     True while the node is running and its cluster is active. With READY_WHEN_REBALANCED, the node must also have
    ///     caught up after its start (received its partitions).
    /// </summary>
    public bool IsReady
    {
        get
        {
            IIgnite? readyNode;
            lock (gate)
            {
                readyNode = phase == Phase.Running ? node : null;
            }

            return readyNode != null &&
                   !(settings.ReadyWhenRebalanced &&
                     RebalanceTracker.IsCatchingUp(() => ClusterActivator.IsInBaseline(readyNode)));
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
            BeginStopUnderGate();
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
                if (phase != Phase.Restarting)
                {
                    phase = Phase.Stopped;
                    return;
                }

                // The node of the restart has stopped: the next run starts it again.
                phase = Phase.Idle;
            }
        }
    }

    private async Task RunOnce()
    {
        // Stop() waits for this start, so that it can stop the node gracefully. Otherwise it would report a stop that
        // only the process exit then makes.
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            // Under the same lock as the stop: a stop that came first means no start, and one that comes later waits
            // for this start.
            if (phase is Phase.Stopping or Phase.Stopped)
            {
                return;
            }

            phase = Phase.Starting;
            nodeStart = started.Task;
            runStopping = stopping;
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
                stopping.TrySetResult();
                stopped.TrySetResult(s?.ToString());
            };
            // From here on, Stop() and Restart() find this node and stop it.
            lock (gate)
            {
                node = startedNode;
                stopRequested = phase == Phase.Stopping;
                if (!stopRequested)
                {
                    phase = Phase.Activating;
                }
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

        bool reactivate;
        lock (gate)
        {
            // taken by this run only: a later restart deactivates the cluster again if it needs to
            reactivate = deactivatedToRestart;
            deactivatedToRestart = false;
        }

        bool activated;
        try
        {
            activated = await activator.ActivateAsync(startedNode, reactivate, stopping.Task).ConfigureAwait(false);
        }
        catch (Exception ex) when (stopping.Task.IsCompleted)
        {
            // A node that stops while its cluster is activated fails the calls of the activation.
            logger.Debug(ex, "The activation ended with the stop of the node.");
            activated = false;
        }

        if (!activated)
        {
            // Waited for, so that a restart does not start the next node beside this one.
            await stopped.Task.ConfigureAwait(false);
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

        // Before the node can be ready: IsReady asks the tracker whether it is still catching up after its start.
        RebalanceTracker.OnRunning();
        lock (gate)
        {
            // Not when a stop or restart began during the activation, or the node stopped on its own.
            if (phase == Phase.Activating && ReferenceEquals(node, startedNode))
            {
                phase = Phase.Running;
            }
        }

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
            // A node that is still starting may have read the previous files; SslFileRestarter asks again later.
            if (phase is not (Phase.Activating or Phase.Running) || node == null)
            {
                return RestartResult.NotRunning;
            }

            phase = Phase.Restarting;
            runStopping?.TrySetResult();
        }

        logger.Information("Restarting Ignite server runner...");
        if (StopNodeGracefully(timeout, true))
        {
            return RestartResult.Restarting;
        }

        lock (gate)
        {
            switch (phase)
            {
                case Phase.Restarting:
                    // The node still stops, later, and must not be started again: its run ends with that stop.
                    phase = Phase.Stopping;
                    return RestartResult.TimedOut;
                case Phase.Stopping or Phase.Stopped:
                    return RestartResult.NotRunning;
                default:
                    // Run already took the restart: the node stopped just after the timeout and is starting again.
                    return RestartResult.Restarting;
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
            BeginStopUnderGate();
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

    // A stop always wins over a restart, and ends the activation in progress. Once Run has returned, nothing is left to
    // stop.
    private void BeginStopUnderGate()
    {
        if (phase != Phase.Stopped)
        {
            phase = Phase.Stopping;
        }

        runStopping?.TrySetResult();
    }

    /// <summary>
    ///     Stops the node within <paramref name="timeout" />, or waits for a stop already in progress (e.g., of a restart that
    ///     timed out).
    ///     <para>
    ///         The GRACEFUL shutdown policy waits until the node's partitions have copies on other nodes. That can take long
    ///         (e.g., while another node restarts too), and never happens for the cluster's last server node. So the last
    ///         server node deactivates the cluster first (<see cref="ClusterActivator.DeactivateIfLastServerNode" />), and
    ///         when <paramref name="restarting" />, the next run activates it again.
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

        if (Node is { } runningNode &&
            activator.DeactivateIfLastServerNode(runningNode, Remaining(timeout, elapsed)) &&
            restarting)
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
    ///     Begins to stop the running node, cancelling its jobs or waiting for them, and returns that stop. Returns null
    ///     when no node is running.
    /// </summary>
    private Task<bool>? StopRunningNode(bool cancel)
    {
        lock (gate)
        {
            if (node is not { } runningNode)
            {
                return null;
            }

            node = null;
            runStopping?.TrySetResult();
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
}
