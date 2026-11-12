using AspNetCore.Ignite.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCore.Ignite.HealthChecks;

/// <summary>Healthy when the cluster is reachable and active.</summary>
public sealed class IgniteHealthCheck(IIgniteManager igniteManager, TimeSpan timeout) : IHealthCheck
{
    /// <summary>The time a check may take by default.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A health check with <see cref="DefaultTimeout" />.</summary>
    /// <param name="igniteManager">The connection to check.</param>
    public IgniteHealthCheck(IIgniteManager igniteManager)
        : this(igniteManager, DefaultTimeout)
    {
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            bool active;
            try
            {
                // The connection attempt is shared, so a slow connection does not block one thread per check.
                var client = await igniteManager.GetClientAsync(timeoutCts.Token).ConfigureAwait(false);
                // The client call is synchronous and bounded only by the socket timeout, so the check stops
                // waiting for it at its own timeout.
                active = await Task.Run(() => client.GetCluster().IsActive(), CancellationToken.None)
                    .WaitAsync(timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The Ignite cluster did not answer within {timeout}.");
            }

            return active ? HealthCheckResult.Healthy("The Ignite cluster is active.") : new HealthCheckResult(
                context.Registration.FailureStatus,
                "The Ignite cluster is not active."
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                "The Ignite cluster is not reachable.",
                ex
            );
        }
    }
}
