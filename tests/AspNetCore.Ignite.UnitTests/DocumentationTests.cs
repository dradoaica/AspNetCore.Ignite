using AspNetCore.IgniteServer;
using Microsoft.Extensions.Configuration;
using System.Reflection;
using System.Text.RegularExpressions;
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

    /// <summary>
    ///     The TLA+ spec of the node lifecycle (docs/tlaplus/ServerLifecycle) allows the same phase changes as
    ///     <see cref="IgniteServerRunner.Transition" />, so that what TLC checks is what the runner does.
    /// </summary>
    [Fact]
    public void LifecycleSpec_AllowsThePhaseChangesOfTheRunner()
    {
        var spec = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "docs", "tlaplus", "ServerLifecycle.tla"));
        var table = spec[spec.IndexOf("Allowed ==", StringComparison.Ordinal)..];
        table = table[..table.IndexOf("\n\n", StringComparison.Ordinal)];

        var allowed = Regex.Matches(table, @"(\w+) \|-> \{([^}]*)\}")
            .ToDictionary(
                match => match.Groups[1].Value,
                match => Regex.Matches(match.Groups[2].Value, "\"(\\w+)\"").Select(name => name.Groups[1].Value).Order()
            );

        Assert.Equal(
            IgniteServerRunner.AllowedTransitions.ToDictionary(
                pair => pair.Key.ToString(),
                pair => pair.Value.Select(phase => phase.ToString()).Order()
            ),
            allowed
        );
    }
}
