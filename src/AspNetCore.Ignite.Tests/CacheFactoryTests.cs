using Apache.Ignite.Core;
using Apache.Ignite.Core.Client;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;

namespace AspNetCore.Ignite.Tests;

[TestClass]
public class CacheFactoryTests
{
    [TestMethod]
    public void GetIgniteClientConfiguration_Defaults()
    {
        var cfg = CacheFactory.GetIgniteClientConfiguration();

        CollectionAssert.AreEqual(new[] { CacheFactory.DefaultEndpoint, }, cfg.Endpoints.ToArray());
        Assert.IsNull(cfg.UserName);
        Assert.IsNull(cfg.Password);
        Assert.IsNull(cfg.SslStreamFactory);
        Assert.IsNull(cfg.Logger);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    public void GetIgniteClientConfiguration_MissingEndpoint_FallsBackToDefault(string? endpoint)
    {
        var cfg = CacheFactory.GetIgniteClientConfiguration(endpoint);

        CollectionAssert.AreEqual(new[] { CacheFactory.DefaultEndpoint, }, cfg.Endpoints.ToArray());
    }

    [TestMethod]
    public void GetIgniteClientConfiguration_Credentials()
    {
        var cfg = CacheFactory.GetIgniteClientConfiguration("10.0.0.1:10800", "user", "secret");

        CollectionAssert.AreEqual(new[] { "10.0.0.1:10800", }, cfg.Endpoints.ToArray());
        Assert.AreEqual("user", cfg.UserName);
        Assert.AreEqual("secret", cfg.Password);
    }

    [TestMethod]
    public void GetIgniteClientConfiguration_Ssl_ValidatesServerCertificateByDefault()
    {
        var cfg = CacheFactory.GetIgniteClientConfiguration(useSsl: true, certificatePath: "client.pfx");

        var sslStreamFactory = cfg.SslStreamFactory as SslStreamFactory;
        Assert.IsNotNull(sslStreamFactory);
        Assert.IsFalse(sslStreamFactory.SkipServerCertificateValidation);
        Assert.IsTrue(sslStreamFactory.CheckCertificateRevocation);
        Assert.AreEqual("client.pfx", sslStreamFactory.CertificatePath);
    }

    [TestMethod]
    public void GetIgniteClientConfiguration_Ssl_OptOuts()
    {
        var cfg = CacheFactory.GetIgniteClientConfiguration(
            useSsl: true,
            skipServerCertificateValidation: true,
            checkCertificateRevocation: false
        );

        var sslStreamFactory = (SslStreamFactory)cfg.SslStreamFactory;
        Assert.IsTrue(sslStreamFactory.SkipServerCertificateValidation);
        Assert.IsFalse(sslStreamFactory.CheckCertificateRevocation);
    }

    [TestMethod]
    public void IsTransient_ClassifiesExceptions()
    {
        Assert.IsTrue(IgniteManager.IsTransient(new SocketException()));
        Assert.IsTrue(IgniteManager.IsTransient(new IOException()));
        Assert.IsTrue(IgniteManager.IsTransient(new TimeoutException()));
        Assert.IsTrue(IgniteManager.IsTransient(new AggregateException(new SocketException(), new IOException())));
        Assert.IsTrue(IgniteManager.IsTransient(new IgniteClientException("x", null, ClientStatusCode.Fail)));
        Assert.IsFalse(
            IgniteManager.IsTransient(new IgniteClientException("x", null, ClientStatusCode.AuthenticationFailed))
        );
        Assert.IsFalse(IgniteManager.IsTransient(new AggregateException(new SocketException(), new ArgumentException())));
        Assert.IsFalse(IgniteManager.IsTransient(new AggregateException()));
        Assert.IsFalse(IgniteManager.IsTransient(new InvalidOperationException()));
    }

    [TestMethod]
    public void ConnectAsClient_ThickClient_DoesNotChangeGlobalClientMode()
    {
        var previous = Ignition.ClientMode;
        IgniteConfiguration cfg = new()
        {
            // an invalid JVM path makes Start fail before any JVM is created
            JvmDllPath = Path.Combine(Path.GetTempPath(), "does-not-exist", "jvm.dll"),
        };

        Assert.Throws<Exception>(() => CacheFactory.ConnectAsClient(cfg));
        Assert.IsTrue(cfg.ClientMode);
        Assert.AreEqual(previous, Ignition.ClientMode);
    }
}
