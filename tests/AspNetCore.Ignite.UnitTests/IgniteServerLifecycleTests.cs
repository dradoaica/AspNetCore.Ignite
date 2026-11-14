using AspNetCore.IgniteServer;
using Serilog.Core;
using Xunit;
using RestartResult = AspNetCore.IgniteServer.IgniteServerRunner.RestartResult;

namespace AspNetCore.Ignite.UnitTests;

/// <summary>
///     The orderings of starts, restarts and stops of <see cref="IgniteServerRunner" />, on nodes of a fake launcher
///     whose stops can be held back (the integration tests cannot time them).
/// </summary>
public sealed class IgniteServerLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Restart_StartsTheNodeAgain_AndStopEndsTheRun()
    {
        FakeNodeLauncher launcher = new();
        using var runner = CreateRunner(launcher);
        var run = runner.Run();

        Assert.Equal(RestartResult.Restarting, runner.Restart(Timeout));
        await FakeNodeLauncher.Eventually(() => launcher.Started == 2 && runner.Node != null);
        Assert.True(runner.Stop(Timeout));

        await run.WaitAsync(Timeout);
        Assert.Equal(2, launcher.Started);
    }

    [Fact]
    public async Task NodeThatStopsOnItsOwn_EndsTheRun()
    {
        FakeNodeLauncher launcher = new();
        using var runner = CreateRunner(launcher);
        var run = runner.Run();

        launcher.Stop(runner.Node!.Name, false);

        await run.WaitAsync(Timeout);
        Assert.Equal(1, launcher.Started);
    }

    [Fact]
    public void Restart_WithoutRunningNode_DoesNothing()
    {
        FakeNodeLauncher launcher = new();
        using var runner = CreateRunner(launcher);

        Assert.Equal(RestartResult.NotRunning, runner.Restart(Timeout));
        Assert.Equal(0, launcher.Started);
    }

    [Fact]
    public async Task RestartThatTimesOut_IsNotStartedAgain_AndStopWaitsForThatStop()
    {
        FakeNodeLauncher launcher = new();
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
        FakeNodeLauncher launcher = new();
        using var runner = CreateRunner(launcher);
        var run = runner.Run();
        using ManualResetEventSlim release = new();
        // The node reports its stop (the run starts it again), but Ignition.Stop returns only after the timeout.
        launcher.AfterStopped = () => release.Wait();

        var result = runner.Restart(TimeSpan.FromSeconds(1));
        release.Set();

        Assert.Equal(RestartResult.Restarting, result);
        await FakeNodeLauncher.Eventually(() => launcher.Started == 2);
        Assert.True(runner.Stop(Timeout));
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Dispose_DoesNotWaitForAStopThatTimedOut()
    {
        FakeNodeLauncher launcher = new();
        var runner = CreateRunner(launcher);
        _ = runner.Run();
        using ManualResetEventSlim release = new();
        launcher.BeforeStopped = () => release.Wait();
        Assert.False(runner.Stop(TimeSpan.FromMilliseconds(100)));

        await Task.Run(runner.Dispose).WaitAsync(Timeout);
        release.Set();
    }

    [Fact]
    public async Task Stop_WhileTheNodeStarts_WaitsForTheStart_AndStopsTheNode()
    {
        FakeNodeLauncher launcher = new();
        using var runner = CreateRunner(launcher);
        using ManualResetEventSlim starting = new(), release = new();
        launcher.BeforeStarted = () =>
        {
            starting.Set();
            release.Wait();
        };
        var run = Task.Run(runner.Run);
        Assert.True(starting.Wait(Timeout));

        var stop = Task.Run(() => runner.Stop(Timeout));
        await Task.Delay(200);
        // it does not report a stop while the node still starts
        Assert.False(stop.IsCompleted);
        release.Set();

        Assert.True(await stop.WaitAsync(Timeout));
        await run.WaitAsync(Timeout);
        Assert.Equal(1, launcher.Stops);
    }

    [Fact]
    public async Task Stop_WhileTheNodeStartsLongerThanTheTimeout_IsNotGraceful_AndTheNodeStopsOnceStarted()
    {
        FakeNodeLauncher launcher = new();
        using var runner = CreateRunner(launcher);
        using ManualResetEventSlim starting = new(), release = new();
        launcher.BeforeStarted = () =>
        {
            starting.Set();
            release.Wait();
        };
        var run = Task.Run(runner.Run);
        Assert.True(starting.Wait(Timeout));

        Assert.False(runner.Stop(TimeSpan.FromMilliseconds(100)));
        release.Set();

        await run.WaitAsync(Timeout);
        Assert.Equal(1, launcher.Stops);
        Assert.Null(runner.Node);
    }

    [Fact]
    public async Task Stop_BeforeTheRun_StartsNoNode()
    {
        FakeNodeLauncher launcher = new();
        using var runner = CreateRunner(launcher);

        Assert.True(runner.Stop(Timeout));
        await runner.Run().WaitAsync(Timeout);

        Assert.Equal(0, launcher.Started);
    }

    private static IgniteServerRunner CreateRunner(FakeNodeLauncher launcher) => new(
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
}
