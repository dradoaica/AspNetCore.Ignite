using Apache.Ignite.Core;
using Apache.Ignite.Core.Events;
using Serilog.Core;

namespace AspNetCore.IgniteServer.Listeners;

internal sealed class CacheRebalancingEventListener(IIgnite ignite, Logger? logger)
    : IEventListener<CacheRebalancingEvent>
{
    public bool Invoke(CacheRebalancingEvent evt)
    {
        if (evt.Type != EventType.CacheRebalancePartDataLost)
        {
            return true;
        }

        logger?.Warning(
            $"Name: {evt.Name}; CacheName: {evt.CacheName}; DiscoveryEventName: {evt.DiscoveryEventName}; DiscoveryNodeAddresses: {string.Join(",", evt.DiscoveryNode.Addresses)}; DiscoveryHostNames: {string.Join(",", evt.DiscoveryNode.HostNames)}"
        );
        try
        {
            ignite.ResetLostPartitions(evt.CacheName);
        }
        catch
        {
            // best effort
        }

        return true;
    }
}
