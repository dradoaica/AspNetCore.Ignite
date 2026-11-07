using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using System.Runtime.InteropServices;
using Xunit;

namespace AspNetCore.Ignite.IntegrationTests;

/// <summary>
///     Builds the server image from the repository Dockerfile once, and runs one node (authentication and persistence
///     on, the defaults) for every test of <see cref="IgniteServerCollection" />; scenarios that need other settings
///     start their own nodes from the same image (<see cref="CreateNodeBuilder" />).
/// </summary>
public sealed class IgniteServerFixture : IAsyncLifetime
{
    public const string UserName = "ignite";
    public const string DefaultPassword = "ignite";

    // quotes and XML/SQL special characters, so the password change is tested end to end
    public const string Password = "p'w&d\"<x>";

    public const int ThinClientPort = 10800;
    public const string RunningMessage = "Ignite server runner is running";

    private IFutureDockerImage? image;
    private IContainer? container;

    public bool IsDockerAvailable { get; private set; }

    public string Endpoint => GetEndpoint(container!);

    /// <summary>The sample keystores and certificates of the repository (password 123456).</summary>
    public static string SampleCertificatesDirectory { get; } = Path.Combine(
        CommonDirectoryPath.GetGitDirectory().DirectoryPath,
        "src",
        "AspNetCore.IgniteServer",
        "config"
    );

    private static string BuildPlatform => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "linux/arm64",
        _ => "linux/amd64",
    };

    public static string GetEndpoint(IContainer node) => $"{node.Hostname}:{node.GetMappedPublicPort(ThinClientPort)}";

    /// <summary>A node of the server image with small memory settings and the thin client port published.</summary>
    public ContainerBuilder CreateNodeBuilder() => new ContainerBuilder(image!)
        .WithPortBinding(ThinClientPort, true)
        .WithEnvironment("DEFAULT_ON_HEAP_MEMORY", "512")
        .WithEnvironment("DEFAULT_OFF_HEAP_MEMORY", "256")
        .WithEnvironment("IGNITE_USER_PASSWORD", Password);

    public async Task InitializeAsync()
    {
        IsDockerAvailable = await DockerSupport.IsDockerAvailableAsync();
        if (!IsDockerAvailable)
        {
            return;
        }

        image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), string.Empty)
            .WithDockerfile("Dockerfile")
            // the legacy builder Testcontainers uses does not set BUILDPLATFORM (BuildKit and buildx do)
            .WithBuildArgument("BUILDPLATFORM", BuildPlatform)
            .WithName($"aspnetcore.ignite-server:integration-tests-{Guid.NewGuid():N}")
            .WithCleanUp(true)
            .Build();
        await image.CreateAsync();

        container = CreateNodeBuilder()
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(RunningMessage))
            .Build();
        await container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (container != null)
        {
            await container.DisposeAsync();
        }

        if (image != null)
        {
            // the image is built per run, so do not leave it behind
            await image.DeleteAsync();
            await image.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class IgniteServerCollection : ICollectionFixture<IgniteServerFixture>
{
    public const string Name = "Ignite server";
}
