using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCore.IgniteServer.Management;

/// <summary>Ready when the node is running and its cluster is active (not while starting, restarting or stopping).</summary>
internal sealed class IgniteNodeHealthCheck(IgniteNodeState nodeState) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(
            nodeState.IsReady ? HealthCheckResult.Healthy("The Ignite node is running and its cluster is active.")
                : new HealthCheckResult(context.Registration.FailureStatus, "The Ignite node is not ready (starting, restarting or stopping).")
        );
}

/// <summary>
///     Healthy once the node runs (joined the topology), even before its cluster is activated: for startup probes, so that
///     nodes waiting for ACTIVATION_MIN_SERVER_NODES are not restarted.
/// </summary>
internal sealed class IgniteNodeStartedHealthCheck(IgniteNodeState nodeState) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(
            nodeState.IsStarted ? HealthCheckResult.Healthy("The Ignite node is running.")
                : new HealthCheckResult(context.Registration.FailureStatus, "The Ignite node is not running (starting, restarting or stopping).")
        );
}
