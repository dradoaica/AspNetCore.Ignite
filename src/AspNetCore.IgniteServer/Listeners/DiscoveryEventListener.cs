using Apache.Ignite.Core.Events;
using Serilog.Core;

namespace AspNetCore.IgniteServer.Listeners;

internal sealed class DiscoveryEventListener : IEventListener<DiscoveryEvent>
{
    private readonly Logger? logger;

    public DiscoveryEventListener(Logger? logger)
    {
        this.logger = logger;
    }

    public bool Invoke(DiscoveryEvent evt)
    {
        if (evt.Type == EventType.NodeJoined)
        {
            logger?.Information(
                $"Name: {evt.Name}; EventNodeAddresses: {string.Join(",", evt.EventNode.Addresses)}; EventNodeHostNames: {string.Join(",", evt.EventNode.HostNames)}");
        }
        else if (evt.Type == EventType.NodeLeft)
        {
            logger?.Warning(
                $"Name: {evt.Name}; EventNodeAddresses: {string.Join(",", evt.EventNode.Addresses)}; EventNodeHostNames: {string.Join(",", evt.EventNode.HostNames)}");
        }
        else if (evt.Type == EventType.NodeFailed)
        {
            logger?.Error(
                $"Name: {evt.Name}; EventNodeAddresses: {string.Join(",", evt.EventNode.Addresses)}; EventNodeHostNames: {string.Join(",", evt.EventNode.HostNames)}");
        }

        return true;
    }
}
