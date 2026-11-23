using AspNetCore.IgniteServer;
using Microsoft.Extensions.Configuration;
using System.Reflection;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class DocumentationTests
{
    private static readonly Lazy<string> ConfigurationDocs = new(() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "docs", "configuration.md"))
    );

    public static TheoryData<string> ServerSettings
    {
        get
        {
            TheoryData<string> data = [];
            foreach (var property in typeof(IgniteServerSettings).GetProperties())
            {
                if (property.GetCustomAttribute<ConfigurationKeyNameAttribute>() is { } key)
                {
                    data.Add(key.Name);
                }
            }

            return data;
        }
    }

    /// <summary>Every setting of the server is named in docs/configuration.md, so that a new one cannot go undocumented.</summary>
    [Theory]
    [MemberData(nameof(ServerSettings))]
    public void ServerSetting_IsInTheConfigurationDocs(string key) => Assert.True(
        ConfigurationDocs.Value.Contains($"`{key}`", StringComparison.Ordinal),
        $"{key} is not in docs/configuration.md: add it to the Settings section."
    );
}
