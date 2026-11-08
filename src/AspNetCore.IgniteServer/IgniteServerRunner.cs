using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apache.Ignite.Core;
using Apache.Ignite.Core.Cluster;
using Apache.Ignite.Core.Common;
using Apache.Ignite.Core.Discovery.Tcp;
using Apache.Ignite.Core.Discovery.Tcp.Static;
using Apache.Ignite.Core.Events;
using AspNetCore.IgniteServer.Listeners;
using Serilog;
using Serilog.Core;

namespace AspNetCore.IgniteServer;

/// <summary>Configures, starts, activates and stops one Ignite server node.</summary>
internal sealed class IgniteServerRunner : IDisposable
{
    private static readonly TimeSpan ActivationPollInterval = TimeSpan.FromSeconds(5);

    private readonly ILogger logger;
    private readonly IgniteServerRunnerOptions options;
    private volatile bool disposed;
    private IIgnite? ignite;
    private volatile bool ready;
    private volatile bool stopRequested;

    public IgniteServerRunner(IgniteServerRunnerOptions options, ILogger? logger = null)
    {
        this.options = options;
        this.logger = logger ?? Logger.None;
        Configuration = IgniteConfigurationFactory.Create(options, this.logger);
        RebalanceTracker = new RebalanceTracker(this.logger, options.RebalanceReadinessMaxWait);
        // in the configuration (not LocalListen after the start): rebalancing may start while the node joins
        Configuration.LocalEventListeners =
        [
            .. Configuration.LocalEventListeners ?? [],
            new LocalEventListener<CacheRebalancingEvent>
            {
                EventTypes = [EventType.CacheRebalanceStarted, EventType.CacheRebalanceStopped],
                Listener = RebalanceTracker
            }
        ];
    }

    /// <summary>The caches this node is rebalancing.</summary>
    public RebalanceTracker RebalanceTracker { get; }

    internal IgniteConfiguration Configuration { get; }

    /// <summary>The running node, or null while it is (re)starting or stopped.</summary>
    public IIgnite? Node => Volatile.Read(ref ignite);

    /// <summary>
    ///     True once the node is running, its cluster is active and (with ReadyWhenRebalanced) it has caught up after its
    ///     start (received its partitions), until it stops.
    /// </summary>
    public bool IsReady => ready && Node is { } node &&
                           !(options.ReadyWhenRebalanced && RebalanceTracker.IsCatchingUp(() => IsInBaseline(node)));

    public void Dispose()
    {
        if (disposed) return;

        stopRequested = true;
        StopNode(true);
        disposed = true;
    }

    public void SetClusterEndpoints(ICollection<string> values)
    {
        EnsureNotRunning();
        GetOrCreateDiscoverySpi().IpFinder = new TcpDiscoveryStaticIpFinder
        {
            Endpoints = values
        };
    }

    public void SetServerPort(int value)
    {
        EnsureNotRunning();
        GetOrCreateDiscoverySpi().LocalPort = value;
    }

    public void SetOnHeapMemoryLimit(int value)
    {
        EnsureNotRunning();
        // the whole heap from the start (as the Ignite chart): with AlwaysPreTouch and guaranteed container memory, it
        // never grows at run time
        Configuration.JvmInitialMemoryMb = value;
        Configuration.JvmMaxMemoryMb = value;
    }

    public void SetOffHeapMemoryLimit(int value)
    {
        EnsureNotRunning();
        IgniteConfigurationFactory.SetOffHeapMemoryLimit(Configuration, value, options.EnableOffHeapMetrics);
    }

    public void SetPersistence(bool value)
    {
        EnsureNotRunning();
        IgniteConfigurationFactory.SetPersistence(Configuration, value);
    }

    public void SetConsistentId(string cid)
    {
        EnsureNotRunning();
        Configuration.ConsistentId = cid;
    }

    public async Task Run()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        TaskCompletionSource<string?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (stopRequested) return;

        logger.Information("Starting Ignite server runner...");
        RebalanceTracker.Reset();
        // persistence may have been enabled after the memory limits were set
        IgniteConfigurationFactory.SetPageEvictionMode(Configuration, options.PageEvictionMode);
        var current = Ignition.Start(Configuration);
        ignite = current;
        current.Stopped += (s, _) =>
        {
            ready = false;
            tcs.TrySetResult(s?.ToString());
        };
        if (stopRequested)
            // Stop() ran while starting, before this node could be stopped
            StopNode(false);

        if (!ReferenceEquals(Volatile.Read(ref ignite), current))
            // stopped before the handler was attached
            tcs.TrySetResult(current.Name);

        var cluster = current.GetCluster();
        var persistenceEnabled = Configuration.DataStorageConfiguration?.DefaultDataRegionConfiguration
            ?.PersistenceEnabled == true;
        if (persistenceEnabled) await WaitForServerNodes(cluster, tcs.Task).ConfigureAwait(false);

        if (tcs.Task.IsCompleted)
        {
            logger.Information("{IgniteInstance} stopped before activation.", tcs.Task.Result);
            return;
        }

