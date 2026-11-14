using AspNetCore.IgniteServer;
using AspNetCore.IgniteServer.Utils;
using Microsoft.Extensions.Hosting;
using Serilog.Core;
using System.Diagnostics;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

/// <summary>
///     How <see cref="IgniteServerHostedService" /> runs and stops the node, and the exit code it sets, on nodes of a
///     <see cref="FakeNodeLauncher" />.
/// </summary>
public sealed class IgniteServerHostedServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ServerExit exit = new();
    private readonly FakeLifetime lifetime = new();
    private readonly FakeNodeLauncher launcher = new();
    private readonly IgniteNodeState nodeState = new();

    [Fact]
    public async Task GracefulStop_StopsTheNode_WithExitCodeZero()
    {
        using var service = await StartAsync();

        await service.StopAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.False(nodeState.IsStarted);
        Assert.False(lifetime.StopRequested);
        Assert.Equal(0, exit.Code);
        // it may contain keystore passwords
        Assert.False(File.Exists(Path.Combine(SpringConfigRenderer.DefaultOutputDirectory, "spring-config.xml")));
    }

    [Fact]
    public async Task StopThatIsNotGraceful_HasExitCodeTwo()
    {
        using var service = await StartAsync();
        using ManualResetEventSlim release = new();
        launcher.BeforeStopped = () => release.Wait();

        // GRACEFUL_STOP_TIMEOUT_IN_SECONDS is 1
        await service.StopAsync(CancellationToken.None).WaitAsync(Timeout);
        release.Set();

        Assert.Equal(ServerExit.NotGraceful, exit.Code);
    }

    [Fact]
    public async Task NodeThatStopsOnItsOwn_StopsTheServerWithExitCodeOne()
    {
        using var service = await StartAsync();

        launcher.Stop(nodeState.Node!.Name, false);

        await FakeNodeLauncher.Eventually(() => lifetime.StopRequested);
        Assert.Equal(ServerExit.Failure, exit.Code);
        // the failure is the cause: the stop that follows does not change the exit code
        await service.StopAsync(CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(ServerExit.Failure, exit.Code);
    }

    [Fact]
    public async Task StopBeforeTheStart_StartsNoNode()
    {
        using var service = CreateService();

        await service.StopAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(Timeout);

        Assert.Equal(0, launcher.Started);
        Assert.False(lifetime.StopRequested);
        Assert.Equal(0, exit.Code);
    }

    [Fact]
    public async Task StopAsync_StopsWaitingForTheNode_AtTheHostsTimeout()
    {
        using var service = await StartAsync(gracefulStopTimeoutInSeconds: 60);
        using ManualResetEventSlim release = new();
        launcher.BeforeStopped = () => release.Wait();
        using CancellationTokenSource hostTimeout = new(TimeSpan.FromMilliseconds(200));
        var stopwatch = Stopwatch.StartNew();

        await service.StopAsync(hostTimeout.Token).WaitAsync(Timeout);

        // far below the graceful stop timeout, which it would otherwise wait for
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
        Assert.Equal(ServerExit.NotGraceful, exit.Code);
        release.Set();
    }

    private IgniteServerHostedService CreateService(int gracefulStopTimeoutInSeconds = 1) => new(
        new IgniteServerSettings
        {
            // in memory, so that the cluster is activated at once, and no local address lookup
            EnableAuthentication = false,
            ClusterEndpoints = ["127.0.0.1:47500",],
            GracefulStopTimeoutInSeconds = gracefulStopTimeoutInSeconds,
        },
        Logger.None,
        lifetime,
        nodeState,
        launcher,
        exit
    );

    private async Task<IgniteServerHostedService> StartAsync(int gracefulStopTimeoutInSeconds = 1)
    {
        var service = CreateService(gracefulStopTimeoutInSeconds);
        await service.StartAsync(CancellationToken.None);
        await FakeNodeLauncher.Eventually(() => nodeState.IsStarted);
        return service;
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();

        public bool StopRequested { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
            StopRequested = true;
            stopping.Cancel();
        }
    }
}
