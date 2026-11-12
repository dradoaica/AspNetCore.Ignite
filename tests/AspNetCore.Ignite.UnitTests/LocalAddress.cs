namespace AspNetCore.Ignite.UnitTests;

/// <summary>Stands in for the DNS lookup of the local address, which the tests must not depend on.</summary>
internal static class LocalAddress
{
    public const string Value = "10.1.2.3";

    public static string Resolve() => Value;
}
