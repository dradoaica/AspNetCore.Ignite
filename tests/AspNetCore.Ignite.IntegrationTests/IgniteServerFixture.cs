using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Xunit;

namespace AspNetCore.Ignite.IntegrationTests;

/// <summary>
///     Builds the server image from the repository Dockerfile and runs one node (authentication and persistence on, the
///     defaults) for every test of <see cref="IgniteServerCollection" />.
/// </summary>
public sealed class IgniteServerFixture : IAsyncLifetime
{
    public const string UserName = "ignite";
    public const string DefaultPassword = "ignite";

    // quotes and XML/SQL special characters, so the password change is tested end to end
    public const string Password = "p'w&d\"<x>";

    private const int ThinClientPort = 10800;

    private IFutureDockerImage? image;
    private IContainer? container;

    public bool IsDockerAvailable { get; private set; }

    public string Endpoint => $"{container!.Hostname}:{container.GetMappedPublicPort(ThinClientPort)}";

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
            .WithName($"aspnetcore.ignite-server:integration-tests-{Guid.NewGuid():N}")
            .WithCleanUp(true)
            .Build();
        await image.CreateAsync();

        container = new ContainerBuilder(image)
            .WithPortBinding(ThinClientPort, true)
            .WithEnvironment("IGNITE_USER_PASSWORD", Password)
            .WithEnvironment("DEFAULT_ON_HEAP_MEMORY", "512")
            .WithEnvironment("DEFAULT_OFF_HEAP_MEMORY", "256")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ignite server runner is running"))
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
