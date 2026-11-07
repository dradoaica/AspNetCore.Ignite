using DotNet.Testcontainers.Configurations;

namespace AspNetCore.Ignite.IntegrationTests;

internal static class DockerSupport
{
    /// <summary>True if Docker is configured and its daemon answers a ping.</summary>
    public static async Task<bool> IsDockerAvailableAsync()
    {
        try
        {
            var dockerEndpointAuthConfig = TestcontainersSettings.OS.DockerEndpointAuthConfig;
            if (dockerEndpointAuthConfig == null) return false;

            using var dockerClient = dockerEndpointAuthConfig.GetDockerClientBuilder(Guid.NewGuid()).Build();
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
            await dockerClient.System.PingAsync(cts.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}