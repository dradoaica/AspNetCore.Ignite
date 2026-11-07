using Apache.Ignite.Core;

namespace AspNetCore.IgniteServer;

/// <summary>The state of the node run by <see cref="IgniteServerHostedService" />, for health checks and metrics.</summary>
internal sealed class IgniteNodeState
{
    private volatile IgniteServerRunner? runner;

    /// <summary>The running node, or null while it is starting, restarting or stopped.</summary>
    public IIgnite? Node => runner?.Node;

    /// <summary>True once the node is running and its cluster is active.</summary>
    public bool IsReady => runner?.IsReady == true;

    internal void SetRunner(IgniteServerRunner? value) => runner = value;
}
