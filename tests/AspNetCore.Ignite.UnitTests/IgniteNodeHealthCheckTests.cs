using AspNetCore.IgniteServer;
using AspNetCore.IgniteServer.Management;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class IgniteNodeHealthCheckTests
{
    [Fact]
    public async Task StartedCheck_IsUnhealthyWithoutNode()
    {
        IgniteNodeStartedHealthCheck check = new(new IgniteNodeState());
        var registration = new HealthCheckRegistration("started", check, null, null);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext
            {
                Registration = registration,
            }
        );

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
