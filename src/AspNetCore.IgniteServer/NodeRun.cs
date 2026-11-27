using Apache.Ignite.Core;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCore.IgniteServer;

/// <summary>
///     One start of the node by <see cref="IgniteServerRunner" />, until the node has stopped: the node, the signals of
///     its start and its stop, and the stop itself. Thread-safe; nothing is called under its lock.
///     <para>
///         The node is stopped once, because Ignite stops a node only once: the first <see cref="BeginStop" /> begins the
///         stop, and later calls get that same stop.
///     </para>
/// </summary>
internal sealed class NodeRun
{
    private readonly Lock gate = new();
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IIgnite? node;
    private Task<bool>? stop;
    private bool stoppedOnItsOwn;

    /// <summary>Completes when Ignition.Start has returned or thrown.</summary>
    public Task Started => started.Task;

    /// <summary>
    ///     Completes when a stop or restart is requested, or the node has stopped: the activation then ends
    ///     (<see cref="ClusterActivator" />).
    /// </summary>
    public Task Stopping => stopping.Task;

    /// <summary>Completes when the node has stopped.</summary>
    public Task Stopped => stopped.Task;

    /// <summary>The started node; null until Ignition.Start has returned it, and when it threw.</summary>
    public IIgnite? Node
    {
        get
        {
            lock (gate)
            {
                return node;
            }
        }
    }

    /// <summary>True when the node stopped without a stop or restart being requested (e.g., segmented).</summary>
    public bool StoppedOnItsOwn
    {
        get
        {
            lock (gate)
            {
                return stoppedOnItsOwn;
            }
        }
    }

    /// <summary>Ignition.Start has returned <paramref name="startedNode" />, or thrown (null).</summary>
    public void OnStarted(IIgnite? startedNode)
    {
        lock (gate)
        {
            node = startedNode;
        }

        started.TrySetResult();
    }

    /// <summary>A stop or restart is requested: the activation in progress ends.</summary>
    public void RequestStop() => stopping.TrySetResult();

    /// <summary>The node has stopped; <paramref name="onItsOwn" /> when nobody requested it.</summary>
    public void OnStopped(bool onItsOwn)
    {
        lock (gate)
        {
            stoppedOnItsOwn = onItsOwn;
        }

        stopping.TrySetResult();
        stopped.TrySetResult();
    }

    /// <summary>
    ///     Begins <paramref name="stopNode" /> on a thread pool thread, unless a stop began already, and returns the stop
    ///     in progress. Null while the node is still starting, and when its start failed.
    /// </summary>
    public Task<bool>? BeginStop(Func<IIgnite, bool> stopNode) => BeginStop(stopNode, out _);

    /// <summary>Like <see cref="BeginStop" />, but null when a stop began already.</summary>
    public Task<bool>? BeginStopIfNoneBegan(Func<IIgnite, bool> stopNode) =>
        BeginStop(stopNode, out var began) is { } begun && began ? begun : null;

    /// <summary>
    ///     Waits for the node to start, then stops it with <paramref name="stopNode" /> (or joins the stop in progress),
    ///     all within <paramref name="timeout" />. <paramref name="stopNode" /> gets the time that is left. Returns false
    ///     when the node did not stop in time.
    /// </summary>
    public bool StopWithin(TimeSpan timeout, Func<IIgnite, TimeSpan, bool> stopNode)
    {
        var elapsed = Stopwatch.StartNew();
        if (!Started.Wait(timeout))
        {
            return false;
        }

        var stopInProgress = BeginStop(startedNode => stopNode(startedNode, Remaining(timeout, elapsed)));
        // No node: its start failed, so there is nothing to stop.
        return stopInProgress?.Wait(Remaining(timeout, elapsed)) ?? true;
    }

    private Task<bool>? BeginStop(Func<IIgnite, bool> stopNode, out bool began)
    {
        lock (gate)
        {
            began = false;
            if (node is not { } startedNode)
            {
                return null;
            }

            if (stop == null)
            {
                began = true;
                stop = stopped.Task.IsCompleted ? Task.FromResult(true) : Task.Run(() => stopNode(startedNode));
            }

            return stop;
        }
    }

    private static TimeSpan Remaining(TimeSpan timeout, Stopwatch elapsed) =>
        timeout > elapsed.Elapsed ? timeout - elapsed.Elapsed : TimeSpan.Zero;
}
