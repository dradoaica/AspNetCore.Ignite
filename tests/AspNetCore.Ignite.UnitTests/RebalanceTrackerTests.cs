using AspNetCore.IgniteServer.Listeners;
using Microsoft.Extensions.Time.Testing;
using Serilog.Core;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class RebalanceTrackerTests
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(10);
    private static readonly Func<bool> InBaseline = () => true;
    private static readonly Func<bool> NotInBaseline = () => false;

    [Fact]
    public void StartedAndStopped_TrackTheRebalancingCaches()
    {
        FakeTimeProvider time = new();
        RebalanceTracker tracker = new(Logger.None, MaxWait, time);

        tracker.OnStarted("a");
        tracker.OnStarted("b");
        Assert.True(tracker.IsCatchingUp(InBaseline));
        Assert.Equal(["a", "b",], tracker.RebalancingCaches.Order());

        tracker.OnStopped("a");
        Assert.Equal(["b",], tracker.RebalancingCaches);
        tracker.OnStopped("b");

        Assert.False(tracker.IsCatchingUp(InBaseline));
    }

    [Fact]
    public void ARebalanceThatNeverEnds_StopsBlockingAfterTheMaxWait()
    {
        FakeTimeProvider time = new();
        RebalanceTracker tracker = new(Logger.None, MaxWait, time);
        tracker.OnStarted("a");

        time.Advance(MaxWait - TimeSpan.FromSeconds(1));
        Assert.True(tracker.IsCatchingUp(InBaseline));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(tracker.IsCatchingUp(InBaseline));
    }

    [Fact]
    public void Reset_ForgetsThePreviousRun()
    {
        FakeTimeProvider time = new();
        var tracker = CaughtUp(time);

        tracker.Reset();

        Assert.Empty(tracker.RebalancingCaches);
        // the restarted node catches up again
        tracker.OnStarted("b");
        Assert.True(tracker.IsCatchingUp(InBaseline));
    }

    [Fact]
    public void AStuckCache_DoesNotEndTheWaitForTheOthers()
    {
        FakeTimeProvider time = new();
        RebalanceTracker tracker = new(Logger.None, MaxWait, time);
        tracker.OnStarted("stuck");
        time.Advance(MaxWait);

        tracker.OnStarted("recent");

        // "stuck" exceeded the max wait, "recent" did not: still rebalancing
        Assert.True(tracker.IsCatchingUp(InBaseline));
        tracker.OnStopped("recent");
        Assert.False(tracker.IsCatchingUp(InBaseline));
    }

    [Fact]
    public void RestartedRebalances_DoNotExtendTheMaxWait()
    {
        FakeTimeProvider time = new();
        RebalanceTracker tracker = new(Logger.None, MaxWait, time);
        tracker.OnStarted("a");

        // topology changes restart the rebalance before it ends
        for (var i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromMinutes(2));
            tracker.OnStarted("a");
        }

        Assert.False(tracker.IsCatchingUp(InBaseline));
    }

    [Fact]
    public void RebalancesAfterTheCatchUp_DoNotMakeTheNodeUnready()
    {
        FakeTimeProvider time = new();
        var tracker = CaughtUp(time);

        // e.g., another node left: every node rebalances, and none must become unready
        tracker.OnStarted("a");

        Assert.False(tracker.IsCatchingUp(InBaseline));
        Assert.Equal(["a",], tracker.RebalancingCaches);
    }

    [Fact]
    public void ARebalanceRightAfterAnother_IsStillTheCatchUp()
    {
        FakeTimeProvider time = new();
        RebalanceTracker tracker = new(Logger.None, MaxWait, time);
        tracker.OnRunning();
        tracker.OnStarted("a");
        tracker.OnStopped("a");
        Assert.False(tracker.IsCatchingUp(InBaseline));
        time.Advance(RebalanceTracker.CatchUpSettleTime - TimeSpan.FromSeconds(1));

        // Ignite rebalances the caches one after another
        tracker.OnStarted("b");

        Assert.True(tracker.IsCatchingUp(InBaseline));
    }

    [Fact]
    public void BeforeTheNodeRuns_TheCatchUpCannotEnd()
    {
        FakeTimeProvider time = new();
        RebalanceTracker tracker = new(Logger.None, MaxWait, time);
        tracker.OnStarted("a");
        tracker.OnStopped("a");
        time.Advance(TimeSpan.FromHours(1));

        tracker.OnStarted("b");

        Assert.True(tracker.IsCatchingUp(InBaseline));
    }

    [Fact]
    public void AStuckCache_DoesNotPreventTheEndOfTheCatchUp()
    {
        FakeTimeProvider time = new();
        RebalanceTracker tracker = new(Logger.None, MaxWait, time);
        tracker.OnRunning();
        tracker.OnStarted("stuck");
        Assert.True(tracker.IsCatchingUp(InBaseline));
        time.Advance(MaxWait + RebalanceTracker.CatchUpSettleTime);

        tracker.OnStarted("later");

        Assert.False(tracker.IsCatchingUp(InBaseline));
    }

    [Fact]
    public void OutsideTheBaseline_TheRunningNodeWaits_AtMostTheMaxWait()
    {
        FakeTimeProvider time = new();
        RebalanceTracker tracker = new(Logger.None, MaxWait, time);
        tracker.OnRunning();

        // a joining node holds no data until the baseline auto-adjust adds it
        Assert.True(tracker.IsCatchingUp(NotInBaseline));
        time.Advance(MaxWait);
        Assert.False(tracker.IsCatchingUp(NotInBaseline));
    }

    [Fact]
    public void TheRebalanceOnceInTheBaseline_IsTheCatchUp()
    {
        FakeTimeProvider time = new();
        RebalanceTracker tracker = new(Logger.None, MaxWait, time);
        tracker.OnRunning();
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(tracker.IsCatchingUp(NotInBaseline));

        // the baseline auto-adjust adds the node, which then receives its partitions
        tracker.OnStarted("a");
        Assert.True(tracker.IsCatchingUp(InBaseline));
        tracker.OnStopped("a");
        Assert.False(tracker.IsCatchingUp(InBaseline));
        time.Advance(RebalanceTracker.CatchUpSettleTime);
        tracker.OnStarted("b");

        Assert.False(tracker.IsCatchingUp(InBaseline));
    }

    /// <summary>A running node, in the baseline, that rebalanced after its start and has been quiet since.</summary>
    private static RebalanceTracker CaughtUp(FakeTimeProvider time)
    {
        RebalanceTracker tracker = new(Logger.None, MaxWait, time);
        tracker.OnStarted("a");
        tracker.OnRunning();
        tracker.OnStopped("a");
        Assert.False(tracker.IsCatchingUp(InBaseline));
        time.Advance(RebalanceTracker.CatchUpSettleTime);
        Assert.False(tracker.IsCatchingUp(InBaseline));
        return tracker;
    }
}
