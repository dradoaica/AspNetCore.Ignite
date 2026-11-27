# Architecture

The repository builds two things:

* **The server** (`src/AspNetCore.IgniteServer`, the `aspnetcore.ignite-server` image): a .NET host that runs one
  Apache Ignite 2 server node in its own process (Ignite.NET embedded mode, which starts a JVM in the process). It adds
  what running Ignite in containers needs: settings from the environment, validation, health and metrics endpoints,
  password and certificate rotation, cluster activation, and a graceful stop.
* **The client library** (`src/AspNetCore.Ignite`, the `AspNetCore.Ignite` package): a thin client for ASP.NET Core
  applications, with dependency injection, retries and a health check.

## Server components

```mermaid
flowchart TB
    subgraph process["Server process (dotnet AspNetCore.IgniteServer.dll)"]
        program["Program<br/>SIGTERM handling, exit code"]
        cmd["ServerCommandLine<br/>-Cluster, -SpiPort, ..."]
        settings["IgniteServerSettings<br/>Load, Validate"]
        exit["ServerExit"]

        subgraph host["Generic host (Kestrel on MANAGEMENT_PORT when MANAGEMENT_ENABLED)"]
            hosted["IgniteServerHostedService"]
            ssl["SslFileRestarter"]
            watcher["FileChangeWatcher<br/>polling, debounce, jitter"]
            state["IgniteNodeState"]
            health["Health checks<br/>/health/startup, /health/readiness, /health/liveness"]
            metrics["IgniteNodeMetrics<br/>/metrics, OTLP"]
        end

        renderer["SpringConfigRenderer<br/>template to private file"]
        runner["IgniteServerRunner<br/>start, restart, stop"]
        activator["ClusterActivator<br/>activation, baseline"]
        factory["IgniteConfigurationFactory"]
        launcher["INodeLauncher<br/>(Ignition, or a fake in tests)"]
        listeners["RebalanceTracker<br/>LostPartitionsListener<br/>DiscoveryEventListener"]
        password["IgniteUserPasswordManager"]

        subgraph jvm["JVM"]
            node["Ignite server node"]
        end
    end

    cmd --> program
    program --> settings
    program --> host
    hosted --> renderer
    hosted --> runner
    hosted --> ssl
    hosted --> exit
    ssl --> watcher
    ssl -->|"Restart"| runner
    runner --> factory
    runner --> launcher
    launcher --> node
    runner --> listeners
    runner --> activator
    activator --> password
    password -->|"thin client over loopback"| node
    node -.->|"events"| listeners
    state --> runner
    health --> state
    metrics --> state
    program -->|"returns"| exit
```

* **`Program`** reads the configuration, validates the settings (exit code `1` with every error listed), builds the
  host and returns the exit code of `ServerExit`. It cancels the default SIGTERM handling, so that the host stops the
  node gracefully and the process returns its own exit code.
* **`IgniteServerHostedService`** owns the node for the lifetime of the host. When it is created, it logs the security
  warnings, renders the Spring configuration and creates the runner, so an invalid configuration fails the host's
  start (exit code `1`). It then starts the SSL restarts, stops the node with the host, and turns a failure into exit
  code `1` (and a stop that was not graceful into `2`).
