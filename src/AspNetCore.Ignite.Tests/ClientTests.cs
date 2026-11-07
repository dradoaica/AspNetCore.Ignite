using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspNetCore.Ignite.Tests;

[TestClass]
public class ClientTests
{
    /// <summary>Requires a running Ignite server on 127.0.0.1:10800 (excluded in CI).</summary>
    [TestMethod]
    [TestCategory("Integration")]
    public void TestCacheFactoryConnectAsClient()
    {
        using var igniteClient = CacheFactory.ConnectAsClient(CacheFactory.GetIgniteClientConfiguration());
        Assert.IsNotNull(igniteClient);
    }
}
