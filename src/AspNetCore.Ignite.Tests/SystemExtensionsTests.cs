using AspNetCore.Ignite.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;

namespace AspNetCore.Ignite.Tests;

[TestClass]
public class SystemExtensionsTests
{
    [TestMethod]
    public void FullNameWithoutAssemblyInfo_NonGeneric() =>
        Assert.AreEqual("System.String", typeof(string).FullNameWithoutAssemblyInfo());

    [TestMethod]
    public void FullNameWithoutAssemblyInfo_Generic()
    {
        var name = typeof(List<string>).FullNameWithoutAssemblyInfo();

        Assert.DoesNotContain("Version=", name);
        Assert.StartsWith("System.Collections.Generic.List`1[[System.String", name);
    }

    [TestMethod]
    public void FullNameWithoutAssemblyInfo_OpenGeneric_FallsBackToName() =>
        // open generic parameters have no FullName
        Assert.AreEqual("T", typeof(List<>).GetGenericArguments()[0].FullNameWithoutAssemblyInfo());
}
