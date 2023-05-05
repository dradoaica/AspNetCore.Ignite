namespace AspNetCore.IgniteServer.Listeners;

using Apache.Ignite.Core.Events;
using Serilog.Core;

internal sealed class DiscoveryEventListener : IEventListener<DiscoveryEvent>
{
    private readonly Logger logger;

    public DiscoveryEventListener(Logger logger) => this.logger = logger;

    public bool Invoke(DiscoveryEvent evt)
    {
        if (evt.Type == EventType.NodeJoined)
        {
            this.logger.Information(
                $"Name: {evt.Name}; EventNodeAddresses: {string.Join(",", evt.EventNode.Addresses)}; EventNodeHostNames: {string.Join(",", evt.EventNode.HostNames)}");
        }
        else if (evt.Type == EventType.NodeLeft)
        {
            this.logger.Warning(
                $"Name: {evt.Name}; EventNodeAddresses: {string.Join(",", evt.EventNode.Addresses)}; EventNodeHostNames: {string.Join(",", evt.EventNode.HostNames)}");
        }
        else if (evt.Type == EventType.NodeFailed)
        {
            this.logger.Error(
                $"Name: {evt.Name}; EventNodeAddresses: {string.Join(",", evt.EventNode.Addresses)}; EventNodeHostNames: {string.Join(",", evt.EventNode.HostNames)}");
        }

        return true;
    }
}
