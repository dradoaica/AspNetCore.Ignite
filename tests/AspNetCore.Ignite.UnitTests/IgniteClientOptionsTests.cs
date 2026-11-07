using Apache.Ignite.Core.Client;
using AspNetCore.Ignite.Abstractions;
using AspNetCore.Ignite.HealthChecks;
using AspNetCore.Ignite.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
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
                }
            )
        );

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
        IgniteClientOptions options = new() { Endpoints = ["x:1",], UserName = "kept", };

        options.ApplyConfigurationKeys(Configuration([]));

        Assert.Equal(["x:1",], options.Endpoints);
        Assert.Equal("kept", options.UserName);
        Assert.True(options.SslCheckCertificateRevocation);
    }

    [Fact]
    public void AddIgnite_ConfiguresFromFlatKeysThenDelegate_WithoutConnecting()
    {
        ServiceCollection services = new();
        services.AddSingleton(Configuration(new Dictionary<string, string?> { ["ASPNETCORE_IGNITE_ENDPOINT"] = "a:1", ["ASPNETCORE_IGNITE_USER_NAME"] = "user", }));
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
        IgniteHealthCheck healthCheck = new(new ThrowingIgniteManager(), TimeSpan.FromSeconds(5));
        HealthCheckContext context = new()
        {
            Registration = new HealthCheckRegistration("ignite", healthCheck, HealthStatus.Degraded, null),
        };

        var result = await healthCheck.CheckHealthAsync(context);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.IsType<IgniteClientException>(result.Exception);
    }

    [Fact]
    public async Task HealthCheck_SlowCluster_TimesOut()
    {
        IgniteHealthCheck healthCheck = new(new ThrowingIgniteManager(TimeSpan.FromSeconds(5)), TimeSpan.FromMilliseconds(50));
        HealthCheckContext context = new()
        {
            Registration = new HealthCheckRegistration("ignite", healthCheck, HealthStatus.Unhealthy, null),
        };

        var result = await healthCheck.CheckHealthAsync(context);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.IsType<TimeoutException>(result.Exception);
    }

    private sealed class ThrowingIgniteManager(TimeSpan? delay = null) : IIgniteManager
    {
        public IIgniteClient Client
        {
            get
            {
                if (delay.HasValue)
                {
                    Thread.Sleep(delay.Value);
                }

                throw new IgniteClientException("unreachable");
            }
        }

        public Apache.Ignite.Core.Client.Cache.ICacheClient<TKey, TData> GetOrCreateCacheClient<TKey, TData>(
            string cacheName,
            Action<Apache.Ignite.Core.Client.Cache.CacheClientConfiguration>? extendConfigurationAction = null
        ) => throw new NotSupportedException();

        public void DestroyCache(string cacheName) => throw new NotSupportedException();
    }
}
