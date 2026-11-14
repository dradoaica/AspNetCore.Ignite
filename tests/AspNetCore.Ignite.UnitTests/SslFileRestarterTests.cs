using AspNetCore.IgniteServer;
using Serilog.Core;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

/// <summary>How <see cref="SslFileRestarter" /> restarts the node of a <see cref="FakeNodeLauncher" />.</summary>
public sealed class SslFileRestarterTests : IDisposable
{
    private readonly FakeNodeLauncher launcher = new();
    private readonly IgniteServerRunner runner;
    private bool failed;
    private bool stopping;

    public SslFileRestarterTests()
    {
        runner = new IgniteServerRunner(Settings, Logger.None, () => "127.0.0.1", null, launcher);
        _ = runner.Run();
    }

    private static IgniteServerSettings Settings => new()
    {
        EnableAuthentication = false,
        GracefulStopTimeoutInSeconds = 1,
    };

    public void Dispose() => runner.Dispose();

    [Fact]
    public async Task FilesChange_RestartTheNode()
    {
        using var restarter = CreateRestarter();

        restarter.OnFilesChanged();

        await FakeNodeLauncher.Eventually(() => launcher.Started == 2 && runner.Node != null);
        Assert.False(failed);
    }

    [Fact]
    public void RestartThatTimesOut_FailsTheServer()
    {
        using var restarter = CreateRestarter();
        using ManualResetEventSlim release = new();
        launcher.BeforeStopped = () => release.Wait();

        restarter.OnFilesChanged();
        release.Set();

        Assert.True(failed);
        Assert.Equal(1, launcher.Started);
    }

    [Fact]
    public void FilesChange_WhileTheHostStops_DoNothing()
    {
        using var restarter = CreateRestarter();
        stopping = true;

        restarter.OnFilesChanged();

        Assert.Equal(1, launcher.Started);
        Assert.NotNull(runner.Node);
        Assert.False(failed);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 2)]
    [InlineData(false, true, 3)]
    [InlineData(true, true, 3)]
    public void SslFiles_AreTheStoresAndTheClientCertificate(bool useSsl, bool useClientSsl, int files) => Assert.Equal(
        files,
        SslFileRestarter.GetSslFiles(
                new IgniteServerSettings
                {
                    UseSsl = useSsl,
                    UseClientSsl = useClientSsl,
                    SslKeyStoreFilePath = "/ssl/server.jks",
                    SslTrustStoreFilePath = "/ssl/trust.jks",
                    SslClientCertificatePath = "/ssl/client.pfx",
                }
            )
            .Count
    );

    [Fact]
    public void WithoutRestartOnSslFileChange_NothingIsWatched() => Assert.Null(
        SslFileRestarter.Create(
            new IgniteServerSettings
            {
                UseSsl = true,
                SslKeyStoreFilePath = "/ssl/server.jks",
                SslTrustStoreFilePath = "/ssl/trust.jks",
                RestartOnSslFileChange = false,
            },
            Logger.None,
            runner,
            () => false,
            () => { }
        )
    );

    // no files: the tests call OnFilesChanged themselves
    private SslFileRestarter CreateRestarter() =>
        new(Settings, Logger.None, runner, [], () => stopping, () => failed = true);
}
