using Apache.Ignite.Core;
using Apache.Ignite.Core.Events;
using Serilog.Core;

namespace AspNetCore.IgniteServer.Listeners
{
    public class DiscoveryEventListener : IEventListener<DiscoveryEvent>
    {
        private readonly IIgnite _ignite;
        private readonly Logger _logger;

        public DiscoveryEventListener(IIgnite ignite, Logger logger)
        {
            _ignite = ignite;
            _logger = logger;
        }

        public bool Invoke(DiscoveryEvent evt)
        {
            if (evt.Type == EventType.NodeLeft)
            {
                _logger.Warning($"Reset lost partitions for all caches due to => Name: {evt.Name}; EventNodeAddresses: {string.Join(",", evt.EventNode.Addresses)}; EventNodeHostNames: {string.Join(",", evt.EventNode.HostNames)}");
                _ignite.ResetLostPartitions(_ignite.GetCacheNames());
            }

            return true;
        }
    }
}