        // a cluster that was never activated has no baseline: its users still have their default passwords
        var newCluster = persistenceEnabled && GetBaselineTopology(cluster) == null;
        if (!cluster.IsActive())
        {
            cluster.SetActive(true);
            logger.Information("The cluster was activated ({ServerNodes} server nodes).",
                cluster.ForServers().GetNodes().Count);
        }

        cluster.SetBaselineAutoAdjustEnabledFlag(true);
        cluster.SetBaselineAutoAdjustTimeout(
            (long)options.GetBaselineAutoAdjustTimeout(persistenceEnabled).TotalMilliseconds);
        // users exist only with authentication (which implies persistence); without it there is no password to set
        if (Configuration.AuthenticationEnabled && !string.IsNullOrWhiteSpace(options.IgniteUserPassword))
            try
            {
                new IgniteUserPasswordManager(options, logger).Apply(current, options.IgniteUserPassword, newCluster);
            }
            catch (Exception ex)
            {
                // the node keeps running with the current password, as without IGNITE_USER_PASSWORD
                logger.Error(ex, "IGNITE_USER_PASSWORD was not applied.");
            }

        // disposed once this run's node stopped
        using LostPartitionsListener lostPartitions = new(current, logger, options.AutoResetLostPartitions,
            persistenceEnabled);
        current.GetEvents().LocalListen<CacheRebalancingEvent>(lostPartitions, EventType.CacheRebalancePartDataLost);
        current.GetEvents().LocalListen<DiscoveryEvent>(lostPartitions, EventType.NodeJoined);
        current.GetEvents()
            .LocalListen(
                new DiscoveryEventListener(logger),
                EventType.NodeJoined,
                EventType.NodeLeft,
                EventType.NodeFailed,
                EventType.NodeSegmented
            );

        ready = !tcs.Task.IsCompleted;
        RebalanceTracker.OnRunning();
        var discoverySpiLocalPort = (current.GetConfiguration().DiscoverySpi as TcpDiscoverySpi)?.LocalPort;
        logger.Information(
            "Ignite server runner is running (DiscoverySpi LocalPort={DiscoverySpiLocalPort}), press CTRL+C to terminate.",
            discoverySpiLocalPort
        );
        var stoppedInstance = await tcs.Task.ConfigureAwait(false);
        logger.Information("'{IgniteInstance}' stopped.", stoppedInstance);
    }

    /// <summary>
    ///     Stops the node gracefully so that <see cref="Run" /> can start it again (e.g., with renewed certificates).
    ///     Returns false when the node did not stop within <paramref name="timeout" /> (e.g., a single node whose caches
    ///     have backups, which the GRACEFUL shutdown policy waits for).
    /// </summary>
    public bool Restart(TimeSpan timeout)
    {
        logger.Information("Restarting Ignite server runner...");
        ready = false;
        var current = Interlocked.Exchange(ref ignite, null);
        return current == null || Task.Run(() => Ignition.Stop(current.Name, false)).Wait(timeout);
    }

    /// <summary>
    ///     Stops the node gracefully; afterward, <see cref="Run" /> does not start it again. Returns false when the node
    ///     did not stop within <paramref name="timeout" />: the GRACEFUL shutdown policy waits until the node's
    ///     partitions have copies on other nodes, which never happens when the whole cluster stops; the node then stops
    ///     with the process.
    /// </summary>
    public bool Stop(TimeSpan timeout)
    {
        stopRequested = true;
        logger.Information("Stopping Ignite server runner...");
        var current = Interlocked.Exchange(ref ignite, null);
        if (current == null) return true;

        ready = false;
        var stopping = Task.Run(() => Ignition.Stop(current.Name, false));
        if (stopping.Wait(timeout))
        {
            logger.Information("Ignite server runner stopped.");
            return true;
        }

        logger.Error(
            "The Ignite node did not stop gracefully within {Timeout} (it waits for other nodes to take over its partitions); it stops with the process.",
            timeout
        );
        return false;
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
            if (serverNodes.Count >= options.ActivationMinServerNodes) return;

            logger.Information(
                "Waiting for {ActivationMinServerNodes} server nodes before activating the cluster ({ServerNodes} joined: {JoinedNodes})...",
                options.ActivationMinServerNodes,
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

    private bool StopNode(bool cancel)
    {
        ready = false;
        var current = Interlocked.Exchange(ref ignite, null);
        if (current == null) return false;

        Ignition.Stop(current.Name, cancel);
        return true;
    }

    private TcpDiscoverySpi GetOrCreateDiscoverySpi()
    {
        switch (Configuration.DiscoverySpi)
        {
            case TcpDiscoverySpi tcpDiscoverySpi:
                return tcpDiscoverySpi;
            case { } other:
                throw new InvalidOperationException(
                    $"The discovery port and cluster endpoints need a TcpDiscoverySpi, the configuration uses {other.GetType().Name}."
                );
        }

        TcpDiscoverySpi created = new();
        Configuration.DiscoverySpi = created;
        return created;
    }

    private void EnsureNotRunning()
    {
        if (ignite != null) throw new InvalidOperationException("Cannot configure running instances.");
    }
}