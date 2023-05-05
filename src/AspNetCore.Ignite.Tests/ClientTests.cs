namespace AspNetCore.Ignite.Tests;

using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class ClientTests
{
    [TestMethod]
    public void Test_CacheFactory_ConnectAsClient()
    {
        var igniteClient = CacheFactory.ConnectAsClient(CacheFactory.GetIgniteClientConfiguration());
        Assert.IsNotNull(igniteClient);
    }
}
