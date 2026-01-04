using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspNetCore.Ignite.Tests;

[TestClass]
public class ClientTests
{
    [TestMethod]
    public void TestCacheFactoryConnectAsClient()
    {
        var igniteClient = CacheFactory.ConnectAsClient(CacheFactory.GetIgniteClientConfiguration());
        Assert.IsNotNull(igniteClient);
    }
}
