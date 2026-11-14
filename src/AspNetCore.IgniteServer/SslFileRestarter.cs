using AspNetCore.IgniteServer.Utils;
using Serilog;
using System;
using System.Collections.Generic;

namespace AspNetCore.IgniteServer;

/// <summary>
///     Restarts the node when its SSL files change (certificate rotation), so that it uses the renewed files; with a
///     debounce and a random delay, so that nodes whose certificates are renewed together do not restart together.
/// </summary>
internal sealed class SslFileRestarter : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(10);

    private readonly Action fail;
    private readonly Func<bool> isStopping;
    private readonly ILogger logger;
    private readonly IgniteServerRunner runner;
    private readonly IgniteServerSettings settings;
    private readonly FileChangeWatcher? watcher;

    /// <param name="settings">The server settings.</param>
    /// <param name="logger">Where the restarts are logged.</param>
    /// <param name="runner">The runner of the node to restart.</param>
    /// <param name="files">The files to watch; with none, only <see cref="OnFilesChanged" /> restarts the node.</param>
    /// <param name="isStopping">Whether the host is stopping: the node is then not restarted.</param>
    /// <param name="fail">Stops the server as failed, when the node cannot be restarted.</param>
    internal SslFileRestarter(
        IgniteServerSettings settings,
        ILogger logger,
        IgniteServerRunner runner,
        IReadOnlyCollection<string> files,
        Func<bool> isStopping,
        Action fail
    )
    {
        this.settings = settings;
        this.logger = logger;
        this.runner = runner;
        this.isStopping = isStopping;
        this.fail = fail;
        if (files.Count > 0)
        {
            watcher = new FileChangeWatcher(
                files,
                OnFilesChanged,
                Debounce,
                TimeSpan.FromSeconds(settings.SslRestartMaxJitterInSeconds)
            );
        }
    }

    public void Dispose() => watcher?.Dispose();

    /// <summary>Watches the SSL files of the settings; null when there are none, or RESTART_ON_SSL_FILE_CHANGE is off.</summary>
    public static SslFileRestarter? Create(
        IgniteServerSettings settings,
        ILogger logger,
        IgniteServerRunner runner,
        Func<bool> isStopping,
        Action fail
    )
    {
        var files = GetSslFiles(settings);
        if (files.Count == 0)
        {
            return null;
        }

        if (!settings.RestartOnSslFileChange)
        {
            logger.Information(
                "RESTART_ON_SSL_FILE_CHANGE is disabled: renewed SSL files are used after the next restart only."
            );
            return null;
        }

        return new SslFileRestarter(settings, logger, runner, files, isStopping, fail);
    }

    internal static List<string> GetSslFiles(IgniteServerSettings settings)
    {
        List<string> files = [];
        if (settings.UseSsl || settings.UseClientSsl)
        {
            files.Add(settings.SslKeyStoreFilePath!);
            files.Add(settings.SslTrustStoreFilePath!);
        }

        if (settings.UseClientSsl)
        {
            files.Add(settings.SslClientCertificatePath!);
        }

        return files;
    }

    internal void OnFilesChanged()
    {
        if (isStopping())
        {
            return;
        }

        if (runner.Node == null)
        {
            RestartOnceStarted();
            return;
        }

        logger.Information("SSL files changed, restarting the Ignite node...");
        var timeout = TimeSpan.FromSeconds(settings.GracefulStopTimeoutInSeconds);
        switch (runner.Restart(timeout))
        {
            case IgniteServerRunner.RestartResult.NotRunning:
                // The node stopped, or began to, since the check above.
                RestartOnceStarted();
                break;
            case IgniteServerRunner.RestartResult.TimedOut:
                // The graceful stop can take long (e.g., while another node restarts too). The server exits instead,
                // and its container restarts with the renewed files.
                logger.Error(
                    "The Ignite node did not stop gracefully within {Timeout} to restart with the renewed SSL files; " +
                    "the server stops so that its container restarts with them.",
                    timeout
                );
                fail();
                break;
        }
    }

    /// <summary>
    ///     The node is starting and may already have read the previous files. Asking the watcher again makes
    ///     <see cref="OnFilesChanged" /> run once more after the usual delay, when the node can be restarted.
    /// </summary>
    private void RestartOnceStarted()
    {
        // e.g., Restart found no node because the host stopped it in the meantime
        if (isStopping())
        {
            return;
        }

        logger.Information("SSL files changed while the Ignite node starts: it restarts once started.");
        watcher?.NotifyChanged();
    }
}
