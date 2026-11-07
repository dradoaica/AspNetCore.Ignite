using AspNetCore.IgniteServer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Apache.Ignite.Core.Discovery.Tcp;
using Apache.Ignite.Core.Discovery.Tcp.Static;
using System.Linq;

namespace AspNetCore.Ignite.Tests;

[TestClass]
public class IgniteServerRunnerTests
{
    [TestMethod]
    public void Constructor_AppliesOptions()
    {
        using IgniteServerRunner runner = new(
            new IgniteServerRunnerOptions
            {
                SpringConfigPath = "/tmp/spring-config.xml",
                UseSsl = true,
                SslKeyStoreFilePath = "server.jks",
            }
        );

        Assert.AreEqual("/tmp/spring-config.xml", runner.Configuration.SpringConfigUrl);
        Assert.IsNotNull(runner.Configuration.SslContextFactory);
        Assert.IsFalse(runner.Configuration.AuthenticationEnabled);
    }

    [TestMethod]
    public void Constructor_AuthenticationEnablesPersistence()
    {
        using IgniteServerRunner runner = new(new IgniteServerRunnerOptions { AuthenticationEnabled = true, });

        Assert.IsTrue(runner.Configuration.AuthenticationEnabled);
        Assert.IsTrue(runner.Configuration.DataStorageConfiguration.DefaultDataRegionConfiguration.PersistenceEnabled);
    }

    [TestMethod]
    public void SetOffHeapMemoryLimit_ConfiguresDefaultRegion()
    {
        using IgniteServerRunner runner = new(new IgniteServerRunnerOptions { EnableOffHeapMetrics = true, });

        runner.SetOffHeapMemoryLimit(2048);
        runner.SetPersistence(true);

        var region = runner.Configuration.DataStorageConfiguration.DefaultDataRegionConfiguration;
        Assert.AreEqual(2048L * 1024 * 1024, region.MaxSize);
        Assert.IsTrue(region.MetricsEnabled);
        Assert.IsTrue(region.PersistenceEnabled);
    }

    [TestMethod]
    public void SetOnHeapMemoryLimit_SetsJvmMemory()
    {
        using IgniteServerRunner runner = new(new IgniteServerRunnerOptions());

        runner.SetOnHeapMemoryLimit(1024);

        Assert.AreEqual(512, runner.Configuration.JvmInitialMemoryMb);
        Assert.AreEqual(1024, runner.Configuration.JvmMaxMemoryMb);
    }

    [TestMethod]
    public void SetServerPortAndClusterEndpoints_ConfigureDiscovery()
    {
        using IgniteServerRunner runner = new(new IgniteServerRunnerOptions());

        runner.SetServerPort(47600);
        runner.SetClusterEndpoints(["10.0.0.1:47500", "10.0.0.2:47500",]);
        runner.SetConsistentId("node-1");

        var spi = (TcpDiscoverySpi)runner.Configuration.DiscoverySpi;
        Assert.AreEqual(47600, spi.LocalPort);
        CollectionAssert.AreEqual(
            new[] { "10.0.0.1:47500", "10.0.0.2:47500", },
            ((TcpDiscoveryStaticIpFinder)spi.IpFinder).Endpoints.ToArray()
        );
        Assert.AreEqual("node-1", runner.Configuration.ConsistentId);
    }

    [TestMethod]
    public void BuildAlterUserPasswordSql_EscapesQuotes() =>
        Assert.AreEqual(
            "ALTER USER \"ig\"\"nite\" WITH PASSWORD 'it''s; DROP';",
            IgniteServerRunner.BuildAlterUserPasswordSql("ig\"nite", "it's; DROP")
        );
}
