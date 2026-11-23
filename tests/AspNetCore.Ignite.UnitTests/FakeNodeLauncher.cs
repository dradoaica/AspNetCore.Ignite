using Apache.Ignite.Core;
using Apache.Ignite.Core.Cluster;
using Apache.Ignite.Core.Events;
using AspNetCore.IgniteServer;
using System.Collections.Concurrent;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

/// <summary>
///     Starts fake nodes in a cluster of <see cref="ServerNodes" /> server nodes (two by default, so that a stopping node
///     does not deactivate it). A start runs <see cref="BeforeStarted" />, and a stop runs <see cref="BeforeStopped" />,
///     raises the node's Stopped event and runs <see cref="AfterStopped" />, so a test can hold either back; it can also
///     hold back the reads of the cluster (<see cref="BeforeClusterRead" />) and its activation
///     (<see cref="BeforeActivated" />).
/// </summary>
internal sealed class FakeNodeLauncher : INodeLauncher
{
    private static readonly TimeSpan EventuallyTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentQueue<string> activatedNodes = new();
    private readonly ConcurrentDictionary<string, EventHandler> stoppedHandlers = new();
    private int deactivations;
    private int failedClusterReads;
    private int started;
    private int stops;

    public int Started => Volatile.Read(ref started);
    public int Stops => Volatile.Read(ref stops);
    public Action BeforeStarted { get; set; } = () => { };
    public Action BeforeStopped { get; set; } = () => { };
    public Action AfterStopped { get; set; } = () => { };

    /// <summary>Runs at the start of every <see cref="IIgnite.GetCluster" /> of the nodes.</summary>
    public Action BeforeClusterRead { get; set; } = () => { };

    /// <summary>Runs when a cluster is activated, before it is active.</summary>
    public Action BeforeActivated { get; set; } = () => { };

    /// <summary>The server nodes of the cluster, this node included: with one, a stopping node deactivates it.</summary>
    public int ServerNodes { get; set; } = 2;

    /// <summary>The nodes whose cluster was activated, in order.</summary>
    public IReadOnlyList<string> ActivatedNodes => [.. activatedNodes,];

    public int Deactivations => Volatile.Read(ref deactivations);

    /// <summary>
    ///     Makes the nodes fail to read their cluster (e.g., while they stop), counted by
    ///     <see cref="FailedClusterReads" />.
    /// </summary>
    public bool FailClusterReads { get; set; }

    public int FailedClusterReads => Volatile.Read(ref failedClusterReads);

    public IIgnite Start(IgniteConfiguration configuration)
    {
        BeforeStarted();
        var name = $"node-{Interlocked.Increment(ref started)}";
        var active = false;
        var servers = Fake.Of<IClusterGroup>((member, _) => member switch
            {
                nameof(IClusterGroup.GetNodes) => Enumerable.Range(0, ServerNodes)
                    .Select(_ => Fake.Of<IClusterNode>((other, _) => throw new NotSupportedException(other)))
                    .ToList(),
                _ => throw new NotSupportedException(member),
            }
        );
        var cluster = Fake.Of<ICluster>((member, args) => member switch
            {
                nameof(ICluster.IsActive) => Volatile.Read(ref active),
                nameof(ICluster.SetActive) => Done(() => SetActive(name, ref active, (bool)args[0]!)),
                nameof(ICluster.ForServers) => servers,
                nameof(ICluster.SetBaselineAutoAdjustEnabledFlag)
                    or nameof(ICluster.SetBaselineAutoAdjustTimeout) => null,
                _ => throw new NotSupportedException(member),
            }
        );
        var events = Fake.Of<IEvents>((member, _) =>
            member == nameof(IEvents.LocalListen) ? null : throw new NotSupportedException(member)
        );
        return Fake.Of<IIgnite>((member, args) => member switch
            {
                nameof(IIgnite.Name) => name,
                "add_" + nameof(IIgnite.Stopped) => Done(() => stoppedHandlers[name] = (EventHandler)args[0]!),
                nameof(IIgnite.GetCluster) => ReadCluster(cluster),
                nameof(IIgnite.GetEvents) => events,
                nameof(IIgnite.GetConfiguration) => configuration,
                _ => throw new NotSupportedException(member),
            }
        );
    }

    public bool Stop(string name, bool cancel)
    {
        Interlocked.Increment(ref stops);
        BeforeStopped();
        stoppedHandlers[name](name, EventArgs.Empty);
        AfterStopped();
        return true;
    }

    /// <summary>Waits until <paramref name="condition" /> holds, failing the test after ten seconds.</summary>
    public static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + EventuallyTimeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    private ICluster ReadCluster(ICluster cluster)
    {
        BeforeClusterRead();
        if (!FailClusterReads)
        {
            return cluster;
        }

        Interlocked.Increment(ref failedClusterReads);
        throw new InvalidOperationException("The node is stopping.");
    }

    private void SetActive(string name, ref bool active, bool value)
    {
        if (value)
        {
            BeforeActivated();
            activatedNodes.Enqueue(name);
        }
        else
        {
            Interlocked.Increment(ref deactivations);
        }

        Volatile.Write(ref active, value);
    }

    private static object? Done(Action action)
    {
        action();
        return null;
    }
}
