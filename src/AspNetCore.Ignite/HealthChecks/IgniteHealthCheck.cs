using Apache.Ignite.Core.Client;
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
    public IgniteHealthCheck(IIgniteManager igniteManager) : this(igniteManager, DefaultTimeout)
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
            IIgniteClient client;
            try
            {
                // shared with every other caller: a slow connection does not block a thread per check
                client = await igniteManager.GetClientAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The Ignite cluster did not answer within {timeout}.");
            }

            // bounded by the client's socket timeout
            var active = client.GetCluster().IsActive();
            return active ? HealthCheckResult.Healthy("The Ignite cluster is active.")
                : new HealthCheckResult(context.Registration.FailureStatus, "The Ignite cluster is not active.");
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
