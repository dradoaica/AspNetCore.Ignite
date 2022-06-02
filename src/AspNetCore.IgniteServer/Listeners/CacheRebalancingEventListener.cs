using Apache.Ignite.Core;
using Apache.Ignite.Core.Events;
using Serilog.Core;

namespace AspNetCore.IgniteServer.Listeners
{
    public class CacheRebalancingEventListener : IEventListener<CacheRebalancingEvent>
    {
        private readonly IIgnite _ignite;
        private readonly Logger _logger;

        public CacheRebalancingEventListener(IIgnite ignite, Logger logger)
        {
            _ignite = ignite;
            _logger = logger;
        }

        public bool Invoke(CacheRebalancingEvent evt)
        {
            if (evt.Type == EventType.CacheRebalancePartDataLost)
            {
                _logger.Warning($"Reset lost partitions for {evt.CacheName} due to => Name: {evt.Name}; DiscoveryEventName: {evt.DiscoveryEventName}; DiscoveryNodeAddresses: {string.Join(",", evt.DiscoveryNode.Addresses)}; DiscoveryHostNames: {string.Join(",", evt.DiscoveryNode.HostNames)}");
                _ignite.ResetLostPartitions(evt.CacheName);
            }

            return true;
        }
    }
}
