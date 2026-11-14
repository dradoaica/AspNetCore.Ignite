using Apache.Ignite.Core.Client;
using Apache.Ignite.Core.Client.Cache;
using AspNetCore.Ignite.Abstractions;
using AspNetCore.Ignite.HealthChecks;
using AspNetCore.Ignite.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class IgniteClientOptionsTests
{
    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void ApplyConfigurationKeys_ReadsFlatKeys()
    {
        IgniteClientOptions options = new();

        options.ApplyConfigurationKeys(
            Configuration(
                new Dictionary<string, string?>
                {
                    ["ASPNETCORE_IGNITE_ENDPOINT"] = "a:10800, b:10800",
                    ["ASPNETCORE_IGNITE_USER_NAME"] = "user",
                    ["ASPNETCORE_IGNITE_PASSWORD"] = "secret",
                    ["ASPNETCORE_IGNITE_USE_SSL"] = "True",
                    ["ASPNETCORE_IGNITE_SSL_CHECK_CERTIFICATE_REVOCATION"] = "false",
                    ["ASPNETCORE_IGNITE_ENABLE_CLUSTER_DISCOVERY"] = "false",
                }
            )
        );

        Assert.False(options.EnableClusterDiscovery);
        Assert.Equal(["a:10800", "b:10800",], options.Endpoints);
        Assert.Equal("user", options.UserName);
        Assert.Equal("secret", options.Password);
        Assert.True(options.UseSsl);
        Assert.False(options.SslCheckCertificateRevocation);
        Assert.False(options.SslSkipServerCertificateValidation);
    }

    [Fact]
    public void ApplyConfigurationKeys_KeepsValuesOfMissingKeys()
    {
        IgniteClientOptions options = new()
        {
            Endpoints = ["x:1",],
            UserName = "kept",
        };

        options.ApplyConfigurationKeys(Configuration([]));

        Assert.Equal(["x:1",], options.Endpoints);
        Assert.Equal("kept", options.UserName);
        Assert.True(options.SslCheckCertificateRevocation);
    }

    [Fact]
    public void AddIgnite_ConfiguresFromFlatKeysThenDelegate_WithoutConnecting()
    {
        ServiceCollection services = new();
        services.AddSingleton(
            Configuration(
                new Dictionary<string, string?>
                {
                    ["ASPNETCORE_IGNITE_ENDPOINT"] = "a:1",
                    ["ASPNETCORE_IGNITE_USER_NAME"] = "user",
                }
            )
        );
        services.AddIgnite(options => options.UserName = "override");
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<IgniteClientOptions>>().Value;
        var igniteManager = provider.GetRequiredService<IIgniteManager>();

        Assert.Equal(["a:1",], options.Endpoints);
        Assert.Equal("override", options.UserName);
        Assert.Same(provider.GetRequiredService<IgniteManager>(), igniteManager);
    }

    [Fact]
    public void AddIgnite_BindsConfigurationSection()
    {
        ServiceCollection services = new();
        var section = Configuration(
                new Dictionary<string, string?>
                {
                    ["Ignite:Endpoints:0"] = "ignite:10800",
                    ["Ignite:Cache:Backups"] = "2",
                    ["Ignite:Cache:EnablePlatformCache"] = "true",
                }
            )
            .GetSection("Ignite");
        services.AddIgnite(section);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<IgniteClientOptions>>().Value;

        Assert.Equal(["ignite:10800",], options.Endpoints);
        Assert.Equal(2, options.Cache.Backups);
        Assert.True(options.Cache.EnablePlatformCache);
    }

    [Fact]
    public async Task HealthCheck_UnreachableCluster_ReportsFailureStatus()
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
    public async Task HealthCheck_SlowConnection_TimesOut_AndChecksShareTheAttempt()
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
    public async Task HealthCheck_HangingCluster_TimesOut()
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

    [Fact]
    public async Task IgniteManager_ConcurrentCallers_ShareOneConnectionAttempt()
    {
        // nothing listens on port 1: each attempt fails fast, and no retries are configured
        using IgniteManager igniteManager = new(
            new IgniteClientOptions
            {
                Endpoints = ["127.0.0.1:1",],
                ConnectRetryCount = 0,
                SocketTimeout = TimeSpan.FromSeconds(1),
            }
        );

        var attempts = Enumerable.Range(0, 10).Select(_ => igniteManager.GetClientAsync()).ToList();

        _ = Assert.Single(attempts.Distinct());
        await Assert.ThrowsAnyAsync<Exception>(() => attempts[0]);
        // a failed attempt is not cached: the next call starts a new one
        var nextAttempt = igniteManager.GetClientAsync();
        Assert.NotSame(attempts[0], nextAttempt);
        await Assert.ThrowsAnyAsync<Exception>(() => nextAttempt);
    }

    [Fact]
    public void IgniteManager_OperationRetries_DoNotRetryAFailedConnection()
    {
        // nothing listens on port 1; the 3 operation retries would wait 2 + 4 + 8 s if they connected again
        using IgniteManager igniteManager = new(
            new IgniteClientOptions
            {
                Endpoints = ["127.0.0.1:1",],
                ConnectRetryCount = 0,
                OperationRetryCount = 3,
                SocketTimeout = TimeSpan.FromSeconds(1),
            }
        );
        var stopwatch = Stopwatch.StartNew();

        Assert.ThrowsAny<Exception>(() => igniteManager.GetOrCreateCacheClient<string, string>("c"));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
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

        public void DestroyCache(string cacheName) => throw new NotSupportedException();
    }
}
