using System.Threading;

namespace AspNetCore.IgniteServer;

/// <summary>
///     The exit code that <c>Program</c> returns, set by the hosted service. Only the first cause counts: a failure
///     stops the server, and the stop that follows must not replace its code with that of a stop that is not graceful.
/// </summary>
internal sealed class ServerExit
{
    /// <summary>Invalid arguments or settings, a failed start, or a node that stopped unexpectedly.</summary>
    public const int Failure = 1;

    /// <summary>
    ///     The server stopped, but the node did not stop gracefully within GRACEFUL_STOP_TIMEOUT_IN_SECONDS, so it ended
    ///     with the process. A persistent node recovers from its WAL on the next start.
    /// </summary>
    public const int NotGraceful = 2;

    private int code;

    public int Code => Volatile.Read(ref code);

    /// <summary>Sets the exit code, unless an earlier cause set one.</summary>
    public void SetIfUnset(int exitCode) => Interlocked.CompareExchange(ref code, exitCode, 0);
}
