using AspNetCore.Ignite.Utils;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class SystemExtensionsTests
{
    [Fact]
    public void FullNameWithoutAssemblyInfo_NonGeneric() =>
        Assert.Equal("System.String", typeof(string).FullNameWithoutAssemblyInfo());

    [Fact]
    public void FullNameWithoutAssemblyInfo_Generic()
    {
        var name = typeof(List<string>).FullNameWithoutAssemblyInfo();

        Assert.DoesNotContain("Version=", name);
        Assert.StartsWith("System.Collections.Generic.List`1[[System.String", name);
    }

    [Fact]
    public void FullNameWithoutAssemblyInfo_OpenGeneric_FallsBackToName() =>
        // open generic parameters have no FullName
        Assert.Equal("T", typeof(List<>).GetGenericArguments()[0].FullNameWithoutAssemblyInfo());
}
