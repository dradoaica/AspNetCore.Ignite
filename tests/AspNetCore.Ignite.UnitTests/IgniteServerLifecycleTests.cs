using Apache.Ignite.Core;
using Apache.Ignite.Core.Cluster;
using Apache.Ignite.Core.Events;
using AspNetCore.IgniteServer;
using Serilog.Core;
using System.Collections.Concurrent;
using Xunit;
using RestartResult = AspNetCore.IgniteServer.IgniteServerRunner.RestartResult;

namespace AspNetCore.Ignite.UnitTests;

/// <summary>
///     The orderings of starts, restarts and stops of <see cref="IgniteServerRunner" />, on nodes of a fake launcher
///     whose stops can be held back (the integration tests cannot time them).
/// </summary>
public sealed class IgniteServerLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Restart_StartsTheNodeAgain_AndStopEndsTheRun()
    {
        FakeLauncher launcher = new();
        using var runner = CreateRunner(launcher);
        var run = runner.Run();

        Assert.Equal(RestartResult.Restarting, runner.Restart(Timeout));
        await Eventually(() => launcher.Started == 2 && runner.Node != null);
        Assert.True(runner.Stop(Timeout));

        await run.WaitAsync(Timeout);
        Assert.Equal(2, launcher.Started);
    }

    [Fact]
    public async Task NodeThatStopsOnItsOwn_EndsTheRun()
    {
        FakeLauncher launcher = new();
        using var runner = CreateRunner(launcher);
        var run = runner.Run();

        launcher.Stop(runner.Node!.Name, false);

        await run.WaitAsync(Timeout);
        Assert.Equal(1, launcher.Started);
    }

    [Fact]
    public void Restart_WithoutRunningNode_DoesNothing()
    {
        FakeLauncher launcher = new();
        using var runner = CreateRunner(launcher);

        Assert.Equal(RestartResult.NotRunning, runner.Restart(Timeout));
        Assert.Equal(0, launcher.Started);
    }

    [Fact]
    public async Task RestartThatTimesOut_IsNotStartedAgain_AndStopWaitsForThatStop()
    {
        FakeLauncher launcher = new();
        using var runner = CreateRunner(launcher);
        var run = runner.Run();
        using ManualResetEventSlim release = new();
        launcher.BeforeStopped = () => release.Wait();

        Assert.Equal(RestartResult.TimedOut, runner.Restart(TimeSpan.FromMilliseconds(100)));
        // the node is still stopping: the stop that follows waits for it rather than reporting it stopped
        Assert.False(runner.Stop(TimeSpan.FromMilliseconds(100)));
        release.Set();

        await run.WaitAsync(Timeout);
        Assert.True(runner.Stop(Timeout));
        Assert.Equal(1, launcher.Started);
    }

    [Fact]
    public async Task RestartWhoseStopEndsJustAfterTheTimeout_IsReportedAsRestarting()
    {
        FakeLauncher launcher = new();
        using var runner = CreateRunner(launcher);
        var run = runner.Run();
        using ManualResetEventSlim release = new();
        // The node reports its stop (the run starts it again), but Ignition.Stop returns only after the timeout.
        launcher.AfterStopped = () => release.Wait();

        var result = runner.Restart(TimeSpan.FromMilliseconds(500));
        release.Set();

        Assert.Equal(RestartResult.Restarting, result);
        await Eventually(() => launcher.Started == 2);
        Assert.True(runner.Stop(Timeout));
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Dispose_DoesNotWaitForAStopThatTimedOut()
    {
        FakeLauncher launcher = new();
        var runner = CreateRunner(launcher);
        _ = runner.Run();
        using ManualResetEventSlim release = new();
        launcher.BeforeStopped = () => release.Wait();
        Assert.False(runner.Stop(TimeSpan.FromMilliseconds(100)));

        await Task.Run(runner.Dispose).WaitAsync(Timeout);
        release.Set();
    }

    [Fact]
    public async Task Stop_BeforeTheRun_StartsNoNode()
    {
        FakeLauncher launcher = new();
        using var runner = CreateRunner(launcher);

        Assert.True(runner.Stop(Timeout));
        await runner.Run().WaitAsync(Timeout);

        Assert.Equal(0, launcher.Started);
    }

    private static IgniteServerRunner CreateRunner(FakeLauncher launcher) => new(
        new IgniteServerSettings
        {
            // in memory, so that the cluster is activated at once
            EnableAuthentication = false,
        },
        Logger.None,
        () => "127.0.0.1",
        null,
        launcher
    );

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    /// <summary>
    ///     Nodes in a cluster of two server nodes (so a stopping node does not deactivate it), whose stops run
    ///     <see cref="BeforeStopped" />, raise the node's Stopped event and run <see cref="AfterStopped" />.
    /// </summary>
    private sealed class FakeLauncher : INodeLauncher
    {
        private readonly ConcurrentDictionary<string, EventHandler> stoppedHandlers = new();
        private int started;

        public int Started => Volatile.Read(ref started);
        public Action BeforeStopped { get; set; } = () => { };
        public Action AfterStopped { get; set; } = () => { };

        public IIgnite Start(IgniteConfiguration configuration)
        {
            var name = $"node-{Interlocked.Increment(ref started)}";
            var active = false;
            var servers = Fake.Of<IClusterGroup>((member, _) => member switch
                {
                    nameof(IClusterGroup.GetNodes) => new List<IClusterNode>
                    {
                        Fake.Of<IClusterNode>((other, _) => throw new NotSupportedException(other)),
                        Fake.Of<IClusterNode>((other, _) => throw new NotSupportedException(other)),
                    },
                    _ => throw new NotSupportedException(member),
                }
            );
            var cluster = Fake.Of<ICluster>((member, args) => member switch
                {
                    nameof(ICluster.IsActive) => active,
                    nameof(ICluster.SetActive) => Done(() => active = (bool)args[0]!),
                    nameof(ICluster.ForServers) => servers,
                    nameof(ICluster.SetBaselineAutoAdjustEnabledFlag) or
                        nameof(ICluster.SetBaselineAutoAdjustTimeout) => null,
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
                    nameof(IIgnite.GetCluster) => cluster,
                    nameof(IIgnite.GetEvents) => events,
                    nameof(IIgnite.GetConfiguration) => configuration,
                    _ => throw new NotSupportedException(member),
                }
            );
        }

        public bool Stop(string name, bool cancel)
        {
            BeforeStopped();
            stoppedHandlers[name](name, EventArgs.Empty);
            AfterStopped();
            return true;
        }

        private static object? Done(Action action)
        {
            action();
            return null;
        }
    }
}
