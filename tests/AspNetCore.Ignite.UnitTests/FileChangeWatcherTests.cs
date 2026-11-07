using AspNetCore.IgniteServer.Utils;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class FileChangeWatcherTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(10);

    [Fact]
    public void Changes_AreDebounced()
    {
        FakeTimeProvider time = new();
        var invocations = 0;
        using FileChangeWatcher watcher = new([], () => invocations++, Debounce, TimeSpan.Zero, time, watchFiles: false);

        watcher.NotifyChanged();
        time.Advance(TimeSpan.FromSeconds(9));
        watcher.NotifyChanged();
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(0, invocations);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, invocations);

        // later changes trigger again (the watcher is not one-shot)
        watcher.NotifyChanged();
        time.Advance(Debounce);
        Assert.Equal(2, invocations);
    }

    [Fact]
    public void Jitter_IsAddedWithinTheMaximum()
    {
        FakeTimeProvider time = new();
        var invocations = 0;
        var maxJitter = TimeSpan.FromSeconds(60);
        using FileChangeWatcher watcher = new([], () => invocations++, Debounce, maxJitter, time, new Random(42), false);

        watcher.NotifyChanged();
        var delay = watcher.PendingDelay!.Value;

        Assert.InRange(delay, Debounce, Debounce + maxJitter);
        time.Advance(delay - TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, invocations);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, invocations);
    }

    [Fact]
    public void Dispose_StopsPendingNotifications()
    {
        FakeTimeProvider time = new();
        var invocations = 0;
        FileChangeWatcher watcher = new([], () => invocations++, Debounce, TimeSpan.Zero, time, watchFiles: false);

        watcher.NotifyChanged();
        watcher.Dispose();
        time.Advance(Debounce);

        Assert.Equal(0, invocations);
    }

    [Fact]
    public async Task WatchedFile_ChangesAreSeen()
    {
        var directory = Directory.CreateTempSubdirectory("file-change-watcher-tests");
        try
        {
            var file = Path.Combine(directory.FullName, "server.jks");
            await File.WriteAllTextAsync(file, "1");
            TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using FileChangeWatcher watcher = new([file,], () => changed.TrySetResult(), TimeSpan.Zero, TimeSpan.Zero);

            await Task.Delay(TimeSpan.FromSeconds(1));
            await File.WriteAllTextAsync(file, "2");

            // the polling watcher checks every 4 seconds
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    /// <summary>Kubernetes updates a mounted secret by swapping the ..data symlink; the file symlinks never change.</summary>
    [Fact]
    public async Task KubernetesSecretUpdate_IsSeen()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("k8s-secret");
        try
        {
            var first = Directory.CreateDirectory(Path.Combine(directory.FullName, "..2026_01"));
            await File.WriteAllTextAsync(Path.Combine(first.FullName, "server.jks"), "1");
            File.CreateSymbolicLink(Path.Combine(directory.FullName, "..data"), first.Name);
            File.CreateSymbolicLink(Path.Combine(directory.FullName, "server.jks"), Path.Combine("..data", "server.jks"));
            TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using FileChangeWatcher watcher = new(
                [Path.Combine(directory.FullName, "server.jks"),],
                () => changed.TrySetResult(),
                TimeSpan.Zero,
                TimeSpan.Zero
            );
            await Task.Delay(TimeSpan.FromSeconds(1));

            var second = Directory.CreateDirectory(Path.Combine(directory.FullName, "..2026_02"));
            await File.WriteAllTextAsync(Path.Combine(second.FullName, "server.jks"), "2");
            File.CreateSymbolicLink(Path.Combine(directory.FullName, "..data_tmp"), second.Name);
            // an atomic rename(2) of the new symlink over ..data, as the kubelet does
            using (var mv = System.Diagnostics.Process.Start("mv", ["-Tf", Path.Combine(directory.FullName, "..data_tmp"), Path.Combine(directory.FullName, "..data"),])!)
            {
                await mv.WaitForExitAsync();
                Assert.Equal(0, mv.ExitCode);
            }

            await changed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
