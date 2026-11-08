using Apache.Ignite.Core.Cache.Query;
using Apache.Ignite.Core.Client;
using AspNetCore.Ignite.Abstractions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Volumes;
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
                   IgniteServerFixture.ClientConfiguration(
                       fixture.Endpoint,
                       IgniteServerFixture.UserName,
                       IgniteServerFixture.Password
                   )
               ))
        {
            Assert.NotNull(igniteClient.GetCacheNames());
        }

        var ex = Assert.Throws<IgniteClientException>(() => CacheFactory.ConnectAsClient(
                IgniteServerFixture.ClientConfiguration(
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
                    ["ASPNETCORE_IGNITE_ENABLE_CLUSTER_DISCOVERY"] = "false"
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
                options.Endpoints = [fixture.Endpoint];
                options.UserName = IgniteServerFixture.UserName;
                options.Password = IgniteServerFixture.Password;
                options.EnableClusterDiscovery = false;
            }
        );
        services.AddHealthChecks().AddIgnite();
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.True(provider.GetRequiredService<IIgniteManager>().Client.GetCluster().IsActive());
    }

    [SkippableFact]
    public void NodeAttributes_ShowNoSecrets()
    {
        Skip.IfNot(fixture.IsDockerAvailable, DockerUnavailable);
        using var igniteClient = Connect(fixture.Endpoint, IgniteServerFixture.Password);

        var attributes = igniteClient.GetCluster().ForServers().GetNodes().Single().Attributes;

        // environment variables (IGNITE_USER_PASSWORD, SSL_*_PASSWORD) and system properties are not published
        Assert.DoesNotContain(attributes.Keys, name => name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(attributes.Values,
            value => value is string text && text.Contains(IgniteServerFixture.Password, StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task PasswordRotation_ReplacesThePreviousPassword()
    {
        Skip.IfNot(fixture.IsDockerAvailable, DockerUnavailable);
        await using var volume = new VolumeBuilder().WithName($"ignite-{Guid.NewGuid():N}").Build();
        await volume.CreateAsync();
        await using (var node = PersistentNode(volume, "first-password", null))
        {
            await node.StartAsync();
        }

        // restarted on the same data, with the new password and the one to replace
        await using var rotated = PersistentNode(volume, "second-password", "first-password");
        await rotated.StartAsync();

        using (Connect(IgniteServerFixture.GetEndpoint(rotated), "second-password"))
        {
        }

        var ex = Assert.Throws<IgniteClientException>(() =>
            Connect(IgniteServerFixture.GetEndpoint(rotated), "first-password"));
        Assert.Equal(ClientStatusCode.AuthenticationFailed, ex.StatusCode);
    }

    [SkippableFact]
    public async Task SqlQueryHistorySize_Zero_KeepsNoHistory()
    {
        Skip.IfNot(fixture.IsDockerAvailable, DockerUnavailable);
        await using var node = fixture.CreateNodeBuilder()
            .WithEnvironment("SQL_QUERY_HISTORY_SIZE", "0")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(IgniteServerFixture.RunningMessage))
            .Build();
        await node.StartAsync();
        using var igniteClient = Connect(IgniteServerFixture.GetEndpoint(node), IgniteServerFixture.Password);
        var cache = igniteClient.GetOrCreateCache<int, int>("history");

        // neither the ALTER USER of the password change (with the password) nor this query are kept
        cache.Query(new SqlFieldsQuery("SELECT 1") { Schema = "PUBLIC" }).GetAll();
        var history = cache.Query(new SqlFieldsQuery("SELECT SQL FROM SYS.SQL_QUERIES_HISTORY") { Schema = "PUBLIC" })
            .GetAll();

        Assert.Empty(history);
    }

    private static IIgniteClient Connect(string endpoint, string password)
    {
        return CacheFactory.ConnectAsClient(
            IgniteServerFixture.ClientConfiguration(endpoint, IgniteServerFixture.UserName, password)
        );
    }

    private IContainer PersistentNode(IVolume volume, string password, string? previousPassword)
    {
        var builder = fixture.CreateNodeBuilder()
            .WithVolumeMount(volume, "/app/work")
            // the same data directory for both starts
            .WithEnvironment("DEFAULT_CONSISTENT_ID", "rotation")
            .WithEnvironment("IGNITE_USER_PASSWORD", password)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(IgniteServerFixture.RunningMessage));
        return previousPassword == null
            ? builder.Build()
            : builder.WithEnvironment("IGNITE_USER_PREVIOUS_PASSWORD", previousPassword).Build();
    }
}
