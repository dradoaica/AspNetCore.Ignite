using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Help;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace AspNetCore.IgniteServer;

/// <summary>
///     The command line options; each overrides the setting of the same meaning (appsettings.json &lt; environment
///     &lt; command line).
/// </summary>
internal sealed class ServerCommandLine
{
    private readonly Option<string[]> clusterOption = new("-Cluster")
    {
        Description = "Specifies IP address and port of a cluster node. Multiple nodes can be specified.",
        Arity = ArgumentArity.ZeroOrMore,
    };

    private readonly Option<string> configFileOption = new("-ConfigFile")
    {
        Description = "XML configuration file. If no file is specified then the default configuration is used.",
    };

    private readonly Option<string> consistentIdOption = new("-ConsistentId")
    {
        Description = "Specifies as a consistent id of the node. This value is used in topology.",
    };

    private readonly Option<int?> offHeapOption = new("-OffHeap")
    {
        Description = "Size of off-heap memory given in megabytes.",
    };

    private readonly Option<int?> onHeapOption = new("-OnHeap")
    {
        Description = "Size of on-heap memory given in megabytes.",
    };

    private readonly Option<bool> persistenceEnabledOption = new("-PersistenceEnabled")
    {
        Description = "If set, it enables persistence mode.",
    };

    private readonly Option<int?> spiPortOption = new("-SpiPort")
    {
        Description = "Specifies port for Discovery Spi.",
    };

    public ServerCommandLine(Func<IReadOnlyDictionary<string, string?>, Task<int>> run)
    {
        RootCommand = new RootCommand("AspNetCore.IgniteServer: Apache Ignite 2 server node.")
        {
            configFileOption,
            offHeapOption,
            onHeapOption,
            spiPortOption,
            clusterOption,
            consistentIdOption,
            persistenceEnabledOption,
        };
        RootCommand.Options.OfType<HelpOption>().Single().Aliases.Add("-Help");
        RootCommand.SetAction((parseResult, _) => run(GetOverrides(parseResult)));
    }

    public RootCommand RootCommand { get; }

    /// <summary>The settings the parsed options override (only the options that were given).</summary>
    public IReadOnlyDictionary<string, string?> GetOverrides(ParseResult parseResult)
    {
        Dictionary<string, string?> overrides = [];
        AddOverride(overrides, "CONFIG_FILE", parseResult.GetValue(configFileOption));
        AddOverride(overrides, "DEFAULT_OFF_HEAP_MEMORY", parseResult.GetValue(offHeapOption));
        AddOverride(overrides, "DEFAULT_ON_HEAP_MEMORY", parseResult.GetValue(onHeapOption));
        AddOverride(overrides, "SPI_PORT", parseResult.GetValue(spiPortOption));
        AddOverride(overrides, "DEFAULT_CONSISTENT_ID", parseResult.GetValue(consistentIdOption));
        if (parseResult.GetValue(persistenceEnabledOption))
        {
            overrides["PERSISTENCE_ENABLED"] = "true";
        }

        var clusterEndpoints = parseResult.GetValue(clusterOption) ?? [];
        for (var i = 0; i < clusterEndpoints.Length; i++)
        {
            overrides[$"CLUSTER_ENDPOINTS:{i}"] = clusterEndpoints[i];
        }

        return overrides;
    }

    /// <summary>Runs the command line; the host handles SIGTERM/CTRL+C (graceful shutdown), not the parser.</summary>
    public Task<int> InvokeAsync(string[] args) => RootCommand.Parse(args)
        .InvokeAsync(
            new InvocationConfiguration
            {
                ProcessTerminationTimeout = null,
            }
        );

    private static void AddOverride<T>(Dictionary<string, string?> overrides, string key, T? value)
    {
        if (value is not null && (value is not string text || !string.IsNullOrWhiteSpace(text)))
        {
            overrides[key] = Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}
