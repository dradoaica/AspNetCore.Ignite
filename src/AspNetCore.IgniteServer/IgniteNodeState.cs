using System.Collections.Generic;
using Apache.Ignite.Core;

namespace AspNetCore.IgniteServer;

/// <summary>The state of the node run by <see cref="IgniteServerHostedService" />, for health checks and metrics.</summary>
internal sealed class IgniteNodeState
{
    private volatile IgniteServerRunner? runner;

    /// <summary>The running node, or null while it is starting, restarting or stopped.</summary>
    public IIgnite? Node => runner?.Node;

    /// <summary>True while the node is running (joined the topology), also before its cluster is activated.</summary>
    public bool IsStarted => runner?.Node != null;

    /// <summary>True once the node is running and its cluster is active.</summary>
    public bool IsReady => runner?.IsReady == true;

    /// <summary>The caches the node is rebalancing (receiving partitions of).</summary>
    public IReadOnlyCollection<string> RebalancingCaches => runner?.RebalanceTracker.RebalancingCaches ?? [];

    internal void SetRunner(IgniteServerRunner? value)
    {
        runner = value;
    }
}