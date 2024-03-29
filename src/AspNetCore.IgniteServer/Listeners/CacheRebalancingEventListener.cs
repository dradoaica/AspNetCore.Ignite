using Apache.Ignite.Core;
using Apache.Ignite.Core.Events;
using Serilog.Core;

namespace AspNetCore.IgniteServer.Listeners;

internal sealed class CacheRebalancingEventListener : IEventListener<CacheRebalancingEvent>
{
    private readonly IIgnite ignite;
    private readonly Logger? logger;

    public CacheRebalancingEventListener(IIgnite ignite, Logger? logger)
    {
        this.ignite = ignite;
        this.logger = logger;
    }

    public bool Invoke(CacheRebalancingEvent evt)
    {
        if (evt.Type == EventType.CacheRebalancePartDataLost)
        {
            logger?.Warning(
                $"Name: {evt.Name}; CacheName: {evt.CacheName}; DiscoveryEventName: {evt.DiscoveryEventName}; DiscoveryNodeAddresses: {string.Join(",", evt.DiscoveryNode.Addresses)}; DiscoveryHostNames: {string.Join(",", evt.DiscoveryNode.HostNames)}");
            try
            {
                ignite.ResetLostPartitions(evt.CacheName);
            }
            catch
            {
                // best effort
            }
        }

        return true;
    }
}
