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

/// <summary>Starts, activates and stops one Ignite server node, configured by the settings.</summary>
internal sealed class IgniteServerRunner : IDisposable
{
    private static readonly TimeSpan ActivationPollInterval = TimeSpan.FromSeconds(5);

    private readonly ILogger logger;
    private readonly IgniteServerSettings settings;

    // Set when this node, as the cluster's last server node, deactivated the cluster in order to restart. The next
    // run then activates the cluster again without waiting for other nodes.
    private volatile bool deactivatedToRestart;
    private volatile bool disposed;
    private IIgnite? ignite;
    private volatile bool ready;
    private volatile bool stopRequested;

    public IgniteServerRunner(
        IgniteServerSettings settings,
        ILogger logger,
        Func<string> getLocalIpAddress,
        string? springConfigPath = null
    )
    {
        this.settings = settings;
        this.logger = logger;
        Configuration = IgniteConfigurationFactory.Create(settings, logger, getLocalIpAddress, springConfigPath);
        RebalanceTracker = new RebalanceTracker(
            this.logger,
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
    public IIgnite? Node => Volatile.Read(ref ignite);

    /// <summary>
    ///     True once the node is running, its cluster is active and (with READY_WHEN_REBALANCED) it has caught up after
    ///     its start (received its partitions), until it stops.
    /// </summary>
    public bool IsReady =>
        ready &&
        Node is { } node &&
        !(settings.ReadyWhenRebalanced && RebalanceTracker.IsCatchingUp(() => IsInBaseline(node)));

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        stopRequested = true;
        StopNode(true);
        disposed = true;
    }

    /// <summary>Starts the node, activates its cluster and returns once the node has stopped.</summary>
    public async Task Run()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (stopRequested)
        {
            return;
        }

        logger.Information("Starting Ignite server runner...");
        RebalanceTracker.Reset();
        var node = Ignition.Start(Configuration);
        TaskCompletionSource<string?> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        node.Stopped += (s, _) =>
        {
            ready = false;
            // The node may also have stopped on its own (e.g., segmented): it is no longer the running node.
            Interlocked.CompareExchange(ref ignite, null, node);
            stopped.TrySetResult(s?.ToString());
        };
        // From here on, Stop() and Restart() find this node and stop it.
        Volatile.Write(ref ignite, node);
        if (stopRequested)
        {
            // Stop() was called while the node was starting, when there was no node for it to stop.
            StopNode(false);
            logger.Information("'{IgniteInstance}' stopped.", node.Name);
            return;
        }

        var persistent = Configuration.DataStorageConfiguration?.DefaultDataRegionConfiguration?.PersistenceEnabled ==
                         true;
        if (!await ActivateCluster(node, persistent, stopped.Task).ConfigureAwait(false))
        {
            logger.Information("'{IgniteInstance}' stopped before activation.", node.Name);
            return;
        }

        // Lives as long as this run: it is disposed when the node has stopped.
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

        ready = !stopped.Task.IsCompleted;
        RebalanceTracker.OnRunning();
        logger.Information(
            "Ignite server runner is running (DiscoverySpi LocalPort={DiscoverySpiLocalPort}), press CTRL+C to " +
            "terminate.",
            (node.GetConfiguration().DiscoverySpi as TcpDiscoverySpi)?.LocalPort
        );
        var stoppedInstance = await stopped.Task.ConfigureAwait(false);
        logger.Information("'{IgniteInstance}' stopped.", stoppedInstance);
    }

    /// <summary>
    ///     Stops the running node gracefully so that <see cref="Run" /> can start it again (e.g., with renewed
    ///     certificates); nothing happens when no node is running. Returns false when the node did not stop within
    ///     <paramref name="timeout" /> (the GRACEFUL shutdown policy waits until the node's partitions have copies on other
    ///     nodes, e.g., while another node restarts). The cluster's last server node deactivates the cluster first, so it
    ///     stops at once, and activates it again once restarted.
    /// </summary>
    public bool Restart(TimeSpan timeout)
    {
        logger.Information("Restarting Ignite server runner...");
        return StopNodeGracefully(timeout, true);
    }

