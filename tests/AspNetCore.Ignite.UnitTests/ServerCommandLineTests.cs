using AspNetCore.IgniteServer;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class ServerCommandLineTests
{
    private static async Task<(int ExitCode, IReadOnlyDictionary<string, string?>? Overrides)> InvokeAsync(
        params string[] args)
    {
        IReadOnlyDictionary<string, string?>? overrides = null;
        ServerCommandLine commandLine = new(values =>
            {
                overrides = values;
                return Task.FromResult(0);
            }
        );
        var exitCode = await commandLine.InvokeAsync(args);
        return (exitCode, overrides);
    }

    [Fact]
    public async Task Options_OverrideTheirSettings()
    {
        var (exitCode, overrides) = await InvokeAsync(
            "-OnHeap", "512", "-OffHeap", "256", "-SpiPort", "47600", "-ConfigFile", "config/persistent-config.xml",
            "-ConsistentId", "node-1", "-PersistenceEnabled", "-Cluster", "a:47500", "-Cluster", "b:47500"
        );

        Assert.Equal(0, exitCode);
        Assert.Equal(
            new Dictionary<string, string?>
            {
                ["DEFAULT_ON_HEAP_MEMORY"] = "512",
                ["DEFAULT_OFF_HEAP_MEMORY"] = "256",
                ["SPI_PORT"] = "47600",
                ["CONFIG_FILE"] = "config/persistent-config.xml",
                ["DEFAULT_CONSISTENT_ID"] = "node-1",
                ["PERSISTENCE_ENABLED"] = "true",
                ["CLUSTER_ENDPOINTS:0"] = "a:47500",
                ["CLUSTER_ENDPOINTS:1"] = "b:47500"
            },
            overrides
        );
    }

    [Fact]
    public async Task NoOptions_NoOverrides()
    {
        var (exitCode, overrides) = await InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.Empty(overrides!);
    }

    [Theory]
    [InlineData("-Bogus")]
    [InlineData("-OnHeap", "lots")]
    [InlineData("-onheap", "512")]
    public async Task InvalidArguments_FailWithoutRunning(params string[] args)
    {
        var (exitCode, overrides) = await InvokeAsync(args);

        Assert.Equal(1, exitCode);
        Assert.Null(overrides);
    }

    [Theory]
    [InlineData("-Help")]
    [InlineData("-?")]
    [InlineData("--help")]
    public async Task Help_DoesNotRun(string option)
    {
        var (exitCode, overrides) = await InvokeAsync(option);

        Assert.Equal(0, exitCode);
        Assert.Null(overrides);
    }
}