using System.Runtime.InteropServices;
using Apache.Ignite.Core.Client;
using AspNetCore.Ignite.Options;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Xunit;

namespace AspNetCore.Ignite.IntegrationTests;

/// <summary>
///     The server image for the nodes of a test collection: a prebuilt one (e.g., built once by CI), or the repository
///     Dockerfile, built once for every collection (Testcontainers removes it after the run).
/// </summary>
public abstract class IgniteNodesFixture : IAsyncLifetime
{
    private static readonly Lazy<Task<IImage?>> ServerImage = new(LoadImageAsync);

    private IImage? image;

    public bool IsDockerAvailable => image != null;

    private static string BuildPlatform => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "linux/arm64",
        _ => "linux/amd64"
    };

    /// <summary>A prebuilt server image to test (e.g., built once by CI); otherwise the Dockerfile is built.</summary>
    public static string? PrebuiltImage => Environment.GetEnvironmentVariable("IGNITE_SERVER_IMAGE") is
        { Length: > 0 } name
        ? name
        : null;

    public virtual async Task InitializeAsync()
    {
        image = await ServerImage.Value;
    }

    public virtual Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>A node of the server image with small memory settings and the thin client port published.</summary>
    public ContainerBuilder CreateNodeBuilder()
    {
        return new ContainerBuilder(image!)
            .WithPortBinding(IgniteServerFixture.ThinClientPort, true)
            .WithPortBinding(IgniteServerFixture.ManagementPort, true)
            .WithEnvironment("DEFAULT_ON_HEAP_MEMORY", "512")
            .WithEnvironment("DEFAULT_OFF_HEAP_MEMORY", "256")
            .WithEnvironment("IGNITE_USER_PASSWORD", IgniteServerFixture.Password);
    }

    /// <summary>The image, or null without Docker.</summary>
    private static async Task<IImage?> LoadImageAsync()
    {
        if (!await DockerSupport.IsDockerAvailableAsync()) return null;

        if (PrebuiltImage is { } prebuiltImage) return new DockerImage(prebuiltImage);

        var built = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), string.Empty)
            .WithDockerfile("Dockerfile")
            // the legacy builder Testcontainers uses does not set BUILDPLATFORM (BuildKit and buildx do)
            .WithBuildArgument("BUILDPLATFORM", BuildPlatform)
            .WithName($"aspnetcore.ignite-server:integration-tests-{Guid.NewGuid():N}")
            .WithCleanUp(true)
            .Build();
        await built.CreateAsync();
        return built;
    }
}

/// <summary>
///     Runs one node (authentication and persistence on, the defaults) for every test of
///     <see cref="IgniteServerCollection" />; scenarios that need other settings start their own nodes from the same
///     image (<see cref="IgniteNodesFixture.CreateNodeBuilder" />).
/// </summary>
public sealed class IgniteServerFixture : IgniteNodesFixture
{
    public const string UserName = "ignite";
    public const string DefaultPassword = "ignite";

    // quotes and XML/SQL special characters, so the password change is tested end to end
    public const string Password = "p'w&d\"<x>";

    public const int ThinClientPort = 10800;
    public const int ManagementPort = 9464;
    public const int RestPort = 8080;
    public const string RunningMessage = "Ignite server runner is running";
    private IContainer? container;

    public string Endpoint => GetEndpoint(container!);

    /// <summary>The sample keystores and certificates of the repository (password 123456).</summary>
    public static string SampleCertificatesDirectory { get; } = Path.Combine(
        CommonDirectoryPath.GetGitDirectory().DirectoryPath,
        "src",
        "AspNetCore.IgniteServer",
        "config"
    );

    public IContainer Node => container!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        if (!IsDockerAvailable) return;

        container = CreateNodeBuilder()
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(RunningMessage))
            .Build();
        await container.StartAsync();
    }

    public override async Task DisposeAsync()
    {
        if (container != null) await container.DisposeAsync();
    }

    public static string GetEndpoint(IContainer node)
    {
        return $"{node.Hostname}:{node.GetMappedPublicPort(ThinClientPort)}";
    }

    /// <summary>
    ///     A thin client configuration that uses only <paramref name="endpoint" />: the container addresses it would
    ///     discover are not reachable from every Docker host (e.g., Docker Desktop), and waiting for them slows the tests.
    /// </summary>
    public static IgniteClientConfiguration ClientConfiguration(string endpoint, string? userName = null,
        string? password = null)
    {
        return CacheFactory.GetIgniteClientConfiguration(
            new IgniteClientOptions
            {
                Endpoints = [endpoint], UserName = userName, Password = password, EnableClusterDiscovery = false
            }
        );
    }

    public static Uri GetManagementUri(IContainer node, string path)
    {
        return new Uri($"http://{node.Hostname}:{node.GetMappedPublicPort(ManagementPort)}{path}");
    }
}

/// <summary>For the tests that start clusters of their own: they run beside those of the shared node.</summary>
public sealed class IgniteClusterFixture : IgniteNodesFixture;

[CollectionDefinition(Name)]
public sealed class IgniteServerCollection : ICollectionFixture<IgniteServerFixture>
{
    public const string Name = "Ignite server";
}

[CollectionDefinition(Name)]
public sealed class IgniteClusterCollection : ICollectionFixture<IgniteClusterFixture>
{
    public const string Name = "Ignite clusters";
}
