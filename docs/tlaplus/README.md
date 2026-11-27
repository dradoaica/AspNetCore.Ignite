# TLA+ specifications

| Folder                                     | What it models                                                                                | Code                                                              |
|--------------------------------------------|-----------------------------------------------------------------------------------------------|-------------------------------------------------------------------|
| [`ServerLifecycle/`](ServerLifecycle/)     | Starts, restarts and stops of the server node, and the activation and deactivation of its cluster | `IgniteServerRunner.cs`, `NodeRun.cs`, `ClusterActivator.cs` (`src/AspNetCore.IgniteServer`) |

The spec models the code as implemented; it is not part of the build, and passing it is not a release gate. A change to
how the runner starts, restarts or stops the node updates the spec too (CONTRIBUTING.md, Node lifecycle).

Running (TLC from `tla2tools.jar` of the [v1.8.0 release](https://github.com/tlaplus/tlaplus/releases/tag/v1.8.0),
a pre-release that is rebuilt: results below from TLC2 2026.11.27.201609, rev `bfe0877`, `tla2tools.jar` SHA-256
`18e0d9caa517a6553ef50c7105191b1bfcfe5cc630bdbceefc087093e0520ce3`, Java 21, 16 workers, 2026-11-27):

```
cd docs/tlaplus/ServerLifecycle
java -XX:+UseParallelGC -cp tla2tools.jar tlc2.TLC -workers auto -config MCSafety.cfg ServerLifecycle.tla
```

TLC writes its state directory (`states/`) and the trace of a violation (`*_TTrace_*`) next to the spec; both are
ignored by git.

## ServerLifecycle

Each action of `ServerLifecycle.tla` is one `lock (gate)` block of the runner, one call of `NodeRun`, or one call into
Ignite, and names the code it stands for:

| Spec                                                              | Code                                                                                                   |
|-------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------|
| `RunCall`, `RunBegin`, `AfterRunOnce`, `RunFinally`               | `IgniteServerRunner.Run` and the first lock of `RunOnce`                                               |
| `JvmUp`, `StartReturns`, `StartFails`                             | `launcher.Start` and the lock after it (`NodeRun.OnStarted`)                                           |
| `RunBeginsStop`                                                   | `RunOnce` when a stop came during the start (`NodeRun.BeginStop`)                                      |
| `TakeReactivate`, `ActivateEnter`, `ActivateExit`, `Configure`    | `deactivatedToRestart`, `ClusterActivator.ActivateAsync` (under `stateLock`, then baseline and password) |
| `NotActivated`, `Listen`, `ToRunning`, `WaitStopped`              | the rest of `RunOnce`: the event listeners, `Running`, the wait for `NodeRun.Stopped`                  |
| `StopCall`, `StopBegins`, `StopEnds`, `StopGivesUp`               | `IgniteServerRunner.Stop` (`RequestStopUnderGate`, `NodeRun.StopWithin`)                               |
| `RestartCall`, `RestartBegins`, `RestartTaskEnds`, `RestartOk`, `RestartLate`, `RestartGivesUp` | `IgniteServerRunner.Restart`                                               |
| `DisposeCall`, `DisposeBegins`                                    | `IgniteServerRunner.Dispose` (`NodeRun.BeginStopIfNoneBegan`)                                          |
| `TaskDeactEnter`, `TaskDeactGivesUp`, `TaskDeactivate`, `TaskFlag`, `TaskStop`, `TaskStopReturns` | the stop task: `IgniteServerRunner.StopGracefully` (`ClusterActivator.DeactivateIfLastServerNode`, `launcher.Stop`) |
| `NodeStopsOnItsOwn`, `AfterNodeStop`                              | Ignite: a node that stops on its own; `NodeStopHandler` and `OnNodeStopped`                           |

Abstractions:

- Time is not modelled. A timeout is a `GiveUp` action, possible at any point of its wait (`Timeouts = TRUE`).
- `NodeRun.StopWithin` waits for the start and begins the stop in one step; both only read and set `NodeRun`'s own
  state.
- `LastServer = TRUE` stands for a cluster of this node alone: the GRACEFUL shutdown policy holds the node of an active
  cluster (`Blocked`), the cluster ends with the node, and a persistent one may activate itself when the node starts
  (`JvmUp`). With other server nodes (`LastServer = FALSE`), the stop is never held, and the cluster stays as it is.
- The baseline, the password, the rebalancing and `WaitForServerNodes` are not modelled: the activation may take any
  time (stuttering).
- Overlapping `SslFileRestarter` callbacks are two Restart callers (`Restarters`). The host calls `Dispose` after
  `StopAsync`; with timeouts, `StopAsync` may have given up while `Stop` goes on.

| Config                     | Checks                                                                                                    | Result                           |
|----------------------------|-----------------------------------------------------------------------------------------------------------|----------------------------------|
| `MCSafety.cfg`             | the invariants below, two restarters, timeouts, one own stop, other server nodes                          | holds (2,893,136 distinct states) |
| `MCSafetyLastServer.cfg`   | the same, for the cluster's only server node                                                               | holds (8,034,620 distinct states) |
| `MCLiveness.cfg`           | `StopEndsTheRun`, `CallsReturn` without timeouts                                                          | holds (157,524 distinct states)   |
| `MCLivenessLastServer.cfg` | the same, for the cluster's only server node                                                              | holds (443,910 distinct states)   |

Invariants: `AtMostOneNode` (two nodes never run at once), `StopWins` (no start after a requested stop),
`OneStopPerNode` (`launcher.Stop` once per start), `PhaseMatchesNode` (`Activating` or `Running` only while the node is
up), `NoBadTransition` (`IgniteServerRunner.AllowedTransitions`), `CallbackForCurrentRun` (`OnNodeStopped` concerns the
current run), `NoActivationUndoesDeactivation`, `RunResultHonest` (what `Run` returns) and `RunThrowsOnlyOnFailure`.
Liveness, with fair threads and Ignite delivering `AfterNodeStop`: once `Stop` is called, `Run` returns
(`StopEndsTheRun`), and so do `Stop` and every `Restart` that was called (`CallsReturn`).

Mutation checks: each property catches the bug it is there for (each a one-line change of the spec):

| Mutation                                                                                        | Config                     | Caught by                                  |
|-------------------------------------------------------------------------------------------------|----------------------------|--------------------------------------------|
| A node that stops on its own leaves the phase as it is (the code before `006d5c8`)              | `MCSafety.cfg`             | `PhaseMatchesNode` (7-state trace)         |
| `NodeRun.BeginStop` begins a stop on every call                                                 | `MCSafety.cfg`             | `OneStopPerNode` (12)                      |
| `RunOnce` starts a node after a stop was requested                                              | `MCSafety.cfg`             | `StopWins` (4)                             |
| The activation does not check whether the node is being stopped                                 | `MCSafetyLastServer.cfg`   | `NoActivationUndoesDeactivation` (12)      |
| `RunOnce` returns without waiting for its node to stop                                          | `MCSafety.cfg`             | `AtMostOneNode` (16, checked alone)        |
| The node's `Stopped` event, subscribed after `Start` returns, misses a stop during the start (the code before `17f3ffc`) | `MCLiveness.cfg` | `StopEndsTheRun` (19)            |
| The last server node stops without deactivating its cluster                                     | `MCLivenessLastServer.cfg` | `StopEndsTheRun`, `CallsReturn` (13)       |
| Nothing catches the listeners' failure on a node stopped or restarted meanwhile (finding 1)     | `MCSafety.cfg`             | `RunThrowsOnlyOnFailure` (14)              |

### Findings

1. **A restart right after the activation failed the server.** After `ActivateAsync` returned, `RunOnce` registered the
   event listeners and logged the running node outside any `catch`. A restart (renewed SSL files) or a stop in that
   window stops the node, these calls to it fail, and `Run` threw: the hosted service reported a restart as a failure
   and stopped the server with exit code `1` (a stop was only logged as an error). TLC found it as a 14-state trace of
   `RunThrowsOnlyOnFailure`. Fixed as in the activation: the exception is caught when the node is being stopped, and
   the run waits for the node to stop; `IgniteServerLifecycleTests.RestartBetweenTheActivationAndTheListeners_RestartsTheNode`
   fails without the fix.
