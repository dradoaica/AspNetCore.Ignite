using Apache.Ignite.Core;
using Apache.Ignite.Core.Client;
using AspNetCore.Ignite.Options;
using System.Security.Authentication;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class IgniteClientFactoryTests
{
    [Fact]
    public void GetIgniteClientConfiguration_Defaults()
    {
        var cfg = IgniteClientFactory.GetIgniteClientConfiguration();

        Assert.Equal([IgniteClientFactory.DefaultEndpoint,], cfg.Endpoints);
        Assert.Null(cfg.UserName);
        Assert.Null(cfg.Password);
        Assert.Null(cfg.SslStreamFactory);
        Assert.Null(cfg.Logger);
        Assert.True(cfg.EnablePartitionAwareness);
        Assert.True(cfg.EnableClusterDiscovery);
        Assert.False(
            IgniteClientFactory.GetIgniteClientConfiguration(
                    new IgniteClientOptions
                    {
                        EnableClusterDiscovery = false,
                    }
                )
                .EnableClusterDiscovery
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void GetIgniteClientConfiguration_MissingEndpoint_FallsBackToDefault(string? endpoint)
    {
        var cfg = IgniteClientFactory.GetIgniteClientConfiguration(endpoint);

        Assert.Equal([IgniteClientFactory.DefaultEndpoint,], cfg.Endpoints);
    }

    [Fact]
    public void GetIgniteClientConfiguration_Credentials()
    {
        var cfg = IgniteClientFactory.GetIgniteClientConfiguration("10.0.0.1:10800", "user", "secret");

        Assert.Equal(["10.0.0.1:10800",], cfg.Endpoints);
        Assert.Equal("user", cfg.UserName);
        Assert.Equal("secret", cfg.Password);
    }

    [Fact]
    public void GetIgniteClientConfiguration_Ssl_ValidatesServerCertificateByDefault()
    {
        var cfg = IgniteClientFactory.GetIgniteClientConfiguration(useSsl: true, certificatePath: "client.pfx");

        var sslStreamFactory = Assert.IsType<SslStreamFactory>(cfg.SslStreamFactory);
        Assert.False(sslStreamFactory.SkipServerCertificateValidation);
        Assert.True(sslStreamFactory.CheckCertificateRevocation);
        Assert.Equal("client.pfx", sslStreamFactory.CertificatePath);
        // the OS chooses the best protocol (TLS 1.3 included)
        Assert.Equal(SslProtocols.None, sslStreamFactory.SslProtocols);
    }

    [Fact]
    public void GetIgniteClientConfiguration_Ssl_OptOuts()
    {
        var cfg = IgniteClientFactory.GetIgniteClientConfiguration(
            useSsl: true,
            skipServerCertificateValidation: true,
            checkCertificateRevocation: false
        );

        var sslStreamFactory = Assert.IsType<SslStreamFactory>(cfg.SslStreamFactory);
        Assert.True(sslStreamFactory.SkipServerCertificateValidation);
        Assert.False(sslStreamFactory.CheckCertificateRevocation);
    }

    [Fact]
    public void GetIgniteClientConfiguration_FromOptions()
    {
        var cfg = IgniteClientFactory.GetIgniteClientConfiguration(
            new IgniteClientOptions
            {
                Endpoints = ["a:10800", " ", "b:10800",],
                RetryLimit = 2,
                SocketTimeout = TimeSpan.FromSeconds(3),
            }
        );

        Assert.Equal(["a:10800", "b:10800",], cfg.Endpoints);
        Assert.Equal(2, cfg.RetryLimit);
        Assert.Equal(TimeSpan.FromSeconds(3), cfg.SocketTimeout);
    }

    [Fact]
    public void ConnectAsClient_ThickClient_DoesNotChangeGlobalClientMode()
    {
        var previous = Ignition.ClientMode;
        IgniteConfiguration cfg = new()
        {
            // an invalid JVM path makes Start fail before any JVM is created
            JvmDllPath = Path.Combine(Path.GetTempPath(), "does-not-exist", "jvm.dll"),
        };

        Assert.ThrowsAny<Exception>(() => IgniteClientFactory.ConnectAsClient(cfg));
        Assert.True(cfg.ClientMode);
        Assert.Equal(previous, Ignition.ClientMode);
    }

    [Fact]
    public void CacheFactory_ForwardsItsObsoleteClientMembers()
    {
        // kept for the code written against 2.18.x, until the next major version
#pragma warning disable CS0618
        Assert.Equal(IgniteClientFactory.DefaultEndpoint, CacheFactory.DefaultEndpoint);
        Assert.Equal(["a:10800",], CacheFactory.GetIgniteClientConfiguration("a:10800").Endpoints);
        Assert.Equal(
            ["b:10800",],
            CacheFactory.GetIgniteClientConfiguration(
                    new IgniteClientOptions
                    {
                        Endpoints = ["b:10800",],
                    }
                )
                .Endpoints
        );
#pragma warning restore CS0618
    }
}
