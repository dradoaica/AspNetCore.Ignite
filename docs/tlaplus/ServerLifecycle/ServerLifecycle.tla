--------------------------- MODULE ServerLifecycle ---------------------------
(***************************************************************************)
(* The node lifecycle of the server, as implemented by IgniteServerRunner,  *)
(* NodeRun and ClusterActivator (src/AspNetCore.IgniteServer).              *)
(*                                                                         *)
(* Threads: the Run thread, the Stop caller (the hosted service's          *)
(* StopAsync), Restart callers (SslFileRestarter, whose timer callbacks    *)
(* can overlap), the Dispose caller, one stop task per start of the node   *)
(* (NodeRun.BeginStop), and Ignite (the node stopping on its own, and the  *)
(* AfterNodeStop callback). Each action is one lock(gate) block of the     *)
(* runner, one NodeRun call, or one call into Ignite; docs/tlaplus/README  *)
(* maps the actions to the code.                                           *)
(*                                                                         *)
(* Time is not modelled: a caller's timeout is GiveUp, which can happen at *)
(* any point of its wait.                                                  *)
(***************************************************************************)
EXTENDS Integers, FiniteSets

CONSTANTS
    Restarters,     \* the Restart callers (SslFileRestarter callbacks)
    LastServer,     \* TRUE: the node is its cluster's only server node
    MaxEnvEvents,   \* how many times a node may stop on its own
    Timeouts        \* FALSE: no caller's timeout runs out (and the host waits for StopAsync before Dispose)

MaxRuns == 1 + Cardinality(Restarters)
Runs == 1..MaxRuns
Phases == {"Idle", "Starting", "Activating", "Running", "Restarting", "Stopping", "Stopped"}

\* IgniteServerRunner.AllowedTransitions
Allowed == [Idle |-> {"Starting", "Stopping"},
            Starting |-> {"Activating", "Stopping", "Stopped"},
            Activating |-> {"Running", "Restarting", "Stopping", "Stopped"},
            Running |-> {"Restarting", "Stopping", "Stopped"},
            Restarting |-> {"Idle", "Stopping", "Stopped"},
            Stopping |-> {"Stopped"},
            Stopped |-> {}]

VARIABLES
    phase, disposed, deactToRestart,      \* IgniteServerRunner, under gate
    current, nruns,                       \* currentRun (0: null), starts so far
    jvm,          \* per start: "none", "up", "cb" (stopped, AfterNodeStop not yet delivered), "down"
    rnode,        \* NodeRun.Node: "pending" (starting), "started", "failed"
    rstop,        \* NodeRun's stop: "none", "graceful", "restart", "cancel"
    tpc, tres,    \* the stop task: "none", "deact", "deactIn", "flag", "stop", "waitCb", "done"; its result
    tdeact,       \* the stop task deactivated the cluster
    rstopping, rstopped, rown,            \* NodeRun.Stopping, .Stopped, .StoppedOnItsOwn
    stopCalls,    \* calls of launcher.Stop per start
    active, stateLock,                    \* the cluster; ClusterActivator.stateLock: "free", "act", "deact"
    runPc, runI, runResult,
    stopPc, stopRun, stopResult,
    rsPc, rsRun, rsResult,
    dPc, dRun,
    env,
    stopReqRuns,  \* nruns when Stop or Dispose first requested a stop; -1 before
    timedOut,     \* a Restart returned TimedOut
    badTransition, wrongRun, undoneDeactivation   \* what the invariants rule out

vars == <<phase, disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tpc, tres, tdeact,
          rstopping, rstopped, rown, stopCalls, active, stateLock, runPc, runI, runResult,
          stopPc, stopRun, stopResult, rsPc, rsRun, rsResult, dPc, dRun, env, stopReqRuns,
          timedOut, badTransition, wrongRun, undoneDeactivation>>

TypeOK ==
    /\ phase \in Phases
    /\ current \in 0..MaxRuns /\ nruns \in 0..MaxRuns
    /\ jvm \in [Runs -> {"none", "up", "cb", "down"}]
    /\ rnode \in [Runs -> {"pending", "started", "failed"}]
    /\ rstop \in [Runs -> {"none", "graceful", "restart", "cancel"}]
    /\ tpc \in [Runs -> {"none", "deact", "deactIn", "flag", "stop", "waitCb", "done"}]
    /\ stateLock \in {"free", "act", "deact"}

\* IgniteServerRunner.Transition: a refused change is recorded (the code throws)
Transition(to) ==
    /\ phase' = to
    /\ badTransition' = (badTransition \/ (to # phase /\ to \notin Allowed[phase]))

\* RequestStopUnderGate (Stop, Dispose)
RequestStop ==
    /\ IF phase # "Stopped" THEN Transition("Stopping") ELSE UNCHANGED <<phase, badTransition>>
    /\ rstopping' = IF current # 0 THEN [rstopping EXCEPT ![current] = TRUE] ELSE rstopping
    /\ stopReqRuns' = IF stopReqRuns = -1 THEN nruns ELSE stopReqRuns

\* NodeRun.BeginStop: the first call begins the stop task; null (no change) while starting or after a failed start
BeginStop(i, kind) ==
    IF rnode[i] = "started" /\ rstop[i] = "none"
    THEN /\ rstop' = [rstop EXCEPT ![i] = kind]
         /\ tpc' = [tpc EXCEPT ![i] = IF rstopped[i] THEN "done" ELSE IF kind = "cancel" THEN "stop" ELSE "deact"]
         /\ tres' = [tres EXCEPT ![i] = IF rstopped[i] THEN TRUE ELSE tres[i]]
    ELSE UNCHANGED <<rstop, tpc, tres>>

Init ==
    /\ phase = "Idle" /\ disposed = FALSE /\ deactToRestart = FALSE
    /\ current = 0 /\ nruns = 0
    /\ jvm = [i \in Runs |-> "none"] /\ rnode = [i \in Runs |-> "pending"]
    /\ rstop = [i \in Runs |-> "none"] /\ tpc = [i \in Runs |-> "none"] /\ tres = [i \in Runs |-> FALSE]
    /\ tdeact = [i \in Runs |-> FALSE]
    /\ rstopping = [i \in Runs |-> FALSE] /\ rstopped = [i \in Runs |-> FALSE] /\ rown = [i \in Runs |-> FALSE]
    /\ stopCalls = [i \in Runs |-> 0]
    /\ active = FALSE /\ stateLock = "free"
    /\ runPc = "init" /\ runI = 0 /\ runResult = "none"
    /\ stopPc = "init" /\ stopRun = 0 /\ stopResult = "none"
    /\ rsPc = [r \in Restarters |-> "init"] /\ rsRun = [r \in Restarters |-> 0]
    /\ rsResult = [r \in Restarters |-> "none"]
    /\ dPc = "init" /\ dRun = 0
    /\ env = 0 /\ stopReqRuns = -1 /\ timedOut = FALSE
    /\ badTransition = FALSE /\ wrongRun = FALSE /\ undoneDeactivation = FALSE

-----------------------------------------------------------------------------
(* The Run thread: Run and RunOnce *)

RunCall ==
    /\ runPc = "init"
    /\ IF disposed
       THEN runPc' = "done" /\ runResult' = "threw"   \* ObjectDisposedException, before the try
       ELSE runPc' = "o1" /\ UNCHANGED runResult
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tpc, tres, tdeact,
                   rstopping, rstopped, rown, stopCalls, active, stateLock, runI, stopPc, stopRun,
                   stopResult, rsPc, rsRun, rsResult, dPc, dRun, env, stopReqRuns, timedOut,
                   badTransition, wrongRun, undoneDeactivation>>

\* RunOnce, first lock: no start after a stop; otherwise Starting and a new NodeRun
RunBegin ==
    /\ runPc = "o1"
    /\ IF phase \in {"Stopping", "Stopped"}
       THEN /\ runPc' = "after"
            /\ UNCHANGED <<phase, badTransition, current, nruns, runI>>
       ELSE /\ nruns < MaxRuns
            /\ Transition("Starting")
            /\ nruns' = nruns + 1 /\ current' = nruns + 1 /\ runI' = nruns + 1
            /\ runPc' = "start"
    /\ UNCHANGED <<disposed, deactToRestart, jvm, rnode, rstop, tpc, tres, tdeact, rstopping, rstopped, rown,
                   stopCalls, active, stateLock, runResult, stopPc, stopRun, stopResult, rsPc, rsRun,
                   rsResult, dPc, dRun, env, stopReqRuns, timedOut, wrongRun, undoneDeactivation>>

\* Ignition.Start brings the node up (its AfterNodeStart); Start has not returned yet
\* Without other server nodes, the cluster is this node's: a persistent one may activate itself (its baseline is
\* complete), so the node may start in an active cluster.
JvmUp ==
    /\ runPc = "start" /\ jvm[runI] = "none"
    /\ jvm' = [jvm EXCEPT ![runI] = "up"]
    /\ IF LastServer THEN active' \in BOOLEAN ELSE UNCHANGED active
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, rnode, rstop, tpc, tres, tdeact,
                   rstopping, rstopped, rown, stopCalls, stateLock, runPc, runI, runResult,
                   stopPc, stopRun, stopResult, rsPc, rsRun, rsResult, dPc, dRun, env, stopReqRuns,
                   timedOut, badTransition, wrongRun, undoneDeactivation>>

\* Ignition.Start throws before the node is up: OnStarted(null), and Run throws
StartFails ==
    /\ runPc = "start" /\ jvm[runI] = "none"
    /\ rnode' = [rnode EXCEPT ![runI] = "failed"]
    /\ runPc' = "finally" /\ runResult' = "threw"
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, jvm, rstop, tpc, tres, tdeact,
                   rstopping, rstopped, rown, stopCalls, active, stateLock, runI, stopPc, stopRun,
                   stopResult, rsPc, rsRun, rsResult, dPc, dRun, env, stopReqRuns, timedOut,
                   badTransition, wrongRun, undoneDeactivation>>

\* Start returns (also when the node already stopped on its own); second lock: OnStarted, Activating unless Stopping
StartReturns ==
    /\ runPc = "start" /\ jvm[runI] # "none"
    /\ rnode' = [rnode EXCEPT ![runI] = "started"]
    /\ IF phase = "Stopping"
       THEN runPc' = "beginStop" /\ UNCHANGED <<phase, badTransition>>
       ELSE Transition("Activating") /\ runPc' = "o5"
    /\ UNCHANGED <<disposed, deactToRestart, current, nruns, jvm, rstop, tpc, tres, tdeact, rstopping,
                   rstopped, rown, stopCalls, active, stateLock, runI, runResult, stopPc, stopRun,
                   stopResult, rsPc, rsRun, rsResult, dPc, dRun, env, stopReqRuns, timedOut, wrongRun,
                   undoneDeactivation>>

\* A stop came during the start: RunOnce begins the same graceful stop (in case Stop gave up)
RunBeginsStop ==
    /\ runPc = "beginStop"
    /\ BeginStop(runI, "graceful")
    /\ runPc' = "waitStopped"
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, jvm, rnode, tdeact, rstopping, rstopped,
                   rown, stopCalls, active, stateLock, runI, runResult, stopPc, stopRun, stopResult, rsPc,
                   rsRun, rsResult, dPc, dRun, env, stopReqRuns, timedOut, badTransition, wrongRun,
                   undoneDeactivation>>

\* Third lock: the run takes deactivatedToRestart
TakeReactivate ==
    /\ runPc = "o5"
    /\ deactToRestart' = FALSE
    /\ runPc' = "activate"
    /\ UNCHANGED <<phase, disposed, current, nruns, jvm, rnode, rstop, tpc, tres, tdeact, rstopping, rstopped,
                   rown, stopCalls, active, stateLock, runI, runResult, stopPc, stopRun, stopResult, rsPc,
                   rsRun, rsResult, dPc, dRun, env, stopReqRuns, timedOut, badTransition, wrongRun,
                   undoneDeactivation>>

\* ClusterActivator.ActivateAsync takes stateLock; no activation once the node is being stopped
ActivateEnter ==
    /\ runPc = "activate" /\ stateLock = "free"
    /\ IF rstopping[runI]
       THEN runPc' = "notActivated" /\ UNCHANGED stateLock
       ELSE runPc' = "activating" /\ stateLock' = "act"
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tpc, tres, tdeact,
                   rstopping, rstopped, rown, stopCalls, active, runI, runResult, stopPc, stopRun, stopResult,
                   rsPc, rsRun, rsResult, dPc, dRun, env, stopReqRuns, timedOut, badTransition, wrongRun,
                   undoneDeactivation>>

\* Under stateLock: IsActive, SetActive(true). A stopped node throws: caught by RunOnce when the node is stopping
ActivateExit ==
    /\ runPc = "activating"
    /\ stateLock' = "free"
    /\ IF jvm[runI] = "up"
       THEN /\ active' = TRUE
            /\ undoneDeactivation' = (undoneDeactivation \/ tdeact[runI])
            /\ runPc' = "configure"
            /\ UNCHANGED runResult
       ELSE /\ IF rstopping[runI] THEN runPc' = "notActivated" /\ UNCHANGED runResult
               ELSE runPc' = "finally" /\ runResult' = "threw"
            /\ UNCHANGED <<active, undoneDeactivation>>
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tpc, tres, tdeact,
                   rstopping, rstopped, rown, stopCalls, runI, stopPc, stopRun, stopResult, rsPc, rsRun,
                   rsResult, dPc, dRun, env, stopReqRuns, timedOut, badTransition, wrongRun>>

\* The rest of ActivateAsync (baseline, password): a stopped node throws, caught when stopping
Configure ==
    /\ runPc = "configure"
    /\ IF jvm[runI] = "up" THEN runPc' = "listen" /\ UNCHANGED runResult
       ELSE IF rstopping[runI] THEN runPc' = "notActivated" /\ UNCHANGED runResult
       ELSE runPc' = "finally" /\ runResult' = "threw"
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tpc, tres, tdeact,
                   rstopping, rstopped, rown, stopCalls, active, stateLock, runI, stopPc, stopRun, stopResult,
                   rsPc, rsRun, rsResult, dPc, dRun, env, stopReqRuns, timedOut, badTransition, wrongRun,
                   undoneDeactivation>>

NotActivated ==
    /\ runPc = "notActivated" /\ rstopped[runI]
    /\ runPc' = "after"
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tpc, tres, tdeact,
                   rstopping, rstopped, rown, stopCalls, active, stateLock, runI, runResult, stopPc, stopRun,
                   stopResult, rsPc, rsRun, rsResult, dPc, dRun, env, stopReqRuns, timedOut, badTransition,
                   wrongRun, undoneDeactivation>>

\* The event listeners (LocalListen) and the log of the running node: a stopped node may throw, caught when the node
\* is being stopped or restarted (as in the activation)
Listen ==
    /\ runPc = "listen"
    /\ \/ runPc' = "toRunning" /\ UNCHANGED runResult
       \/ /\ jvm[runI] # "up"
          /\ IF rstopping[runI] THEN runPc' = "waitStopped" /\ UNCHANGED runResult
             ELSE runPc' = "finally" /\ runResult' = "threw"
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tpc, tres, tdeact,
                   rstopping, rstopped, rown, stopCalls, active, stateLock, runI, stopPc, stopRun, stopResult,
                   rsPc, rsRun, rsResult, dPc, dRun, env, stopReqRuns, timedOut, badTransition, wrongRun,
                   undoneDeactivation>>

\* Fourth lock: Running, unless a stop or restart began, or the node stopped on its own
ToRunning ==
    /\ runPc = "toRunning"
    /\ IF phase = "Activating" THEN Transition("Running") ELSE UNCHANGED <<phase, badTransition>>
    /\ runPc' = "waitStopped"
    /\ UNCHANGED <<disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tpc, tres, tdeact, rstopping,
                   rstopped, rown, stopCalls, active, stateLock, runI, runResult, stopPc, stopRun, stopResult,
                   rsPc, rsRun, rsResult, dPc, dRun, env, stopReqRuns, timedOut, wrongRun, undoneDeactivation>>

WaitStopped ==
    /\ runPc = "waitStopped" /\ rstopped[runI]
    /\ runPc' = "after"
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tpc, tres, tdeact,
                   rstopping, rstopped, rown, stopCalls, active, stateLock, runI, runResult, stopPc, stopRun,
                   stopResult, rsPc, rsRun, rsResult, dPc, dRun, env, stopReqRuns, timedOut, badTransition,
                   wrongRun, undoneDeactivation>>

\* Run, after RunOnce: start again after a restart, otherwise return whether the stop was requested
AfterRunOnce ==
    /\ runPc = "after"
    /\ IF phase # "Restarting"
       THEN /\ runResult' = IF current # 0 /\ rown[current] THEN "false" ELSE "true"
            /\ runPc' = "finally"
            /\ UNCHANGED <<phase, badTransition>>
       ELSE Transition("Idle") /\ runPc' = "o1" /\ UNCHANGED runResult
    /\ UNCHANGED <<disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tpc, tres, tdeact, rstopping,
                   rstopped, rown, stopCalls, active, stateLock, runI, stopPc, stopRun, stopResult, rsPc, rsRun,
                   rsResult, dPc, dRun, env, stopReqRuns, timedOut, wrongRun, undoneDeactivation>>

\* Run's finally, its own lock
RunFinally ==
    /\ runPc = "finally"
    /\ Transition("Stopped")
    /\ runPc' = "done"
    /\ UNCHANGED <<disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tpc, tres, tdeact, rstopping,
                   rstopped, rown, stopCalls, active, stateLock, runI, runResult, stopPc, stopRun, stopResult,
                   rsPc, rsRun, rsResult, dPc, dRun, env, stopReqRuns, timedOut, wrongRun, undoneDeactivation>>

-----------------------------------------------------------------------------
(* Stop(timeout), from the hosted service's StopAsync *)

StopCall ==
    /\ stopPc = "init"
    /\ RequestStop
    /\ stopRun' = current
    /\ IF current = 0 THEN stopPc' = "done" /\ stopResult' = "true" ELSE stopPc' = "waitStarted" /\ UNCHANGED stopResult
    /\ UNCHANGED <<disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tpc, tres, tdeact, rstopped,
                   rown, stopCalls, active, stateLock, runPc, runI, runResult, rsPc, rsRun, rsResult, dPc, dRun,
                   env, timedOut, wrongRun, undoneDeactivation>>

\* NodeRun.StopWithin: once started, begin (or join) the graceful stop
StopBegins ==
    /\ stopPc = "waitStarted" /\ rnode[stopRun] # "pending"
    /\ BeginStop(stopRun, "graceful")
    /\ IF rnode[stopRun] = "failed" THEN stopPc' = "done" /\ stopResult' = "true"
       ELSE stopPc' = "waitTask" /\ UNCHANGED stopResult
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, jvm, rnode, tdeact, rstopping, rstopped,
                   rown, stopCalls, active, stateLock, runPc, runI, runResult, stopRun, rsPc, rsRun, rsResult,
                   dPc, dRun, env, stopReqRuns, timedOut, badTransition, wrongRun, undoneDeactivation>>

StopEnds ==
    /\ stopPc = "waitTask" /\ tpc[stopRun] = "done"
    /\ stopPc' = "done" /\ stopResult' = IF tres[stopRun] THEN "true" ELSE "false"
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tpc, tres, tdeact,
                   rstopping, rstopped, rown, stopCalls, active, stateLock, runPc, runI, runResult, stopRun,
                   rsPc, rsRun, rsResult, dPc, dRun, env, stopReqRuns, timedOut, badTransition, wrongRun,
                   undoneDeactivation>>

\* The timeout runs out while waiting for the start or the stop
StopGivesUp ==
    /\ Timeouts /\ stopPc \in {"waitStarted", "waitTask"}
    /\ stopPc' = "done" /\ stopResult' = "false"
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tpc, tres, tdeact,
                   rstopping, rstopped, rown, stopCalls, active, stateLock, runPc, runI, runResult, stopRun,
                   rsPc, rsRun, rsResult, dPc, dRun, env, stopReqRuns, timedOut, badTransition, wrongRun,
                   undoneDeactivation>>

-----------------------------------------------------------------------------
(* Restart(timeout), from SslFileRestarter *)

RestartCall(r) ==
    /\ rsPc[r] = "init"
    /\ IF phase \notin {"Activating", "Running"} \/ current = 0
       THEN /\ rsPc' = [rsPc EXCEPT ![r] = "done"] /\ rsResult' = [rsResult EXCEPT ![r] = "NotRunning"]
            /\ UNCHANGED <<phase, badTransition, rstopping, rsRun>>
       ELSE /\ Transition("Restarting")
            /\ rsRun' = [rsRun EXCEPT ![r] = current]
            /\ rstopping' = [rstopping EXCEPT ![current] = TRUE]
            /\ rsPc' = [rsPc EXCEPT ![r] = "waitStarted"]
            /\ UNCHANGED rsResult
    /\ UNCHANGED <<disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tpc, tres, tdeact, rstopped,
                   rown, stopCalls, active, stateLock, runPc, runI, runResult, stopPc, stopRun, stopResult,
                   dPc, dRun, env, stopReqRuns, timedOut, wrongRun, undoneDeactivation>>

RestartBegins(r) ==
    /\ rsPc[r] = "waitStarted" /\ rnode[rsRun[r]] # "pending"
    /\ BeginStop(rsRun[r], "restart")
    /\ rsPc' = [rsPc EXCEPT ![r] = IF rnode[rsRun[r]] = "failed" THEN "ok" ELSE "waitTask"]
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, jvm, rnode, tdeact, rstopping, rstopped,
                   rown, stopCalls, active, stateLock, runPc, runI, runResult, stopPc, stopRun, stopResult,
                   rsRun, rsResult, dPc, dRun, env, stopReqRuns, timedOut, badTransition, wrongRun,
                   undoneDeactivation>>

RestartTaskEnds(r) ==
    /\ rsPc[r] = "waitTask" /\ tpc[rsRun[r]] = "done"
    /\ rsPc' = [rsPc EXCEPT ![r] = IF tres[rsRun[r]] THEN "ok" ELSE "late"]
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tpc, tres, tdeact,
                   rstopping, rstopped, rown, stopCalls, active, stateLock, runPc, runI, runResult, stopPc,
                   stopRun, stopResult, rsRun, rsResult, dPc, dRun, env, stopReqRuns, timedOut, badTransition,
                   wrongRun, undoneDeactivation>>

RestartGivesUp(r) ==
    /\ Timeouts /\ rsPc[r] \in {"waitStarted", "waitTask"}
    /\ rsPc' = [rsPc EXCEPT ![r] = "late"]
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tpc, tres, tdeact,
                   rstopping, rstopped, rown, stopCalls, active, stateLock, runPc, runI, runResult, stopPc,
                   stopRun, stopResult, rsRun, rsResult, dPc, dRun, env, stopReqRuns, timedOut, badTransition,
                   wrongRun, undoneDeactivation>>

RestartOk(r) ==
    /\ rsPc[r] = "ok"
    /\ rsPc' = [rsPc EXCEPT ![r] = "done"] /\ rsResult' = [rsResult EXCEPT ![r] = "Restarting"]
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tpc, tres, tdeact,
                   rstopping, rstopped, rown, stopCalls, active, stateLock, runPc, runI, runResult, stopPc,
                   stopRun, stopResult, rsRun, dPc, dRun, env, stopReqRuns, timedOut, badTransition, wrongRun,
                   undoneDeactivation>>

\* The stop did not end in time; second lock: TimedOut (then Stopping) only if Run has not taken the restart
RestartLate(r) ==
    /\ rsPc[r] = "late"
    /\ CASE phase = "Restarting" ->
              Transition("Stopping") /\ rsResult' = [rsResult EXCEPT ![r] = "TimedOut"] /\ timedOut' = TRUE
         [] phase \in {"Stopping", "Stopped"} ->
              rsResult' = [rsResult EXCEPT ![r] = "NotRunning"] /\ UNCHANGED <<phase, badTransition, timedOut>>
         [] OTHER ->
              rsResult' = [rsResult EXCEPT ![r] = "Restarting"] /\ UNCHANGED <<phase, badTransition, timedOut>>
    /\ rsPc' = [rsPc EXCEPT ![r] = "done"]
    /\ UNCHANGED <<disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tpc, tres, tdeact, rstopping,
                   rstopped, rown, stopCalls, active, stateLock, runPc, runI, runResult, stopPc, stopRun,
                   stopResult, rsRun, dPc, dRun, env, stopReqRuns, wrongRun, undoneDeactivation>>

-----------------------------------------------------------------------------
(* Dispose, which the host calls after StopAsync; with a timeout, StopAsync may have given up while Stop goes on *)

DisposeCall ==
    /\ dPc = "init" /\ (stopPc = "done" \/ (Timeouts /\ stopPc # "init"))
    /\ disposed' = TRUE
    /\ RequestStop
    /\ dRun' = current
    /\ dPc' = "begin"
    /\ UNCHANGED <<deactToRestart, current, nruns, jvm, rnode, rstop, tpc, tres, tdeact, rstopped, rown,
                   stopCalls, active, stateLock, runPc, runI, runResult, stopPc, stopRun, stopResult, rsPc,
                   rsRun, rsResult, env, timedOut, wrongRun, undoneDeactivation>>

\* NodeRun.BeginStopIfNoneBegan: a stop with the jobs cancelled, only if no stop began; then a bounded wait
DisposeBegins ==
    /\ dPc = "begin"
    /\ IF dRun # 0 THEN BeginStop(dRun, "cancel") ELSE UNCHANGED <<rstop, tpc, tres>>
    /\ dPc' = "done"
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, jvm, rnode, tdeact, rstopping, rstopped,
                   rown, stopCalls, active, stateLock, runPc, runI, runResult, stopPc, stopRun, stopResult, rsPc,
                   rsRun, rsResult, dRun, env, stopReqRuns, timedOut, badTransition, wrongRun,
                   undoneDeactivation>>

-----------------------------------------------------------------------------
(* The stop task of start i (NodeRun.BeginStop runs it on the thread pool) *)

\* DeactivateIfLastServerNode: TryEnter stateLock, or wait for the activation (GiveUp below)
TaskDeactEnter(i) ==
    /\ tpc[i] = "deact" /\ stateLock = "free"
    /\ stateLock' = "deact" /\ tpc' = [tpc EXCEPT ![i] = "deactIn"]
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tres, tdeact, rstopping,
                   rstopped, rown, stopCalls, active, runPc, runI, runResult, stopPc, stopRun, stopResult, rsPc,
                   rsRun, rsResult, dPc, dRun, env, stopReqRuns, timedOut, badTransition, wrongRun,
                   undoneDeactivation>>

TaskDeactGivesUp(i) ==
    /\ Timeouts /\ tpc[i] = "deact"
    /\ tpc' = [tpc EXCEPT ![i] = "stop"]
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tres, tdeact, rstopping,
                   rstopped, rown, stopCalls, active, stateLock, runPc, runI, runResult, stopPc, stopRun,
                   stopResult, rsPc, rsRun, rsResult, dPc, dRun, env, stopReqRuns, timedOut, badTransition,
                   wrongRun, undoneDeactivation>>

\* Under stateLock: an active cluster whose last server node this is is deactivated (a stopped node cannot)
TaskDeactivate(i) ==
    /\ tpc[i] = "deactIn"
    /\ LET deact == active /\ LastServer /\ jvm[i] = "up" IN
         /\ active' = (active /\ ~deact)
         /\ tdeact' = [tdeact EXCEPT ![i] = deact]
         /\ tpc' = [tpc EXCEPT ![i] = IF deact /\ rstop[i] = "restart" THEN "flag" ELSE "stop"]
    /\ stateLock' = "free"
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tres, rstopping, rstopped,
                   rown, stopCalls, runPc, runI, runResult, stopPc, stopRun, stopResult, rsPc, rsRun, rsResult,
                   dPc, dRun, env, stopReqRuns, timedOut, badTransition, wrongRun, undoneDeactivation>>

\* The runner's lock: the next run activates the cluster again
TaskFlag(i) ==
    /\ tpc[i] = "flag"
    /\ deactToRestart' = TRUE /\ tpc' = [tpc EXCEPT ![i] = "stop"]
    /\ UNCHANGED <<phase, disposed, current, nruns, jvm, rnode, rstop, tres, tdeact, rstopping, rstopped, rown,
                   stopCalls, active, stateLock, runPc, runI, runResult, stopPc, stopRun, stopResult, rsPc,
                   rsRun, rsResult, dPc, dRun, env, stopReqRuns, timedOut, badTransition, wrongRun,
                   undoneDeactivation>>

\* launcher.Stop: the GRACEFUL policy holds the last server node of an active cluster; an unknown node returns false
Blocked(i) == LastServer /\ active

\* The cluster of the only server node ends with it
ClusterGoesWith(i) == IF LastServer THEN active' = FALSE ELSE UNCHANGED active

TaskStop(i) ==
    /\ tpc[i] = "stop"
    /\ stopCalls' = [stopCalls EXCEPT ![i] = @ + 1]
    /\ IF jvm[i] = "up"
       THEN /\ ~Blocked(i)
            /\ jvm' = [jvm EXCEPT ![i] = "cb"]
            /\ tpc' = [tpc EXCEPT ![i] = "waitCb"]
            /\ ClusterGoesWith(i)
            /\ UNCHANGED tres
       ELSE /\ tpc' = [tpc EXCEPT ![i] = "done"]
            /\ tres' = [tres EXCEPT ![i] = FALSE]
            /\ UNCHANGED <<jvm, active>>
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, rnode, rstop, tdeact, rstopping, rstopped,
                   rown, stateLock, runPc, runI, runResult, stopPc, stopRun, stopResult, rsPc, rsRun,
                   rsResult, dPc, dRun, env, stopReqRuns, timedOut, badTransition, wrongRun, undoneDeactivation>>

\* Ignition.Stop returns once the node's AfterNodeStop has been delivered
TaskStopReturns(i) ==
    /\ tpc[i] = "waitCb" /\ jvm[i] = "down"
    /\ tpc' = [tpc EXCEPT ![i] = "done"] /\ tres' = [tres EXCEPT ![i] = TRUE]
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, jvm, rnode, rstop, tdeact, rstopping,
                   rstopped, rown, stopCalls, active, stateLock, runPc, runI, runResult, stopPc, stopRun,
                   stopResult, rsPc, rsRun, rsResult, dPc, dRun, env, stopReqRuns, timedOut, badTransition,
                   wrongRun, undoneDeactivation>>

-----------------------------------------------------------------------------
(* Ignite *)

\* The node stops on its own (e.g., segmented)
NodeStopsOnItsOwn(i) ==
    /\ jvm[i] = "up" /\ env < MaxEnvEvents
    /\ jvm' = [jvm EXCEPT ![i] = "cb"] /\ env' = env + 1
    /\ ClusterGoesWith(i)
    /\ UNCHANGED <<phase, disposed, deactToRestart, current, nruns, rnode, rstop, tpc, tres, tdeact, rstopping,
                   rstopped, rown, stopCalls, stateLock, runPc, runI, runResult, stopPc, stopRun,
                   stopResult, rsPc, rsRun, rsResult, dPc, dRun, stopReqRuns, timedOut, badTransition,
                   wrongRun, undoneDeactivation>>

\* AfterNodeStop reaches NodeStopHandler: OnNodeStopped signals the current run
AfterNodeStop(i) ==
    /\ jvm[i] = "cb"
    /\ jvm' = [jvm EXCEPT ![i] = "down"]
    /\ IF current = 0
       THEN UNCHANGED <<phase, badTransition, rstopping, rstopped, rown, wrongRun>>
       ELSE LET own == phase \in {"Starting", "Activating", "Running"} IN
              /\ wrongRun' = (wrongRun \/ i # current)
              /\ IF own THEN Transition("Stopping") ELSE UNCHANGED <<phase, badTransition>>
              /\ rown' = [rown EXCEPT ![current] = own]
              /\ rstopping' = [rstopping EXCEPT ![current] = TRUE]
              /\ rstopped' = [rstopped EXCEPT ![current] = TRUE]
    /\ UNCHANGED <<disposed, deactToRestart, current, nruns, rnode, rstop, tpc, tres, tdeact, stopCalls, active,
                   stateLock, runPc, runI, runResult, stopPc, stopRun, stopResult, rsPc, rsRun, rsResult, dPc,
                   dRun, env, stopReqRuns, timedOut, undoneDeactivation>>

-----------------------------------------------------------------------------

RunStep == RunCall \/ RunBegin \/ JvmUp \/ StartReturns \/ RunBeginsStop \/ TakeReactivate \/ ActivateEnter
           \/ ActivateExit \/ Configure \/ NotActivated \/ Listen \/ ToRunning \/ WaitStopped \/ AfterRunOnce
           \/ RunFinally

TaskStep(i) == TaskDeactEnter(i) \/ TaskDeactivate(i) \/ TaskFlag(i) \/ TaskStop(i) \/ TaskStopReturns(i)

Next ==
    \/ RunStep \/ StartFails
    \/ StopCall \/ StopBegins \/ StopEnds \/ StopGivesUp
    \/ \E r \in Restarters : RestartCall(r) \/ RestartBegins(r) \/ RestartTaskEnds(r) \/ RestartGivesUp(r)
                             \/ RestartOk(r) \/ RestartLate(r)
    \/ DisposeCall \/ DisposeBegins
    \/ \E i \in Runs : TaskStep(i) \/ TaskDeactGivesUp(i) \/ NodeStopsOnItsOwn(i) \/ AfterNodeStop(i)

Spec == Init /\ [][Next]_vars

\* Liveness: the threads make progress, Ignite delivers AfterNodeStop and Stop is called. Timeouts (GiveUp), failed
\* starts, own stops, restarts and Dispose are possible but not forced.
FairSpec ==
    /\ Spec
    /\ WF_vars(RunStep)
    /\ WF_vars(StopCall \/ StopBegins \/ StopEnds)
    /\ \A r \in Restarters : WF_vars(RestartBegins(r) \/ RestartTaskEnds(r) \/ RestartOk(r) \/ RestartLate(r))
    /\ WF_vars(DisposeBegins)
    /\ \A i \in Runs : WF_vars(TaskStep(i)) /\ WF_vars(AfterNodeStop(i))

-----------------------------------------------------------------------------
(* Properties *)

\* Two nodes never run at once (ports, the work directory)
AtMostOneNode == Cardinality({i \in Runs : jvm[i] \in {"up", "cb"}}) <= 1

\* A stop requested by Stop or Dispose is never undone: no start of the node after it
StopWins == stopReqRuns # -1 => nruns = stopReqRuns

\* Ignite stops a node once: launcher.Stop is called at most once per start
OneStopPerNode == \A i \in Runs : stopCalls[i] <= 1

\* Activating or Running only while the current node is up (or its AfterNodeStop is on its way)
PhaseMatchesNode == phase \in {"Activating", "Running"} => current # 0 /\ jvm[current] \in {"up", "cb"}

\* Only the changes of AllowedTransitions; AfterNodeStop always concerns the current run; an activation never
\* undoes the deactivation of its own stopping node
NoBadTransition == ~badTransition
CallbackForCurrentRun == ~wrongRun
NoActivationUndoesDeactivation == ~undoneDeactivation

\* Run returns false only after its node stopped on its own, and true only after a requested stop (or a restart
\* that timed out, which SslFileRestarter reports by stopping the server)
RunResultHonest ==
    /\ runResult = "false" => \E i \in Runs : rown[i]
    /\ runResult = "true" => stopReqRuns # -1 \/ timedOut

\* Run throws only when it must: a failed start, a Run called after Dispose, or a node that stopped on its own. A
\* requested stop or restart never makes it throw (the hosted service would report a restart as a failure).
RunThrowsOnlyOnFailure ==
    runResult = "threw" => \/ \E i \in Runs : rnode[i] = "failed"
                           \/ runPc = "done" /\ nruns = 0 /\ disposed
                           \/ env > 0

\* Liveness: once Stop is called, Run returns, and so do Stop and every Restart that was called
StopEndsTheRun == stopPc # "init" ~> runPc = "done"
CallsReturn == /\ stopPc # "init" ~> stopPc = "done"
               /\ \A r \in Restarters : rsPc[r] # "init" ~> rsPc[r] = "done"
=============================================================================
