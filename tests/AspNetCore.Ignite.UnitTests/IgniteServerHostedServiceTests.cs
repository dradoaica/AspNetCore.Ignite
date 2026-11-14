using AspNetCore.IgniteServer;
using Microsoft.Extensions.Hosting;
using Serilog.Core;
using System.Diagnostics;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

/// <summary>
///     How <see cref="IgniteServerHostedService" /> runs, restarts and stops the node, on nodes of a
///     <see cref="FakeNodeLauncher" />. A failure sets the process exit code, which each test resets.
/// </summary>
public sealed class IgniteServerHostedServiceTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly FakeLifetime lifetime = new();
    private readonly FakeNodeLauncher launcher = new();
    private readonly IgniteNodeState nodeState = new();

    public IgniteServerHostedServiceTests() => Environment.ExitCode = 0;

    public void Dispose() => Environment.ExitCode = 0;

    [Fact]
    public async Task GracefulStop_StopsTheNode_WithoutFailing()
    {
        using var service = await StartAsync();

        await service.StopAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.False(nodeState.IsStarted);
        Assert.False(lifetime.StopRequested);
        Assert.Equal(0, Environment.ExitCode);
        // it may contain keystore passwords
        Assert.False(File.Exists(RenderedSpringConfigPath));
    }

    [Fact]
    public async Task NodeThatStopsOnItsOwn_StopsTheServerWithExitCodeOne()
    {
        using var service = await StartAsync();

        launcher.Stop(nodeState.Node!.Name, false);

        await FakeNodeLauncher.Eventually(() => lifetime.StopRequested);
        Assert.Equal(1, Environment.ExitCode);
        await service.StopAsync(CancellationToken.None).WaitAsync(Timeout);
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
    }

    [Fact]
    public async Task SslFilesChange_RestartsTheNode()
    {
        using var service = await StartAsync();

        service.OnSslFilesChanged();

        await FakeNodeLauncher.Eventually(() => launcher.Started == 2 && nodeState.IsStarted);
        Assert.False(lifetime.StopRequested);
        await service.StopAsync(CancellationToken.None).WaitAsync(Timeout);
    }

    [Fact]
    public async Task SslRestartThatTimesOut_StopsTheServerWithExitCodeOne()
    {
        using var service = await StartAsync();
        using ManualResetEventSlim release = new();
        launcher.BeforeStopped = () => release.Wait();

        service.OnSslFilesChanged();

        Assert.True(lifetime.StopRequested);
        Assert.Equal(1, Environment.ExitCode);
        Assert.Equal(1, launcher.Started);
        release.Set();
        await service.StopAsync(CancellationToken.None).WaitAsync(Timeout);
    }

    [Fact]
    public async Task SslFilesChange_WhileTheHostStops_DoesNothing()
    {
        using var service = await StartAsync();
        lifetime.StopApplication();

        service.OnSslFilesChanged();

        Assert.Equal(1, launcher.Started);
        Assert.True(nodeState.IsStarted);
        await service.StopAsync(CancellationToken.None).WaitAsync(Timeout);
    }

    [Fact]
    public async Task StopAsync_StopsWaitingForTheNode_AtTheHostsTimeout()
    {
        using var service = await StartAsync(gracefulStopTimeoutInSeconds: 30);
        using ManualResetEventSlim release = new();
        launcher.BeforeStopped = () => release.Wait();
        using CancellationTokenSource hostTimeout = new(TimeSpan.FromMilliseconds(200));
        var stopwatch = Stopwatch.StartNew();

        await service.StopAsync(hostTimeout.Token).WaitAsync(Timeout);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"took {stopwatch.Elapsed}");
        release.Set();
    }

    private static string RenderedSpringConfigPath => Path.Combine(
        Path.GetTempPath(),
        $"aspnetcore-ignite-server-{Environment.ProcessId}",
        "spring-config.xml"
    );

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
        launcher
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
