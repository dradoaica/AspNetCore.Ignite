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
                _logger.Warning($"Name: {evt.Name}; CacheName: {evt.CacheName}; DiscoveryEventName: {evt.DiscoveryEventName}; DiscoveryNodeAddresses: {string.Join(",", evt.DiscoveryNode.Addresses)}; DiscoveryHostNames: {string.Join(",", evt.DiscoveryNode.HostNames)}");
                try
                {
                    _ignite.ResetLostPartitions(evt.CacheName);
                }
                catch
                {
                    // best effort
                }
            }

            return true;
        }
    }
}
