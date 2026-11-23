using AspNetCore.IgniteServer;
using Microsoft.Extensions.Configuration;
using System.Reflection;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class DocumentationTests
{
    /// <summary>Every setting of the server is named in docs/configuration.md, so that a new one cannot go undocumented.</summary>
    [Fact]
    public void ServerSettings_AreInTheConfigurationDocs()
    {
        var docs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "docs", "configuration.md"));

        var undocumented = typeof(IgniteServerSettings).GetProperties()
            .Select(property => property.GetCustomAttribute<ConfigurationKeyNameAttribute>()?.Name)
            .OfType<string>()
            .Where(key => !docs.Contains($"`{key}`", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            undocumented.Count == 0,
            $"Not in docs/configuration.md (add them to its Settings section): {string.Join(", ", undocumented)}"
        );
    }
}
