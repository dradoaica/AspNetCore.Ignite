using Apache.Ignite.Core.Client;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspNetCore.Ignite.Tests;

[TestClass]
public class ClientTests
{
    [TestMethod]
    public void Test_CacheFactory_ConnectAsClient()
    {
        IIgniteClient igniteClient = CacheFactory.ConnectAsClient(CacheFactory.GetIgniteClientConfiguration());
        Assert.IsNotNull(igniteClient);
    }
}
