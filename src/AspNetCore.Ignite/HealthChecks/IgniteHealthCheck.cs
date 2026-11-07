using AspNetCore.Ignite.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCore.Ignite.HealthChecks;

/// <summary>Healthy when the cluster is reachable and active.</summary>
public sealed class IgniteHealthCheck(IIgniteManager igniteManager, TimeSpan timeout) : IHealthCheck
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public IgniteHealthCheck(IIgniteManager igniteManager) : this(igniteManager, DefaultTimeout)
    {
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            // the thin client API is synchronous (and the first call connects), so bound it with a timeout
            var check = Task.Run(() => igniteManager.Client.GetCluster().IsActive(), cancellationToken);
            using CancellationTokenSource delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (await Task.WhenAny(check, Task.Delay(timeout, delayCts.Token)).ConfigureAwait(false) != check)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException($"The Ignite cluster did not answer within {timeout}.");
            }

            delayCts.Cancel();
            var active = await check.ConfigureAwait(false);
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