    /// <summary>
    ///     Stops the node gracefully; afterward, <see cref="Run" /> does not start it again. Returns false when the node
    ///     did not stop within <paramref name="timeout" />: the GRACEFUL shutdown policy waits until the node's partitions
    ///     have copies on other nodes, which never happens when the whole cluster stops; the node then stops with the process.
    ///     The cluster's last server node deactivates the cluster first, so it stops at once.
    /// </summary>
    public bool Stop(TimeSpan timeout)
    {
        stopRequested = true;
        ready = false;
        logger.Information("Stopping Ignite server runner...");
        if (StopNodeGracefully(timeout, false))
        {
            logger.Information("Ignite server runner stopped.");
            return true;
        }

        logger.Error(
            "The Ignite node did not stop gracefully within {Timeout} (it waits for other nodes to take over its " +
            "partitions); it stops with the process.",
            timeout
        );
        return false;
    }

    /// <summary>
    ///     Activates the cluster (a persistent one once enough server nodes have joined), lets its baseline follow the
    ///     topology and applies IGNITE_USER_PASSWORD. False when the node stopped before its cluster was activated.
    /// </summary>
    private async Task<bool> ActivateCluster(IIgnite node, bool persistent, Task stopped)
    {
        var cluster = node.GetCluster();
        // Before its restart, the cluster was active with this node alone (e.g., the others are down). It must not wait
        // for ACTIVATION_MIN_SERVER_NODES now, as a cluster that starts for the first time does.
        var reactivate = deactivatedToRestart;
        deactivatedToRestart = false;
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
                new IgniteUserPasswordManager(settings, logger).Apply(node, settings.IgniteUserPassword, newCluster);
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
    ///     Stops the running node gracefully within <paramref name="timeout" />, the cluster's last server node after
    ///     deactivating the cluster (which the next run activates again when <paramref name="restarting" />).
    /// </summary>
    private bool StopNodeGracefully(TimeSpan timeout, bool restarting)
    {
        var stopping = Stopwatch.StartNew();
        if (DeactivateIfLastServerNode(timeout) && restarting)
        {
            deactivatedToRestart = true;
        }

        var remaining = timeout - stopping.Elapsed;
        return StopNode(false, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
    }

    /// <summary>
    ///     No other node can take over the partitions of the cluster's last server node, which the GRACEFUL shutdown
    ///     policy would wait for until the timeout: that node deactivates the cluster (a persistent one checkpoints its data,
    ///     and the next start activates it again). True when it deactivated the cluster.
    /// </summary>
    private bool DeactivateIfLastServerNode(TimeSpan timeout)
    {
        if (Node is not { } node)
        {
            return false;
        }

        try
        {
            var cluster = node.GetCluster();
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
    ///     Stops the running node, if any, cancelling its jobs or waiting for them; false when it is still stopping after
    ///     <paramref name="timeout" /> (no timeout: waits until it has stopped).
    /// </summary>
    private bool StopNode(bool cancel, TimeSpan? timeout = null)
    {
        ready = false;
        if (Interlocked.Exchange(ref ignite, null) is not { } node)
        {
            return true;
        }

        return Task.Run(() => Ignition.Stop(node.Name, cancel)).Wait(timeout ?? Timeout.InfiniteTimeSpan);
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
                string.Join(", ", serverNodes.Select(node => node.ConsistentId))
            );
            await Task.WhenAny(stopped, Task.Delay(ActivationPollInterval)).ConfigureAwait(false);
        }
    }

    /// <summary>Whether the node's consistent id is in the baseline (false when it cannot tell, e.g., while stopping).</summary>
    internal static bool IsInBaseline(IIgnite node)
    {
        try
        {
            var cluster = node.GetCluster();
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
}
