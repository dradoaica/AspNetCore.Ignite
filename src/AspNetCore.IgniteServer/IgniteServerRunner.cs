using Apache.Ignite.Core;
using Apache.Ignite.Core.Discovery.Tcp;
using Apache.Ignite.Core.Events;
using Apache.Ignite.Core.Lifecycle;
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
///         State: <see cref="Phase" /> says where the runner is (docs/architecture.md draws it as a state diagram, with a
///         table of what each call does in each phase). Each start of the node is a <see cref="NodeRun" />, which holds
///         that node and the signals of its start and stop. Only <see cref="Transition" /> changes the phase.
///     </para>
///     <para>
///         Threads: <see cref="Run" /> runs the node. <see cref="Restart" />, <see cref="Stop" />,
///         <see cref="Dispose" /> and the node's stop come from other threads. All the state is read and written under
///         one lock, <c>gate</c>. Ignite is never called while it is held, because starting, stopping and reading the
///         cluster can take long, and Ignite's threads call back into this class.
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

    /// <summary>The phases of the runner, as in the state diagram of docs/architecture.md.</summary>
    internal enum Phase
    {
        /// <summary>Before the first start, and between the stop and the next start of a restart.</summary>
        Idle,

        /// <summary>Ignition.Start runs. A restart is refused. A stop waits until the node has started, then stops it.</summary>
        Starting,

        /// <summary>
        ///     The node has started and <see cref="ClusterActivator" /> activates its cluster. A persistent cluster that
        ///     starts for the first time first waits for ACTIVATION_MIN_SERVER_NODES.
        /// </summary>
        Activating,

        /// <summary>The cluster is active. The node is ready once it has caught up after its start (RebalanceTracker).</summary>
        Running,

        /// <summary>A restart stops the node. Once it has stopped, Run starts it again.</summary>
        Restarting,

        /// <summary>A stop was requested. Run starts no node again.</summary>
        Stopping,

        /// <summary>Run has returned. Final.</summary>
        Stopped,
    }

    // The phase changes the runner makes; Transition refuses any other. Staying in the same phase is always allowed.
    private static readonly Dictionary<Phase, Phase[]> AllowedTransitions = new()
    {
        [Phase.Idle] = [Phase.Starting, Phase.Stopping, Phase.Stopped,],
        [Phase.Starting] = [Phase.Activating, Phase.Stopping, Phase.Stopped,],
        [Phase.Activating] = [Phase.Running, Phase.Restarting, Phase.Stopping, Phase.Stopped,],
        [Phase.Running] = [Phase.Restarting, Phase.Stopping, Phase.Stopped,],
        [Phase.Restarting] = [Phase.Idle, Phase.Stopping, Phase.Stopped,],
        [Phase.Stopping] = [Phase.Stopped,],
        [Phase.Stopped] = [],
    };

    private readonly ClusterActivator activator;
    private readonly Lock gate = new();
    private readonly INodeLauncher launcher;
    private readonly ILogger logger;
    private readonly bool persistent;
    private readonly IgniteServerSettings settings;

    // ---- Read and written only under gate. ----

    // The latest start of the node; null before the first one.
    private NodeRun? currentRun;

    // True when the node, as the last server node of its cluster, deactivated the cluster to restart. The next run then
    // activates the cluster at once, without waiting for ACTIVATION_MIN_SERVER_NODES.
    private bool deactivatedToRestart;
    private bool disposed;
    private Phase phase;

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
        // Both are registered in the configuration rather than on the started node: rebalancing can begin while the
        // node is still joining, and the node can stop before Ignition.Start has even returned it.
        Configuration.LocalEventListeners =
        [
            .. Configuration.LocalEventListeners ?? [],
            new LocalEventListener<CacheRebalancingEvent>
            {
                EventTypes = [EventType.CacheRebalanceStarted, EventType.CacheRebalanceStopped,],
                Listener = RebalanceTracker,
            },
        ];
        Configuration.LifecycleHandlers = [.. Configuration.LifecycleHandlers ?? [], new NodeStopHandler(this),];
    }

    /// <summary>The caches this node is rebalancing.</summary>
    public RebalanceTracker RebalanceTracker { get; }

    internal IgniteConfiguration Configuration { get; }

    private TimeSpan GracefulStopTimeout => TimeSpan.FromSeconds(settings.GracefulStopTimeoutInSeconds);

    /// <summary>The phase of the runner.</summary>
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

    /// <summary>The running node, or null while it is (re)starting, stopping or stopped.</summary>
    public IIgnite? Node
    {
        get
        {
            lock (gate)
            {
                return RunningNodeUnderGate();
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
                readyNode = phase == Phase.Running ? RunningNodeUnderGate() : null;
            }

            return readyNode != null &&
                   !(settings.ReadyWhenRebalanced &&
                     RebalanceTracker.IsCatchingUp(() => ClusterActivator.IsInBaseline(readyNode)));
        }
    }

    /// <summary>
    ///     Stops a node that is still running, cancelling its jobs, and waits for that at most
    ///     GRACEFUL_STOP_TIMEOUT_IN_SECONDS. A stop that began earlier (e.g., one that timed out) is not waited for: that
    ///     node stops with the process. Never throws.
    /// </summary>
    public void Dispose()
    {
        Task<bool>? stop;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            RequestStopUnderGate();
            stop = currentRun is { Stop: null, } run
                ? BeginNodeStopUnderGate(run, node => launcher.Stop(node.Name, true)) : null;
        }

        try
        {
            if (stop != null && !stop.Wait(GracefulStopTimeout))
            {
                logger.Warning(
                    "The Ignite node did not stop within {Timeout}; it stops with the process.",
                    GracefulStopTimeout
                );
            }
        }
        catch (AggregateException ex)
        {
            logger.Warning(ex.InnerException ?? ex, "The Ignite node failed to stop; it stops with the process.");
        }
    }

    /// <summary>
    ///     Starts the node and activates its cluster, and starts it again after each <see cref="Restart" />. Returns once
    ///     the node has stopped for good: true when that was requested (<see cref="Stop" /> or <see cref="Dispose" />),
    ///     false when the node stopped on its own (e.g., segmented).
    /// </summary>
    public async Task<bool> Run()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }

        try
        {
            while (true)
            {
                await RunOnce().ConfigureAwait(false);
                lock (gate)
                {
                    if (phase != Phase.Restarting)
                    {
                        return phase == Phase.Stopping;
                    }

                    Transition(Phase.Idle);
                }
            }
        }
        finally
        {
            lock (gate)
            {
                Transition(Phase.Stopped);
            }
        }
    }

    /// <summary>One start of the node. Returns only once that node has stopped, so two nodes never run at once.</summary>
    private async Task RunOnce()
    {
        NodeRun run = new();
        lock (gate)
        {
            // A stop that came before means no start. One that comes later finds this run and waits for its start.
            if (phase is Phase.Stopping or Phase.Stopped)
            {
                return;
            }

            Transition(Phase.Starting);
            currentRun = run;
        }

        logger.Information("Starting Ignite server runner...");
        RebalanceTracker.Reset();
        IIgnite node;
        bool stopRequested;
        try
        {
            node = launcher.Start(Configuration);
            lock (gate)
            {
                run.Node = node;
                stopRequested = phase == Phase.Stopping;
                if (!stopRequested)
                {
                    Transition(Phase.Activating);
                }
            }
        }
        finally
        {
            run.Started.TrySetResult();
        }

        if (stopRequested)
        {
            // Stop() was called while the node started, and stops it now. This begins the same stop in case Stop()
            // gave up waiting for the start (it then returned false).
            _ = BeginGracefulStop(run, GracefulStopTimeout, false);
            await run.Stopped.Task.ConfigureAwait(false);
            logger.Information("'{IgniteInstance}' stopped.", node.Name);
            return;
        }

        bool reactivate;
        lock (gate)
        {
            // Used by this run only. A later restart sets it again if it deactivates the cluster again.
            reactivate = deactivatedToRestart;
            deactivatedToRestart = false;
        }

        bool activated;
        try
        {
            activated = await activator.ActivateAsync(node, reactivate, run.Stopping.Task).ConfigureAwait(false);
        }
        catch (Exception ex) when (run.Stopping.Task.IsCompleted)
        {
            // The node was stopped during the activation, which then fails to call it.
            logger.Debug(ex, "The activation ended with the stop of the node.");
            activated = false;
        }

        if (!activated)
        {
            await run.Stopped.Task.ConfigureAwait(false);
            logger.Information("'{IgniteInstance}' stopped before activation.", node.Name);
            return;
        }

        // Disposed when this node has stopped.
        using LostPartitionsListener lostPartitions = new(node, logger, settings.AutoResetLostPartitions, persistent);
        var events = node.GetEvents();
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

        // Before the node can be ready: IsReady asks the tracker whether the node is still catching up.
        RebalanceTracker.OnRunning();
        lock (gate)
        {
            // Unless a stop or restart began during the activation.
            if (phase == Phase.Activating)
            {
                Transition(Phase.Running);
            }
        }

        logger.Information(
            "Ignite server runner is running (DiscoverySpi LocalPort={DiscoverySpiLocalPort}), press CTRL+C to " +
            "terminate.",
            (node.GetConfiguration().DiscoverySpi as TcpDiscoverySpi)?.LocalPort
        );
        await run.Stopped.Task.ConfigureAwait(false);
        logger.Information("'{IgniteInstance}' stopped.", node.Name);
    }

    /// <summary>
    ///     Stops the running node gracefully so that <see cref="Run" /> starts it again (e.g., with renewed certificates),
    ///     within <paramref name="timeout" />.
    /// </summary>
    public RestartResult Restart(TimeSpan timeout)
    {
        NodeRun run;
        lock (gate)
        {
            // A node that is still starting may have read the previous files: SslFileRestarter asks again later.
            if (phase is not (Phase.Activating or Phase.Running) || RunningNodeUnderGate() == null)
            {
                return RestartResult.NotRunning;
            }

            Transition(Phase.Restarting);
            run = currentRun!;
            run.Stopping.TrySetResult();
        }

        logger.Information("Restarting Ignite server runner...");
        if (StopGracefully(run, timeout, true))
        {
            return RestartResult.Restarting;
        }

        lock (gate)
        {
            switch (phase)
            {
                case Phase.Restarting:
                    // The node stops later, and must not be started again then.
                    Transition(Phase.Stopping);
                    return RestartResult.TimedOut;
                case Phase.Stopping or Phase.Stopped:
                    return RestartResult.NotRunning;
                default:
                    // The node stopped just after the timeout, and Run is already starting it again.
                    return RestartResult.Restarting;
            }
        }
    }

    /// <summary>
    ///     Stops the node gracefully; <see cref="Run" /> then starts no node again. Returns false when the node did not
    ///     stop within <paramref name="timeout" />: it then stops with the process.
    /// </summary>
    public bool Stop(TimeSpan timeout)
    {
        NodeRun? run;
        lock (gate)
        {
            RequestStopUnderGate();
            run = currentRun;
        }

        logger.Information("Stopping Ignite server runner...");
        // No run: Run has not started the node, and now never will.
        if (run == null || StopGracefully(run, timeout, false))
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

    // A stop always wins over a restart, and ends the activation in progress. Once Run has returned, there is nothing
    // left to stop.
    private void RequestStopUnderGate()
    {
        if (phase != Phase.Stopped)
        {
            Transition(Phase.Stopping);
        }

        currentRun?.Stopping.TrySetResult();
    }

    /// <summary>
    ///     Waits for the node of <paramref name="run" /> to start, then stops it (or joins a stop already in progress),
    ///     all within <paramref name="timeout" />. Returns false when the node did not stop in time.
    /// </summary>
    private bool StopGracefully(NodeRun run, TimeSpan timeout, bool restarting)
    {
        var elapsed = Stopwatch.StartNew();
        if (!run.Started.Task.Wait(timeout))
        {
            return false;
        }

        var stop = BeginGracefulStop(run, Remaining(timeout, elapsed), restarting);
        // No node: its start failed, so there is nothing to stop.
        return stop?.Wait(Remaining(timeout, elapsed)) ?? true;
    }

    /// <summary>
    ///     Begins the graceful stop of the started node of <paramref name="run" />, unless a stop began already, and
    ///     returns the stop in progress.
    ///     <para>
    ///         The GRACEFUL shutdown policy waits until the node's partitions have copies on other nodes. That never
    ///         happens for the last server node of a cluster, so that node deactivates the cluster first
    ///         (<see cref="ClusterActivator.DeactivateIfLastServerNode" />). When <paramref name="restarting" />, the next
    ///         run then activates it again.
    ///     </para>
    /// </summary>
    private Task<bool>? BeginGracefulStop(NodeRun run, TimeSpan deactivationTimeout, bool restarting)
    {
        lock (gate)
        {
            return BeginNodeStopUnderGate(
                run,
                node =>
                {
                    if (activator.DeactivateIfLastServerNode(node, deactivationTimeout) && restarting)
                    {
                        lock (gate)
                        {
                            deactivatedToRestart = true;
                        }
                    }

                    return launcher.Stop(node.Name, false);
                }
            );
        }
    }

    /// <summary>
    ///     The node of a run is stopped once (Ignite stops a node only once): the first caller begins
    ///     <paramref name="stop" /> on a thread pool thread, and later callers get that same stop. Null while the node is
    ///     still starting, or when its start failed.
    /// </summary>
    private static Task<bool>? BeginNodeStopUnderGate(NodeRun run, Func<IIgnite, bool> stop)
    {
        if (run.Node is not { } node)
        {
            return null;
        }

        return run.Stop ??= run.Stopped.Task.IsCompleted ? Task.FromResult(true) : Task.Run(() => stop(node));
    }

    /// <summary>
    ///     The node of the current run while it is running: started, not yet stopped, and with no stop or restart
    ///     requested. The phase stays Activating or Running when the node stops on its own, until Run returns.
    /// </summary>
    private IIgnite? RunningNodeUnderGate() =>
        phase is Phase.Activating or Phase.Running && currentRun is { Stopped.Task.IsCompleted: false, } run
            ? run.Node : null;

    /// <summary>Called by Ignite (<see cref="NodeStopHandler" />) once the node has stopped, however it stopped.</summary>
    private void OnNodeStopped()
    {
        lock (gate)
        {
            // Only one node runs at a time (RunOnce returns once its node has stopped), so this is the current one.
            currentRun?.Stopping.TrySetResult();
            currentRun?.Stopped.TrySetResult();
        }
    }

    private void Transition(Phase to)
    {
        if (phase != to && !AllowedTransitions[phase].Contains(to))
        {
            throw new InvalidOperationException($"The runner cannot go from {phase} to {to}.");
        }

        phase = to;
    }

    private static TimeSpan Remaining(TimeSpan timeout, Stopwatch elapsed) =>
        timeout > elapsed.Elapsed ? timeout - elapsed.Elapsed : TimeSpan.Zero;

    /// <summary>One start of the node, until it has stopped. Its fields are written under <c>gate</c>.</summary>
    private sealed class NodeRun
    {
        /// <summary>Completes when Ignition.Start has returned (or thrown).</summary>
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the node has stopped.</summary>
        public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        ///     Completes when a stop or restart is requested, or the node has stopped: the activation then ends
        ///     (<see cref="ClusterActivator" />).
        /// </summary>
        public readonly TaskCompletionSource Stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The started node; null until Ignition.Start returns it.</summary>
        public IIgnite? Node;

        /// <summary>The stop of the node, once one began.</summary>
        public Task<bool>? Stop;
    }

    /// <summary>
    ///     Tells the runner that its node has stopped. Registered in the configuration, so it is in place before the node
    ///     starts: unlike the node's Stopped event, it also sees a node that stops before Ignition.Start returns.
    /// </summary>
    private sealed class NodeStopHandler(IgniteServerRunner runner) : ILifecycleHandler
    {
        public void OnLifecycleEvent(LifecycleEventType evt)
        {
            if (evt == LifecycleEventType.AfterNodeStop)
            {
                runner.OnNodeStopped();
            }
        }
    }
}
