using Apache.Ignite.Core.Events;
using Serilog;
using Serilog.Events;

namespace AspNetCore.IgniteServer.Listeners;

internal sealed class DiscoveryEventListener(ILogger logger) : IEventListener<DiscoveryEvent>
{
    private const string MessageTemplate =
        "Name: {Name}; EventNodeAddresses: {EventNodeAddresses}; EventNodeHostNames: {EventNodeHostNames}";

    public bool Invoke(DiscoveryEvent evt)
    {
        LogEventLevel? level = evt.Type == EventType.NodeJoined ? LogEventLevel.Information
            : evt.Type == EventType.NodeLeft ? LogEventLevel.Warning : evt.Type == EventType.NodeFailed
                ? LogEventLevel.Error
                // the local node was cut off from the cluster: it stops, and so does the server (exit code 1)
                : evt.Type == EventType.NodeSegmented ? LogEventLevel.Error : null;
        if (level.HasValue)
        {
            logger.Write(
                level.Value,
                MessageTemplate,
                evt.Name,
                string.Join(",", evt.EventNode.Addresses),
                string.Join(",", evt.EventNode.HostNames)
            );
        }

        return true;
    }
}
