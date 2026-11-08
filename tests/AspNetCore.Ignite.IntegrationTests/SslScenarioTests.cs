using Apache.Ignite.Core.Client;
using Apache.Ignite.Core.Client.Cache;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace AspNetCore.Ignite.IntegrationTests;

[Collection(IgniteClusterCollection.Name)]
public sealed class SslScenarioTests(IgniteClusterFixture fixture)
{
    private const string CertificatesDirectory = "/certs";
    private const string SamplePassword = "123456";

    // the keystores belong to the server's user (1001), so the tests can "rotate" them as that user
    private const uint ServerUserId = 1001;

    [SkippableFact]
    public async Task ClientCertificate_IsRequired_ServerCertificateIsValidated_AndRotationRestartsTheNode()
    {
        Skip.IfNot(fixture.IsDockerAvailable, "Docker is not available.");
        await using var node = SslNode()
            .WithEnvironment("USE_CLIENT_SSL", "true")
            .WithEnvironment("SSL_CLIENT_CERTIFICATE_PATH", $"{CertificatesDirectory}/thin-client-cert.pfx")
            .WithEnvironment("SSL_CLIENT_CERTIFICATE_PASSWORD", SamplePassword)
            .Build();
        await node.StartAsync();
        var endpoint = IgniteServerFixture.GetEndpoint(node);
        var clientCertificate = Path.Combine(IgniteServerFixture.SampleCertificatesDirectory, "thin-client-cert.pfx");

        // the sample server certificate is issued by a private CA for server.dradoaica.com: rejected by default
        Assert.ThrowsAny<Exception>(() => Connect(endpoint, clientCertificate, false).Dispose());
        // without a client certificate the server rejects the connection
        Assert.ThrowsAny<Exception>(() => Connect(endpoint, null, true).Dispose());
        using (var igniteClient = Connect(endpoint, clientCertificate, true))
        {
            Assert.True(igniteClient.GetCluster().IsActive());
        }

        // the control utility connects with the node's key and trust stores (and credentials)
        var (controlExitCode, controlStdout, controlStderr) = await ExecAsync(node, "control.sh", "--state");
        Assert.True(controlExitCode == 0, controlStdout + controlStderr);

        // rotating a keystore restarts the node (after the 10 s debounce), which then serves clients again
        var (exitCode, _, stderr) = await ExecAsync(node, "touch", $"{CertificatesDirectory}/server.jks");
        Assert.True(exitCode == 0, stderr);
        await TestContainerLogs.WaitForMessageAsync(node, "SSL files changed", 1, TimeSpan.FromSeconds(60));
        await TestContainerLogs.WaitForMessageAsync(
            node,
            IgniteServerFixture.RunningMessage,
            2,
            TimeSpan.FromSeconds(120)
        );
        using (var igniteClient = Connect(endpoint, clientCertificate, true))
        {
            Assert.True(igniteClient.GetCluster().IsActive());
        }
    }

    [SkippableFact]
    public async Task RotationOfANodeThatCannotStopGracefully_RestartsItsContainer()
    {
        Skip.IfNot(fixture.IsDockerAvailable, "Docker is not available.");
        await using var node = SslNode()
            .WithEnvironment("GRACEFUL_STOP_TIMEOUT_IN_SECONDS", "3")
            .WithEnvironment("SHUTDOWN_TIMEOUT_IN_SECONDS", "10")
            .Build();
        await node.StartAsync();
        using (var igniteClient = CacheFactory.ConnectAsClient(
                   IgniteServerFixture.ClientConfiguration(
                       IgniteServerFixture.GetEndpoint(node),
                       IgniteServerFixture.UserName,
                       IgniteServerFixture.Password
                   )
               ))
        {
            // alone, the node waits forever for the backups of this cache before it stops gracefully
            igniteClient.GetOrCreateCache<int, int>(
                    new CacheClientConfiguration("backed-up")
                    {
                        Backups = 1,
                    }
                )
                .Put(1, 1);
        }

        var (touched, _, stderr) = await ExecAsync(node, "touch", $"{CertificatesDirectory}/server.jks");
        Assert.True(touched == 0, stderr);

        await TestContainerLogs.WaitForMessageAsync(
            node,
            "to restart with the renewed SSL files",
            1,
            TimeSpan.FromSeconds(60)
        );

        // the server exits (it waits for the container to exit), and its container restarts with the renewed files
        Assert.Equal(1, await node.GetExitCodeAsync().WaitAsync(TimeSpan.FromSeconds(60)));
    }

    /// <summary>A node with SSL between nodes, from the sample keystores.</summary>
    private ContainerBuilder SslNode()
    {
        const UnixFileModes ownerReadWrite = UnixFileModes.UserRead | UnixFileModes.UserWrite;
        var builder = fixture.CreateNodeBuilder()
            .WithEnvironment("USE_SSL", "true")
            .WithEnvironment("SSL_KEY_STORE_FILE_PATH", $"{CertificatesDirectory}/server.jks")
            .WithEnvironment("SSL_KEY_STORE_PASSWORD", SamplePassword)
            .WithEnvironment("SSL_TRUST_STORE_FILE_PATH", $"{CertificatesDirectory}/trust.jks")
            .WithEnvironment("SSL_TRUST_STORE_PASSWORD", SamplePassword)
            .WithEnvironment("SSL_RESTART_MAX_JITTER_IN_SECONDS", "0")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(IgniteServerFixture.RunningMessage));
        foreach (var file in new[]
                 {
                     "server.jks", "trust.jks", "thin-client-cert.pfx",
                 })
        {
            builder = builder.WithResourceMapping(
                new FileInfo(Path.Combine(IgniteServerFixture.SampleCertificatesDirectory, file)),
                new FileInfo($"{CertificatesDirectory}/{file}"),
                ServerUserId,
                0,
                ownerReadWrite
            );
        }

        return builder;
    }

    private static IIgniteClient Connect(
        string endpoint,
        string? clientCertificate,
        bool skipServerCertificateValidation
    )
    {
        var configuration = CacheFactory.GetIgniteClientConfiguration(
            endpoint,
            IgniteServerFixture.UserName,
            IgniteServerFixture.Password,
            true,
            clientCertificate,
            SamplePassword,
            skipServerCertificateValidation,
            // the private sample CA publishes no revocation lists
            false
        );
        // only the given endpoint (see IgniteServerFixture.ClientConfiguration)
        configuration.EnableClusterDiscovery = false;
        return CacheFactory.ConnectAsClient(configuration);
    }

    private static async Task<(long ExitCode, string Stdout, string Stderr)> ExecAsync(
        IContainer node,
        params string[] command
    )
    {
        var result = await node.ExecAsync(command);
        return (result.ExitCode ?? -1, result.Stdout, result.Stderr);
    }
}
