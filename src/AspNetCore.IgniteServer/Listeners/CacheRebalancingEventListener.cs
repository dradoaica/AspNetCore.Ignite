using Apache.Ignite.Core;
using Apache.Ignite.Core.Events;
using Serilog;
using System;

namespace AspNetCore.IgniteServer.Listeners;

/// <summary>
///     Logs partition data loss and, when <paramref name="autoResetLostPartitions" /> is set, resets the lost partitions so
///     the cache becomes usable again. Resetting does NOT recover the lost data.
/// </summary>
internal sealed class CacheRebalancingEventListener(IIgnite ignite, ILogger logger, bool autoResetLostPartitions)
    : IEventListener<CacheRebalancingEvent>
{
    private const string MessageTemplate =
        "Partition data lost: Name: {Name}; CacheName: {CacheName}; DiscoveryEventName: {DiscoveryEventName}; DiscoveryNodeAddresses: {DiscoveryNodeAddresses}; DiscoveryHostNames: {DiscoveryHostNames}";

    public bool Invoke(CacheRebalancingEvent evt)
    {
        if (evt.Type != EventType.CacheRebalancePartDataLost)
        {
            return true;
        }

        logger.Error(
            MessageTemplate,
            evt.Name,
            evt.CacheName,
            evt.DiscoveryEventName,
            string.Join(",", evt.DiscoveryNode.Addresses),
            string.Join(",", evt.DiscoveryNode.HostNames)
        );
        if (!autoResetLostPartitions)
        {
            return true;
        }

        try
        {
            ignite.ResetLostPartitions(evt.CacheName);
            logger.Warning("Lost partitions of cache {CacheName} were reset; their data is gone.", evt.CacheName);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Failed to reset lost partitions of cache {CacheName}.", evt.CacheName);
        }

        return true;
    }
}
