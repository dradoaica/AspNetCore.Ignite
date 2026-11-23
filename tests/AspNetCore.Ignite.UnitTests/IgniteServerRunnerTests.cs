using AspNetCore.IgniteServer;
using Serilog.Core;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class IgniteServerRunnerTests
{
    [Fact]
    public void Constructor_AppliesSettings()
    {
        using IgniteServerRunner runner = new(
            new IgniteServerSettings
            {
                EnableAuthentication = false,
                UseSsl = true,
                SslKeyStoreFilePath = "server.jks",
            },
            Logger.None,
            LocalAddress.Resolve,
            "/tmp/spring-config.xml"
        );

        Assert.Equal("/tmp/spring-config.xml", runner.Configuration.SpringConfigUrl);
        Assert.NotNull(runner.Configuration.SslContextFactory);
        Assert.False(runner.Configuration.AuthenticationEnabled);
        // the host, not the JVM, handles SIGTERM
        Assert.Single(runner.Configuration.JvmOptions, option => option == "-Xrs");
    }
}
