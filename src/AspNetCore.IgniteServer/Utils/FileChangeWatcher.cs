using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AspNetCore.IgniteServer.Utils;

/// <summary>
///     Watches files and invokes a callback once they have settled: after <c>debounce</c> without further changes, plus
///     a random delay of up to <c>maxJitter</c> so that nodes whose certificates are rotated at the same time do not
///     restart at the same time.
/// </summary>
internal sealed class FileChangeWatcher : IDisposable
{
    private readonly TimeSpan debounce;
    private readonly TimeSpan maxJitter;
    private readonly Action onChanged;
    private readonly List<IDisposable> registrations = [];
    private readonly ITimer timer;
    private int disposed;

    public FileChangeWatcher(
        IEnumerable<string> filePaths,
        Action onChanged,
        TimeSpan debounce,
        TimeSpan maxJitter,
        TimeProvider? timeProvider = null
    )
    {
        this.onChanged = onChanged;
        this.debounce = debounce;
        this.maxJitter = maxJitter;
        timer = (timeProvider ?? TimeProvider.System).CreateTimer(
            _ => OnSettled(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
        foreach (var filePath in filePaths)
        {
            var fullPath = Path.GetFullPath(filePath);
            PhysicalFileProvider physicalFileProvider = new(Path.GetDirectoryName(fullPath)!)
            {
                // polling also sees changes on mounted volumes (e.g., Kubernetes secrets) that raise no inotify events
                UseActivePolling = true,
                UsePollingFileWatcher = true,
            };
            registrations.Add(physicalFileProvider);
            registrations.Add(
                ChangeToken.OnChange(() => physicalFileProvider.Watch(Path.GetFileName(fullPath)), NotifyChanged)
            );
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        timer.Dispose();
        registrations.ForEach(registration => registration.Dispose());
    }

    /// <summary>Records a change; every change restarts the debounce.</summary>
    public void NotifyChanged()
    {
        if (Volatile.Read(ref disposed) == 1)
        {
            return;
        }

        var jitter = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * maxJitter.TotalMilliseconds);
        timer.Change(debounce + jitter, Timeout.InfiniteTimeSpan);
    }

    private void OnSettled()
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            onChanged();
        }
    }
}
