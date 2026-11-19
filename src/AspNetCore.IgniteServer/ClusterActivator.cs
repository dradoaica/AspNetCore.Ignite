using Apache.Ignite.Core;
using Apache.Ignite.Core.Cluster;
using Apache.Ignite.Core.Common;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AspNetCore.IgniteServer;

/// <summary>
///     Activates the cluster of a node that has started (for <see cref="IgniteServerRunner" />), and reads its
///     baseline topology. It holds no state of its own: what the runner remembers between runs (e.g., that the node
///     deactivated the cluster to restart) comes in as arguments.
/// </summary>
internal sealed class ClusterActivator(IgniteServerSettings settings, ILogger logger)
{
    private static readonly TimeSpan ActivationPollInterval = TimeSpan.FromSeconds(5);

    private readonly ILogger logger = logger.ForContext<ClusterActivator>();

    /// <summary>
    ///     Activates the cluster (a persistent one once enough server nodes have joined), lets its baseline follow the
    ///     topology and applies IGNITE_USER_PASSWORD. Returns false when the node stopped before its cluster was activated.
    /// </summary>
    /// <param name="node">The node that has started.</param>
    /// <param name="persistent">Whether its default data region is persistent.</param>
    /// <param name="authenticationEnabled">Whether the node has authentication (and so users with passwords).</param>
    /// <param name="reactivate">
    ///     The node was the cluster's only server node before its restart, and deactivated the cluster to
    ///     restart: it does not wait for ACTIVATION_MIN_SERVER_NODES, as a cluster that starts for the first time does.
    /// </param>
    /// <param name="stopped">Completes when the node stops.</param>
    public async Task<bool> ActivateAsync(
        IIgnite node,
        bool persistent,
        bool authenticationEnabled,
        bool reactivate,
        Task stopped
    )
    {
        var cluster = node.GetCluster();
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
        if (authenticationEnabled && !string.IsNullOrWhiteSpace(settings.IgniteUserPassword))
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
}
