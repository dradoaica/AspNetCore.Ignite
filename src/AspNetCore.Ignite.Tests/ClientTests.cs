namespace AspNetCore.Ignite.Tests;

using Microsoft.VisualStudio.TestTools.UnitTesting;

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
