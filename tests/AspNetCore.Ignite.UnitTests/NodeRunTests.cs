using Apache.Ignite.Core;
using AspNetCore.IgniteServer;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class NodeRunTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly IIgnite Node = Fake.Of<IIgnite>((member, _) => throw new NotSupportedException(member));

    [Fact]
    public async Task BeginStop_StopsTheNodeOnce_AndLaterCallsGetThatStop()
    {
        NodeRun run = new();
        run.OnStarted(Node);
        var stops = 0;
        using ManualResetEventSlim release = new();

        var first = run.BeginStop(_ =>
            {
                Interlocked.Increment(ref stops);
                release.Wait();
                return true;
            }
        );
        var second = run.BeginStop(_ => throw new InvalidOperationException("a second stop"));
        release.Set();

        Assert.Same(first, second);
        Assert.True(await first!.WaitAsync(Timeout));
        Assert.Equal(1, stops);
    }

    [Fact]
    public void BeginStopIfNoneBegan_IsNull_OnceAStopBegan()
    {
        NodeRun run = new();
        run.OnStarted(Node);
        Assert.NotNull(run.BeginStop(_ => true));

        Assert.Null(run.BeginStopIfNoneBegan(_ => throw new InvalidOperationException("a second stop")));
    }

    [Fact]
    public void BeginStop_WhileTheNodeStarts_IsNull()
    {
        NodeRun run = new();

        Assert.Null(run.BeginStop(_ => true));
    }

    [Fact]
    public void BeginStop_OfANodeThatStopped_DoesNotStopIt()
    {
        NodeRun run = new();
        run.OnStarted(Node);
        run.OnStopped(true);

        Assert.True(run.BeginStop(_ => throw new InvalidOperationException("stopped twice"))!.IsCompletedSuccessfully);
        Assert.True(run.StoppedOnItsOwn);
    }

    [Fact]
    public void StopWithin_AFailedStart_HasNothingToStop()
    {
        NodeRun run = new();
        run.OnStarted(null);

        Assert.True(run.StopWithin(Timeout, (_, _) => throw new InvalidOperationException("no node")));
    }

    [Fact]
    public void StopWithin_AStartThatOutlastsTheTimeout_IsFalse()
    {
        NodeRun run = new();

        Assert.False(run.StopWithin(TimeSpan.FromMilliseconds(50), (_, _) => true));
    }

    [Fact]
    public void StopWithin_GivesTheStopTheTimeThatIsLeft()
    {
        NodeRun run = new();
        run.OnStarted(Node);
        var timeLeft = TimeSpan.MaxValue;

        Assert.True(run.StopWithin(Timeout, (_, left) =>
                {
                    timeLeft = left;
                    return true;
                }
            )
        );
        Assert.InRange(timeLeft, TimeSpan.Zero, Timeout);
    }
}
