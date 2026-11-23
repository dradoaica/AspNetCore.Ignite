using AspNetCore.Ignite.Abstractions;
using AspNetCore.Ignite.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    public void AddIgnite_SectionEndpoints_ReplaceTheFlatKey()
    {
        var configuration = Configuration(
            new Dictionary<string, string?>
            {
                ["ASPNETCORE_IGNITE_ENDPOINT"] = "env:10800",
                ["Ignite:Endpoints:0"] = "section:10800",
            }
        );
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddIgnite(configuration.GetSection("Ignite"));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<IgniteClientOptions>>().Value;

        Assert.Equal(["section:10800",], options.Endpoints);
    }

    [Fact]
    public void AddIgnite_SectionWithoutEndpoints_KeepsTheFlatKey()
    {
        var configuration = Configuration(
            new Dictionary<string, string?>
            {
                ["ASPNETCORE_IGNITE_ENDPOINT"] = "env:10800",
                ["Ignite:UserName"] = "user",
            }
        );
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddIgnite(configuration.GetSection("Ignite"));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<IgniteClientOptions>>().Value;

        Assert.Equal(["env:10800",], options.Endpoints);
        Assert.Equal("user", options.UserName);
    }
}
