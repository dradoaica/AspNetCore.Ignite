using Apache.Ignite.Core.Client;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace AspNetCore.Ignite.IntegrationTests;

[Collection(IgniteServerCollection.Name)]
public sealed class SslScenarioTests(IgniteServerFixture fixture)
{
    private const string CertificatesDirectory = "/certs";
    private const string SamplePassword = "123456";

    [SkippableFact]
    public async Task ClientCertificate_IsRequired_ServerCertificateIsValidated_AndRotationRestartsTheNode()
    {
        Skip.IfNot(fixture.IsDockerAvailable, "Docker is not available.");
        // the keystores belong to the server's user (1001), so the test can "rotate" them as that user
        const uint serverUserId = 1001;
        const UnixFileModes ownerReadWrite = UnixFileModes.UserRead | UnixFileModes.UserWrite;
        var builder = fixture.CreateNodeBuilder()
            .WithEnvironment("USE_SSL", "true")
            .WithEnvironment("USE_CLIENT_SSL", "true")
            .WithEnvironment("SSL_KEY_STORE_FILE_PATH", $"{CertificatesDirectory}/server.jks")
            .WithEnvironment("SSL_KEY_STORE_PASSWORD", SamplePassword)
            .WithEnvironment("SSL_TRUST_STORE_FILE_PATH", $"{CertificatesDirectory}/trust.jks")
            .WithEnvironment("SSL_TRUST_STORE_PASSWORD", SamplePassword)
            .WithEnvironment("SSL_CLIENT_CERTIFICATE_PATH", $"{CertificatesDirectory}/thin-client-cert.pfx")
            .WithEnvironment("SSL_CLIENT_CERTIFICATE_PASSWORD", SamplePassword)
            .WithEnvironment("SSL_RESTART_MAX_JITTER_IN_SECONDS", "0")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(IgniteServerFixture.RunningMessage));
        foreach (var file in new[] { "server.jks", "trust.jks", "thin-client-cert.pfx" })
            builder = builder.WithResourceMapping(
                new FileInfo(Path.Combine(IgniteServerFixture.SampleCertificatesDirectory, file)),
                new FileInfo($"{CertificatesDirectory}/{file}"),
                serverUserId,
                0,
                ownerReadWrite
            );

        await using var node = builder.Build();
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

        // rotating a keystore restarts the node (after the 10 s debounce), which then serves clients again
        var (exitCode, _, stderr) = await ExecAsync(node, "touch", $"{CertificatesDirectory}/server.jks");
        Assert.True(exitCode == 0, stderr);
        await TestContainerLogs.WaitForMessageAsync(node, "SSL files changed", 1, TimeSpan.FromSeconds(60));
        await TestContainerLogs.WaitForMessageAsync(node, IgniteServerFixture.RunningMessage, 2,
            TimeSpan.FromSeconds(120));
        using (var igniteClient = Connect(endpoint, clientCertificate, true))
        {
            Assert.True(igniteClient.GetCluster().IsActive());
        }
    }

    private static IIgniteClient Connect(string endpoint, string? clientCertificate,
        bool skipServerCertificateValidation)
    {
        return CacheFactory.ConnectAsClient(
            CacheFactory.GetIgniteClientConfiguration(
                endpoint,
                IgniteServerFixture.UserName,
                IgniteServerFixture.Password,
                true,
                clientCertificate,
                SamplePassword,
                skipServerCertificateValidation,
                // the private sample CA publishes no revocation lists
                false
            )
        );
    }

    private static async Task<(long ExitCode, string Stdout, string Stderr)> ExecAsync(IContainer node,
        params string[] command)
    {
        var result = await node.ExecAsync(command);
        return (result.ExitCode ?? -1, result.Stdout, result.Stderr);
    }
}