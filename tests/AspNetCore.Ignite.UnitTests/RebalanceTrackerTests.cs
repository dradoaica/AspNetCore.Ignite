using AspNetCore.IgniteServer.Listeners;
using Microsoft.Extensions.Time.Testing;
using Serilog.Core;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class RebalanceTrackerTests
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(10);

    [Fact]
    public void StartedAndStopped_TrackTheRebalancingCaches()
    {
        FakeTimeProvider time = new();
        RebalanceTracker tracker = new(Logger.None, time);

        tracker.OnStarted("a");
        tracker.OnStarted("b");
        Assert.True(tracker.IsRebalancing(MaxWait));
        Assert.Equal(["a", "b",], tracker.RebalancingCaches.Order());

        tracker.OnStopped("a");
        Assert.Equal(["b",], tracker.RebalancingCaches);
        tracker.OnStopped("b");

        Assert.False(tracker.IsRebalancing(MaxWait));
    }

    [Fact]
    public void ARebalanceThatNeverEnds_StopsBlockingAfterTheMaxWait()
    {
        FakeTimeProvider time = new();
        RebalanceTracker tracker = new(Logger.None, time);
        tracker.OnStarted("a");

        time.Advance(MaxWait - TimeSpan.FromSeconds(1));
        Assert.True(tracker.IsRebalancing(MaxWait));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(tracker.IsRebalancing(MaxWait));
    }

    [Fact]
    public void Reset_ForgetsThePreviousRun()
    {
        RebalanceTracker tracker = new(Logger.None);
        tracker.OnStarted("a");

        tracker.Reset();

        Assert.False(tracker.IsRebalancing(MaxWait));
        Assert.Empty(tracker.RebalancingCaches);
    }
}
