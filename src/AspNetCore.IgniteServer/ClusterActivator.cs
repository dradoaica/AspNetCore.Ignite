using Apache.Ignite.Core;
using Apache.Ignite.Core.Cluster;
using Apache.Ignite.Core.Common;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCore.IgniteServer;

/// <summary>
///     Changes the state of the cluster of a started node, for <see cref="IgniteServerRunner" />: activates it
///     (<see cref="ActivateAsync" />), and deactivates it before the node stops when the node is its last server node
///     (<see cref="DeactivateIfLastServerNode" />). It also reads the baseline topology.
///     <para>
///         The activation and the deactivation take turns (<c>stateLock</c>), and no activation begins once the node is
///         being stopped. So either the stop comes first and the cluster is not activated, or the stop waits for the
///         activation and then deactivates the cluster. An activation never undoes the deactivation of a stopping node.
///     </para>
///     <para>
///         It keeps nothing between runs: what the runner remembers (e.g., that the node deactivated the cluster to
///         restart) comes in as arguments.
///     </para>
/// </summary>
/// <param name="settings">The server settings.</param>
/// <param name="logger">Where the activation logs.</param>
/// <param name="persistent">Whether the default data region of the nodes is persistent.</param>
/// <param name="authenticationEnabled">Whether the nodes have authentication (and so users with passwords).</param>
internal sealed class ClusterActivator(
    IgniteServerSettings settings,
    ILogger logger,
    bool persistent,
    bool authenticationEnabled
)
{
    private static readonly TimeSpan ActivationPollInterval = TimeSpan.FromSeconds(5);

    private readonly ILogger logger = logger.ForContext<ClusterActivator>();

    // Held while this node activates or deactivates its cluster, so Ignite is called under it. Ignite's own threads
    // never take it.
    private readonly Lock stateLock = new();

    /// <summary>
    ///     Activates the cluster (a persistent one once enough server nodes have joined), lets its baseline follow the
    ///     topology and applies IGNITE_USER_PASSWORD. Returns false when the node began to stop before its cluster was
    ///     activated.
    /// </summary>
    /// <param name="node">The node that has started.</param>
    /// <param name="reactivate">
    ///     The node was the cluster's only server node before its restart, and deactivated the cluster to
    ///     restart: it does not wait for ACTIVATION_MIN_SERVER_NODES, as a cluster that starts for the first time does.
    /// </param>
    /// <param name="stopping">Completes when the node begins to stop or restart, or stops on its own.</param>
    public async Task<bool> ActivateAsync(IIgnite node, bool reactivate, Task stopping)
    {
        var cluster = node.GetCluster();
        if (persistent && !reactivate)
        {
            await WaitForServerNodes(cluster, stopping).ConfigureAwait(false);
        }

        bool newCluster;
        lock (stateLock)
        {
            // Checked under the lock: a stop that began before has the cluster left as it is, and one that begins now
            // waits for this activation (DeactivateIfLastServerNode).
            if (stopping.IsCompleted)
            {
                logger.Information("The cluster is not activated: the node is being stopped.");
                return false;
            }

            // A persistent cluster without a baseline was never activated, so its users still have their default
            // passwords.
            newCluster = persistent && GetBaselineTopology(cluster) == null;
            if (!cluster.IsActive())
            {
                cluster.SetActive(true);
                logger.Information(
                    "The cluster was activated{Again:l} ({ServerNodes} server nodes).",
                    reactivate ? " again after the restart of its last server node" : "",
                    cluster.ForServers().GetNodes().Count
                );
            }
        }

        cluster.SetBaselineAutoAdjustEnabledFlag(true);
        cluster.SetBaselineAutoAdjustTimeout((long)settings.GetBaselineAutoAdjustTimeout(persistent).TotalMilliseconds);
        // Users exist only with authentication. Without it, there is no password to set. A node that is being stopped
        // leaves the password to its next start.
        if (authenticationEnabled && !string.IsNullOrWhiteSpace(settings.IgniteUserPassword) && !stopping.IsCompleted)
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
    ///     Deactivates the cluster before the node stops, when the node is the cluster's last server node: no other node
    ///     can take over its partitions, which the GRACEFUL stop would wait for in vain. An inactive cluster lets the node
    ///     stop at once (a persistent one checkpoints its data), and the next start activates it again. An activation in
    ///     progress is waited for, within <paramref name="timeout" /> like the deactivation. Returns true when it
    ///     deactivated the cluster.
    /// </summary>
    public bool DeactivateIfLastServerNode(IIgnite node, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        if (!stateLock.TryEnter())
        {
            logger.Information("The node stops while its cluster is activated: waiting for the activation to end.");
            if (!stateLock.TryEnter(timeout))
            {
                logger.Warning("The cluster was not deactivated: its activation did not end within {Timeout}.", timeout);
                return false;
            }
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
            var remaining = timeout > elapsed.Elapsed ? timeout - elapsed.Elapsed : TimeSpan.Zero;
            if (Task.Run(() => cluster.SetActive(false)).Wait(remaining))
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
        finally
        {
            stateLock.Exit();
        }

        return false;
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

    /// <summary>
    ///     A persistent cluster records its baseline topology on activation, so wait for the configured number of server
    ///     nodes before activating it (unless it is already active, e.g., when resuming).
    /// </summary>
    private async Task WaitForServerNodes(ICluster cluster, Task stopping)
    {
        while (!stopping.IsCompleted && !cluster.IsActive())
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
            await Task.WhenAny(stopping, Task.Delay(ActivationPollInterval)).ConfigureAwait(false);
        }
    }
}
