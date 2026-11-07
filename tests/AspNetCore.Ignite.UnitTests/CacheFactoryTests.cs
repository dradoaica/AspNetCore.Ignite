using System.Net.Sockets;
using System.Security.Authentication;
using Apache.Ignite.Core;
using Apache.Ignite.Core.Client;
using AspNetCore.Ignite.Options;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class CacheFactoryTests
{
    public static TheoryData<Exception, bool> TransientCases => new()
    {
        { new SocketException(), true },
        { new IOException(), true },
        { new TimeoutException(), true },
        { new AggregateException(new SocketException(), new IOException()), true },
        { new IgniteClientException("x", null, ClientStatusCode.Fail), true },
        { new IgniteClientException("x", null, ClientStatusCode.AuthenticationFailed), false },
        { new IgniteClientException("x", null, ClientStatusCode.SecurityViolation), false },
        { new AggregateException(new SocketException(), new ArgumentException()), false },
        { new AggregateException(), false },
        { new InvalidOperationException(), false }
    };

    [Fact]
    public void GetIgniteClientConfiguration_Defaults()
    {
        var cfg = CacheFactory.GetIgniteClientConfiguration();

        Assert.Equal([CacheFactory.DefaultEndpoint], cfg.Endpoints);
        Assert.Null(cfg.UserName);
        Assert.Null(cfg.Password);
        Assert.Null(cfg.SslStreamFactory);
        Assert.Null(cfg.Logger);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void GetIgniteClientConfiguration_MissingEndpoint_FallsBackToDefault(string? endpoint)
    {
        var cfg = CacheFactory.GetIgniteClientConfiguration(endpoint);

        Assert.Equal([CacheFactory.DefaultEndpoint], cfg.Endpoints);
    }

    [Fact]
    public void GetIgniteClientConfiguration_Credentials()
    {
        var cfg = CacheFactory.GetIgniteClientConfiguration("10.0.0.1:10800", "user", "secret");

        Assert.Equal(["10.0.0.1:10800"], cfg.Endpoints);
        Assert.Equal("user", cfg.UserName);
        Assert.Equal("secret", cfg.Password);
    }

    [Fact]
    public void GetIgniteClientConfiguration_Ssl_ValidatesServerCertificateByDefault()
    {
        var cfg = CacheFactory.GetIgniteClientConfiguration(useSsl: true, certificatePath: "client.pfx");

        var sslStreamFactory = Assert.IsType<SslStreamFactory>(cfg.SslStreamFactory);
        Assert.False(sslStreamFactory.SkipServerCertificateValidation);
        Assert.True(sslStreamFactory.CheckCertificateRevocation);
        Assert.Equal("client.pfx", sslStreamFactory.CertificatePath);
        // the OS chooses the best protocol (TLS 1.3 included)
        Assert.Equal(SslProtocols.None, sslStreamFactory.SslProtocols);
    }

    [Fact]
    public void GetIgniteClientConfiguration_FromOptions()
    {
        var cfg = CacheFactory.GetIgniteClientConfiguration(
            new IgniteClientOptions
            {
                Endpoints = ["a:10800", " ", "b:10800"],
                RetryLimit = 2,
                SocketTimeout = TimeSpan.FromSeconds(3)
            }
        );

        Assert.Equal(["a:10800", "b:10800"], cfg.Endpoints);
        Assert.Equal(2, cfg.RetryLimit);
        Assert.Equal(TimeSpan.FromSeconds(3), cfg.SocketTimeout);
    }

    [Fact]
    public void CacheDefaults_PlatformCacheIsOptIn()
    {
        Assert.Null(CacheFactory.GetPlatformCacheConfiguration<int, string>(new IgniteCacheOptions()));

        var platformCache = CacheFactory.GetPlatformCacheConfiguration<int, string>(
            new IgniteCacheOptions { EnablePlatformCache = true }
        );

        Assert.NotNull(platformCache);
        Assert.Equal("System.String", platformCache.ValueTypeName);
    }

    [Fact]
    public void CacheDefaults_GroupByValueType()
    {
        Assert.Equal("System.String", CacheFactory.GetGroupName<string>(new IgniteCacheOptions()));
        Assert.Null(CacheFactory.GetGroupName<string>(new IgniteCacheOptions { GroupByValueType = false }));
    }

    [Fact]
    public void GetIgniteClientConfiguration_Ssl_OptOuts()
    {
        var cfg = CacheFactory.GetIgniteClientConfiguration(
            useSsl: true,
            skipServerCertificateValidation: true,
            checkCertificateRevocation: false
        );

        var sslStreamFactory = Assert.IsType<SslStreamFactory>(cfg.SslStreamFactory);
        Assert.True(sslStreamFactory.SkipServerCertificateValidation);
        Assert.False(sslStreamFactory.CheckCertificateRevocation);
    }

    [Theory]
    [MemberData(nameof(TransientCases))]
    public void IsTransient_ClassifiesExceptions(Exception exception, bool expected)
    {
        Assert.Equal(expected, IgniteManager.IsTransient(exception));
    }

    [Fact]
    public void ConnectAsClient_ThickClient_DoesNotChangeGlobalClientMode()
    {
        var previous = Ignition.ClientMode;
        IgniteConfiguration cfg = new()
        {
            // an invalid JVM path makes Start fail before any JVM is created
            JvmDllPath = Path.Combine(Path.GetTempPath(), "does-not-exist", "jvm.dll")
        };

        Assert.ThrowsAny<Exception>(() => CacheFactory.ConnectAsClient(cfg));
        Assert.True(cfg.ClientMode);
        Assert.Equal(previous, Ignition.ClientMode);
    }
}