* **`IgniteServerRunner`** starts the node, has its cluster activated, registers the event listeners, and restarts or
  stops the node on request; a stop that comes before the start means no node is started. Each run moves through the
  phases of its `Phase` enum, which the [Node lifecycle](#node-lifecycle) diagram shows. The phase and the rest of the
  lifecycle state are guarded by one lock, which is never held while Ignite is called. It starts and stops nodes
  through `INodeLauncher`, so that its lifecycle can be tested without a JVM.
* **`ClusterActivator`** activates the cluster of a started node (once `ACTIVATION_MIN_SERVER_NODES` have joined, for a
  persistent cluster that starts for the first time), turns on the baseline auto-adjust and applies
  `IGNITE_USER_PASSWORD`. It also deactivates the cluster before the cluster's last server node stops. Activation and
  deactivation take turns under a lock of its own, and no activation begins once a stop or restart of the node has
  begun, so an activation never undoes the deactivation of a stopping node. It keeps nothing between runs: the runner
  passes in what it remembers.
* **`IgniteNodeState`** is what health checks and metrics read: the running node, whether it is started and ready, and
  the caches it rebalances.

## Configuration

```mermaid
flowchart LR
    defaults["Defaults<br/>(IgniteServerSettings)"] --> json["appsettings.json"]
    json --> env["Environment"]
    env --> cmdline["Command line"]
    cmdline --> settings["IgniteServerSettings.Load"]
    settings --> validate{"Validate"}
    validate -->|"errors"| exit1["exit 1"]
    validate -->|"valid"| netconfig["IgniteConfiguration<br/>(IgniteConfigurationFactory)"]
    validate -->|"valid"| spring["Spring XML<br/>(SpringConfigRenderer)"]
    netconfig --> start(["Ignition.Start"])
    spring --> start
```

* Each source overrides the previous one. A list setting (`CLUSTER_ENDPOINTS`, `SQL_SCHEMAS`) comes from one source,
  the one of the highest priority: the configuration would otherwise merge the lists of several sources index by index.
* The node is configured twice, and Ignite merges both:
    * **.NET** (`IgniteConfigurationFactory`): the default configuration, then a `-ConfigFile` (the properties it sets),
      then the settings: JVM options, memory, storage, timeouts, thread pools, static discovery, authentication, SSL
      between nodes. The .NET configuration wins for the properties it sets. The JVM options and event types the server
      needs are added back after the file (`RequiredEventTypes`, the `--add-opens` options, `-Xrs`).
    * **Spring XML** (`config/spring-config*.xml`, rendered into a private file): what .NET cannot set, the GRACEFUL
      shutdown policy, no environment variables as node attributes, the Kubernetes IP finder, thin client SSL, and the
      `SPRING_EXTRA_*` fragments. Values are inserted at `{{NAME}}` tokens, XML-escaped; Spring expressions (`#{`) are
      refused.
* Discovery is static (`CLUSTER_ENDPOINTS`, or the node itself) unless `USE_TCP_DISCOVERY_STATIC_IP_FINDER=false`
  without endpoints or `SPI_PORT`: the nodes then find each other through the Kubernetes API.

## Node lifecycle

```mermaid
stateDiagram-v2
    [*] --> Idle
    Idle --> Starting: Run
    Idle --> Stopping: Stop before the start
    Starting --> Activating: started
    state Activating {
        [*] --> WaitingForServerNodes: persistent, first activation
        [*] --> Activation: in memory, or reactivation
        WaitingForServerNodes --> Activation: ACTIVATION_MIN_SERVER_NODES joined
    }
    Activating --> Running: cluster active, password applied
    state Running {
        [*] --> CatchingUp
        CatchingUp --> Ready: caught up, or REBALANCE_READINESS_MAX_WAIT_IN_SECONDS passed
    }
    Activating --> Restarting: Restart (no activation)
    Running --> Restarting: Restart (SSL files changed)
    Restarting --> Idle: the node stopped
    Starting --> Stopping: Stop (the node is stopped once started)
    Activating --> Stopping: Stop (no activation)
    Running --> Stopping: Stop (SIGTERM)
    Restarting --> Stopping: Stop, or the restart timed out
    Starting --> Stopping: the node stopped on its own
    Activating --> Stopping: the node stopped on its own
    Running --> Stopping: the node stopped on its own
    Stopping --> Stopped: the node stopped
    Stopped --> [*]
```

The states are the values of `IgniteServerRunner.Phase`. `IgniteServerRunner.Transition` refuses any change that the
diagram does not show, so the diagram and the code cannot drift apart unnoticed. The one exception is a `Run` that
throws (e.g., a failed start), which ends in `Stopped` from the phase it is in. Each start of the node is a `NodeRun`:
its node, the signals of its start and stop, and its one stop. The states inside `Activating` and
`Running` are those of `ClusterActivator` and `RebalanceTracker`.

* **Started** (`/health/startup`) once the node has joined the topology, also while its cluster waits for
  `ACTIVATION_MIN_SERVER_NODES`, so that a startup probe does not restart nodes waiting for each other.
* **Ready** (`/health/readiness`) once the cluster is active and, with `READY_WHEN_REBALANCED`, the node has caught up:
  it is in the baseline (which the baseline auto-adjust adds it to) and has not rebalanced for 10 seconds
  (`RebalanceTracker`). A rolling restart therefore moves on only once the restarted node holds its data again. The
  catch-up advances when readiness is read (the probe, the `node_ready` gauge), because the baseline is read from
  Ignite on each read. So that a node is never unready for good, it is ready anyway
  `REBALANCE_READINESS_MAX_WAIT_IN_SECONDS` (600 by default) after it runs when it is still not in the baseline, and a
  rebalance that started longer ago than that is ignored.
* **Restart and stop requests** move the run to `Restarting` or `Stopping`: a stop always wins over a restart, and a
  restart is taken by the next start. A request that comes during the activation means the cluster is not activated.
  `Restart` returns `Restarting`, `NotRunning` or `TimedOut`.
* **The graceful stop** (Ignite's GRACEFUL shutdown policy) waits until the node's partitions have copies on other
  nodes, within `GRACEFUL_STOP_TIMEOUT_IN_SECONDS`; a node that is still starting is stopped once started, within the
  same timeout. No other node can take over the partitions of the cluster's last server node, so that node deactivates
  the cluster first and stops at once; after a restart, it activates the cluster again without waiting for other
  nodes.
* **A node that stops on its own** (e.g., segmented from the cluster) stops the server with exit code `1`, and its
  container restarts.

What each call does in each phase. Every start of the node is stopped once: the first stop begins it, and a later stop
or restart waits for that same stop instead of beginning another. `Run` returns only once its node has stopped, so two
nodes never run at once.

| Phase        | `Restart(timeout)`                                                                                                                                    | `Stop(timeout)`                                                                                                                            | The node stops on its own                    |
|--------------|-------------------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------|
| `Idle`       | `NotRunning`                                                                                                                                          | Goes to `Stopping`; `Run` starts no node. Returns true.                                                                                    | –                                            |
| `Starting`   | `NotRunning`: the node may have read the old files, so `SslFileRestarter` asks again later                                                            | Goes to `Stopping`, waits for the start, then stops the node. False if the start outlasts the timeout; `Run` still stops the node once started. | Goes to `Stopping`, seen even before the start returns; `Run` returns false |
| `Activating` | Goes to `Restarting`: the cluster is not activated, the node stops, and `Run` starts it again                                                          | Goes to `Stopping`: an activation not yet begun is skipped, one in progress ends first; then the graceful stop    | Goes to `Stopping`; `Run` returns false (exit code `1`) |
| `Running`    | Goes to `Restarting`: the node stops (the last server node deactivates the cluster first) and `Run` starts it again. `TimedOut` goes to `Stopping` instead | Goes to `Stopping`: the graceful stop (the last server node deactivates the cluster first)                                                | Goes to `Stopping`; `Run` returns false (exit code `1`) |
| `Restarting` | `NotRunning`                                                                                                                                          | Goes to `Stopping`: waits for the restart's stop, and `Run` starts no node again                                                          | Part of the restart: `Run` starts it again   |
| `Stopping`   | `NotRunning`                                                                                                                                          | Waits for the stop in progress                                                                                                             | –                                            |
| `Stopped`    | `NotRunning`                                                                                                                                          | Returns true at once                                                                                                                       | –                                            |

[`docs/tlaplus/ServerLifecycle`](tlaplus/README.md) models this table as a TLA+ spec: TLC checks every interleaving of
these calls, the node's own stop and the stop tasks (two nodes never run at once, a requested stop is never undone, a
node is stopped once, and a stop always ends the run).

`Dispose` requests a stop like `Stop`, and stops a node that is still running with its jobs cancelled, waiting at most
`GRACEFUL_STOP_TIMEOUT_IN_SECONDS`. It does not wait for a stop that began earlier: that node stops with the process.

### Certificate rotation

```mermaid
sequenceDiagram
    participant files as SSL files (e.g., a Secret)
    participant watcher as FileChangeWatcher
    participant restarter as SslFileRestarter
    participant runner as IgniteServerRunner
    participant node as Ignite node

    files->>watcher: change (polling)
    Note over watcher: debounce 10 s,<br/>then a random delay up to SSL_RESTART_MAX_JITTER_IN_SECONDS
    watcher->>restarter: OnFilesChanged
    alt node starting
        restarter->>watcher: ask again after the delay
    else node running
        restarter->>runner: Restart(GRACEFUL_STOP_TIMEOUT_IN_SECONDS)
        runner->>node: graceful stop (the last server node deactivates the cluster first)
        alt stopped within the timeout
            node-->>runner: Stopped
            runner->>node: start again, with the renewed files
        else the stop timed out
            runner-->>restarter: TimedOut
            restarter->>restarter: fail (exit 1): the container restarts with the renewed files
        end
    end
```

### Exit codes

`0` after a graceful stop, `1` after a failure (invalid settings, a failed start, a node that stopped unexpectedly), `2`
after a stop that was not graceful: see [Configuration](configuration.md#exit-codes) for the table. `ServerExit` keeps
the first cause: a failure stops the server, and that stop is then not graceful either.

## Cluster maintenance on the node

* **Activation and baseline** (`ClusterActivator`): a persistent cluster records its baseline on its first
  activation, so it waits for `ACTIVATION_MIN_SERVER_NODES`. The baseline then follows the topology after
  `BASELINE_AUTO_ADJUST_TIMEOUT_IN_SECONDS` (5 minutes with persistence, so that a restarted node returns with its data;
  30 seconds without).
* **Password** (`IgniteUserPasswordManager`): the node connects to itself as a thin client over loopback (never to other
  nodes, which would receive the password too) and runs `ALTER USER` through a temporary cache of its own. It tries the
  most likely current password first (the default one on a new cluster, `IGNITE_USER_PREVIOUS_PASSWORD` otherwise), and
  a password another node applied first counts as applied.
* **Lost partitions** (`LostPartitionsListener`): every node logs and remembers the caches that lost partitions; the
  oldest server node resets them, once per batch of events and again on every topology change. A persistent cluster
  waits for a node that owns them to return; an in-memory one drops the departed nodes from the baseline first, and that
  data is gone.

## Client library

```mermaid
flowchart LR
    app["ASP.NET Core application"] -->|"AddIgnite()"| manager["IgniteManager<br/>(IIgniteManager)"]
    hc["IgniteHealthCheck"] --> manager
    manager -->|"one shared connection attempt,<br/>ConnectRetryCount"| client["Ignite thin client"]
    client -->|"RetryLimit, partition awareness,<br/>cluster discovery"| cluster["Ignite cluster<br/>(port 10800)"]
```

* `IgniteManager` connects on first use, not when it is resolved. Concurrent callers share one connection attempt, and
  a failed attempt is retried by the next call.
* Retries happen at three levels, each for its own failures: the connection (`ConnectRetryCount`), the operations of
  `IgniteManager` once connected (`OperationRetryCount`, transient failures only: a lost connection or a cluster that is
  not active), and the reads of the thin client itself (`RetryLimit`).
* Options come from `ASPNETCORE_IGNITE_*` keys, a configuration section, or code (`AddIgnite(...)`).

## Tests

* **Unit tests** (no Docker, no JVM): settings and validation, configuration and Spring rendering, listeners, metrics,
  and the node lifecycle on the fake nodes of `FakeNodeLauncher`, whose stops a test can hold back.
  `ServerLogTests` checks that the log messages the integration tests wait for still exist, and `DocumentationTests`
  that every setting is in [Configuration](configuration.md).
* **Integration tests** (Docker, Testcontainers): the image itself, with real clusters: authentication, SSL and
  rotation, activation, rebalancing, lost partitions, the management endpoints, the control utility and the exit codes.
* **Chart tests** (CI): the image with its Helm chart in kind.

See [Development](development.md) for how to run them.

## Where to change what

| To change                                    | Look at                                                         |
|----------------------------------------------|-----------------------------------------------------------------|
| A setting (name, default, validation)        | `IgniteServerSettings*.cs`, then `docs/configuration.md`        |
| How a setting configures the node            | `IgniteConfigurationFactory.cs`, or `config/spring-config*.xml` |
| Start, restart and stop                      | `IgniteServerRunner.cs`, `NodeRun.cs` (tests: `IgniteServerLifecycleTests`, `NodeRunTests`; spec: `ServerLifecycle.tla`) |
| Cluster activation and the baseline          | `ClusterActivator.cs`                                           |
| Host integration, exit codes                 | `IgniteServerHostedService.cs`, `ServerExit.cs`, `Program.cs`   |
| Certificate rotation                         | `SslFileRestarter.cs`, `Utils/FileChangeWatcher.cs`             |
| Health and metrics                           | `Management/`                                                   |
| A log message the integration tests wait for | the message, and `ServerLog.cs` of the integration tests        |
| The client library                           | `src/AspNetCore.Ignite`, then `docs/client.md`                  |
