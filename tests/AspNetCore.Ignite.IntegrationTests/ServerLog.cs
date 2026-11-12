namespace AspNetCore.Ignite.IntegrationTests;

/// <summary>
///     The parts of the server's log messages that the integration tests wait for. Each one must be part of a message
///     in the server's code: a unit test (<c>ServerLogTests</c>) fails when a message is reworded without updating
///     this list, long before an integration test would time out waiting for it.
/// </summary>
internal static class ServerLog
{
    public const string Running = "Ignite server runner is running";
    public const string Stopped = "Ignite server runner stopped.";
    public const string WaitingForServerNodes = "server nodes before activating the cluster";
    public const string RebalancingOfCache = "Rebalancing of cache";
    public const string RebalancingFinished = "finished in";
    public const string PartitionDataLost = "Partition data lost";
    public const string LostPartitionsResetWithoutData = "were reset; their data is gone";
    public const string LostPartitionsResetWithReturnedData = "with the data of the nodes that returned";
    public const string LostPartitionsWaitForOwner = "once a node that owns them returns";
    public const string SslFilesChanged = "SSL files changed, restarting the Ignite node";
    public const string DeactivatingAsLastServerNode = "deactivating the cluster so that it stops at once";
    public const string ActivatedAgainAfterRestart = "again after the restart of its last server node";
}
