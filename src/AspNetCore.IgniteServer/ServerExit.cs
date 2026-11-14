using System.Threading;

namespace AspNetCore.IgniteServer;

/// <summary>
///     The exit code of the server, for <c>Program</c> to return: set by the hosted service, the first cause only (a
///     failure stops the server, and that stop is then not graceful either).
/// </summary>
internal sealed class ServerExit
{
    /// <summary>Invalid arguments or settings, a failed start, or a node that stopped unexpectedly.</summary>
    public const int Failure = 1;

    /// <summary>
    ///     Stopped, but the node not gracefully (within GRACEFUL_STOP_TIMEOUT_IN_SECONDS): it stopped with the process,
    ///     and a persistent one recovers from its WAL on the next start.
    /// </summary>
    public const int NotGraceful = 2;

    private int code;

    public int Code => Volatile.Read(ref code);

    /// <summary>Sets the exit code, unless an earlier cause set one.</summary>
    public void SetIfUnset(int exitCode) => Interlocked.CompareExchange(ref code, exitCode, 0);
}
