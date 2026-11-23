using Apache.Ignite.Core.Client;
using Apache.Ignite.Core.Client.Cache;
using AspNetCore.Ignite.Abstractions;
using AspNetCore.Ignite.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class IgniteHealthCheckTests
{
    [Fact]
    public async Task UnreachableCluster_ReportsFailureStatus()
    {
        IgniteHealthCheck healthCheck = new(
            new FakeIgniteManager(Task.FromException<IIgniteClient>(new IgniteClientException("unreachable"))),
            TimeSpan.FromSeconds(5)
        );

        var result = await healthCheck.CheckHealthAsync(Context(healthCheck, HealthStatus.Degraded));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.IsType<IgniteClientException>(result.Exception);
    }

    [Fact]
    public async Task SlowConnection_TimesOut_AndChecksShareTheAttempt()
    {
        TaskCompletionSource<IIgniteClient> connecting = new();
        FakeIgniteManager igniteManager = new(connecting.Task);
        IgniteHealthCheck healthCheck = new(igniteManager, TimeSpan.FromMilliseconds(100));

        var results = await Task.WhenAll(
            Enumerable.Range(0, 20)
                .Select(_ => healthCheck.CheckHealthAsync(Context(healthCheck, HealthStatus.Unhealthy)))
        );

        Assert.All(results, result => Assert.IsType<TimeoutException>(result.Exception));
        // every check waited for the same (still running) connection attempt, none blocked a thread
        Assert.Equal(20, igniteManager.GetClientCalls);
        Assert.False(connecting.Task.IsCompleted);
    }

    [Fact]
    public async Task HangingCluster_TimesOut()
    {
        using ManualResetEventSlim release = new();
        var cluster = Fake.Of<IClientCluster>((_, _) =>
            {
                // IsActive blocks, as when the cluster does not answer
                release.Wait();
                return true;
            }
        );
        var client = Fake.Of<IIgniteClient>((name, _) =>
            name == nameof(IIgniteClient.GetCluster) ? cluster : throw new NotSupportedException(name)
        );
        IgniteHealthCheck healthCheck = new(
            new FakeIgniteManager(Task.FromResult(client)),
            TimeSpan.FromMilliseconds(100)
        );
        try
        {
            // connected, but the cluster does not answer: the check ends with its timeout, not the socket timeout
            var result = await healthCheck.CheckHealthAsync(Context(healthCheck, HealthStatus.Unhealthy))
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.IsType<TimeoutException>(result.Exception);
        }
        finally
        {
            release.Set();
        }
    }

    private static HealthCheckContext Context(IHealthCheck healthCheck, HealthStatus failureStatus) => new()
    {
        Registration = new HealthCheckRegistration("ignite", healthCheck, failureStatus, null),
    };

    private sealed class FakeIgniteManager(Task<IIgniteClient> connection) : IIgniteManager
    {
        private int getClientCalls;

        public int GetClientCalls => getClientCalls;

        public IIgniteClient Client => connection.GetAwaiter().GetResult();

        public async Task<IIgniteClient> GetClientAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref getClientCalls);
            return await connection.WaitAsync(cancellationToken);
        }

        public ICacheClient<TKey, TData> GetOrCreateCacheClient<TKey, TData>(
            string cacheName,
            Action<CacheClientConfiguration>? extendConfigurationAction = null
        ) => throw new NotSupportedException();

        public Task<ICacheClient<TKey, TData>> GetOrCreateCacheClientAsync<TKey, TData>(
            string cacheName,
            Action<CacheClientConfiguration>? extendConfigurationAction = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public void DestroyCache(string cacheName) => throw new NotSupportedException();
    }
}
