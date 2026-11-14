using Apache.Ignite.Core.Events;
using Serilog;
using Serilog.Events;
using System.Collections.Generic;

namespace AspNetCore.IgniteServer.Listeners;

internal sealed class DiscoveryEventListener(ILogger logger) : IEventListener<DiscoveryEvent>
{
    private const string MessageTemplate =
        "Name: {Name}; EventNodeAddresses: {EventNodeAddresses}; EventNodeHostNames: {EventNodeHostNames}";

    // Ignite's event types are static fields, not constants, so they cannot be switch cases.
    private static readonly Dictionary<int, LogEventLevel> Levels = new()
    {
        [EventType.NodeJoined] = LogEventLevel.Information,
        [EventType.NodeLeft] = LogEventLevel.Warning,
        [EventType.NodeFailed] = LogEventLevel.Error,
        // The local node was cut off from the cluster. It stops, and the server exits with code 1.
        [EventType.NodeSegmented] = LogEventLevel.Error,
    };

    public bool Invoke(DiscoveryEvent evt)
    {
        if (Levels.TryGetValue(evt.Type, out var level))
        {
            logger.Write(
                level,
                MessageTemplate,
                evt.Name,
                string.Join(",", evt.EventNode.Addresses),
                string.Join(",", evt.EventNode.HostNames)
            );
        }

        return true;
    }
}
