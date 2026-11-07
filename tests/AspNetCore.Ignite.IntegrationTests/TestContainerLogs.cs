using System.Text.RegularExpressions;
using DotNet.Testcontainers.Containers;

namespace AspNetCore.Ignite.IntegrationTests;

internal static class TestContainerLogs
{
    /// <summary>Waits until <paramref name="message" /> was logged at least <paramref name="count" /> times.</summary>
    public static async Task WaitForMessageAsync(IContainer container, string message, int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var (stdout, stderr) = await container.GetLogsAsync();
            if (Regex.Count(stdout + stderr, Regex.Escape(message)) >= count) return;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"'{message}' was not logged {count} time(s) within {timeout}:\n{stdout}");

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }
}