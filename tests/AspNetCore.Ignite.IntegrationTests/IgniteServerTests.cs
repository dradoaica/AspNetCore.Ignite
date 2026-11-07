using Apache.Ignite.Core.Client;
using AspNetCore.Ignite.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace AspNetCore.Ignite.IntegrationTests;

[Collection(IgniteServerCollection.Name)]
public sealed class IgniteServerTests(IgniteServerFixture fixture)
{
    private const string DockerUnavailable = "Docker is not available.";

    [SkippableFact]
    public void DefaultUserPassword_IsChangedOnStartup()
    {
        Skip.IfNot(fixture.IsDockerAvailable, DockerUnavailable);

        using (var igniteClient = CacheFactory.ConnectAsClient(
                   CacheFactory.GetIgniteClientConfiguration(
                       fixture.Endpoint,
                       IgniteServerFixture.UserName,
                       IgniteServerFixture.Password
                   )
               ))
        {
            Assert.NotNull(igniteClient.GetCacheNames());
        }

        var ex = Assert.Throws<IgniteClientException>(() => CacheFactory.ConnectAsClient(
                CacheFactory.GetIgniteClientConfiguration(
                    fixture.Endpoint,
                    IgniteServerFixture.UserName,
                    IgniteServerFixture.DefaultPassword
                )
            )
        );
        Assert.Equal(ClientStatusCode.AuthenticationFailed, ex.StatusCode);
    }

    [SkippableFact]
    public void IgniteManager_CreatesUsesAndDestroysCaches()
    {
        Skip.IfNot(fixture.IsDockerAvailable, DockerUnavailable);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ASPNETCORE_IGNITE_ENDPOINT"] = fixture.Endpoint,
                    ["ASPNETCORE_IGNITE_USER_NAME"] = IgniteServerFixture.UserName,
                    ["ASPNETCORE_IGNITE_PASSWORD"] = IgniteServerFixture.Password,
                }
            )
            .Build();
        var cacheName = $"cache-{Guid.NewGuid():N}";
        using IgniteManager igniteManager = new(configuration);

        var cache = igniteManager.GetOrCreateCacheClient<int, string>(cacheName);
        cache.Put(1, "one");

        Assert.Equal("one", cache.Get(1));
        igniteManager.DestroyCache(cacheName);
        // destroying a cache that does not exist is a no-op
        igniteManager.DestroyCache(cacheName);
    }

    [SkippableFact]
    public async Task HealthCheck_ReportsHealthyCluster()
    {
        Skip.IfNot(fixture.IsDockerAvailable, DockerUnavailable);
        ServiceCollection services = new();
        // with logging, the Ignite client logs through Microsoft.Extensions.Logging (IgniteLoggerAdapter)
        services.AddLogging();
        services.AddIgnite(options =>
            {
                options.Endpoints = [fixture.Endpoint,];
                options.UserName = IgniteServerFixture.UserName;
                options.Password = IgniteServerFixture.Password;
            }
        );
        services.AddHealthChecks().AddIgnite();
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.True(provider.GetRequiredService<IIgniteManager>().Client.GetCluster().IsActive());
    }
}
