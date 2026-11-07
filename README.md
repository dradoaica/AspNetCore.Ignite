# AspNetCore.IgniteServer (extracted from [Tarzan](https://github.com/awesomedotnetcore/Tarzan) and improved)

AspNetCore.IgniteServer is an implementation of Apache Ignite 2 embedded mode that references necessary assemblies
to provide a server-side component for the ASP.NET Core platform. Kubernetes ready, authentication and SSL enabled with
auto-discovery clustering capabilities. Usually, every node runs one or more instances of the server.

The repository also contains `AspNetCore.Ignite`, a thin client library for ASP.NET Core applications (see
[Client library](#client-library-aspnetcoreignite)).

## Usage

The server can be executed with various options:

```
dotnet AspNetCore.IgniteServer.dll [options]

Options:
  -?, -h, --help, -Help         Show help and usage information
  -ConfigFile <ConfigFile>      XML configuration file. If no file is specified then the default configuration is used.
  -OffHeap <OffHeap>            Size of off-heap memory given in megabytes.
  -OnHeap <OnHeap>              Size of on-heap memory given in megabytes.
  -SpiPort <SpiPort>            Specifies port for Discovery Spi.
  -Cluster <Cluster>            Specifies IP address and port of a cluster node. Multiple nodes can be specified.
  -ConsistentId <ConsistentId>  Specifies as a consistent id of the node. This value is used in topology.
  -PersistenceEnabled           If set, it enables persistence mode.
```

Option names are case-sensitive. Every option has a [setting](#configuration) of the same meaning, so it can also be
given as an environment variable.

The default configuration uses the default parameters of the Ignite environment. A configuration file
(`-ConfigFile`, Ignite.NET XML) is applied over it: the properties the file sets override the defaults, the others
(JVM options, timeouts, event types, ...) stay; a nested element (e.g., `dataStorageConfiguration`) replaces the default
one as a whole. Use it for caches, binary, SQL or transaction configuration, etc. `PEER_ASSEMBLY_LOADING` always decides
peer assembly loading. There are two predefined configuration files available in `config` folder:

* `default-config.xml` - this is minimal configuration for running memory only Ignite server.
* `persistent-config.xml` - this is minimal configuration for running persistent Ignite server.

The server runs on the .NET Generic Host: SIGTERM/CTRL+C stop the node gracefully. The `GRACEFUL` shutdown policy
waits until other nodes hold copies of the node's partitions, which never happens when the whole cluster stops; after
`GRACEFUL_STOP_TIMEOUT_IN_SECONDS` an error is logged and the node stops with the process (persistent nodes recover
from the WAL on the next start).

| Exit code | Meaning |
|---|---|
| `0` | Stopped gracefully (SIGTERM/CTRL+C). |
| `1` | Invalid arguments or settings, failed start, or the Ignite node stopped unexpectedly. |

## Configuration

Settings are read from `appsettings.json`, overridden by environment variables of the same name, overridden by the
command line options.

| Setting | Option | Default | Description |
|---|---|---|---|
| `CONFIG_FILE` | `-ConfigFile` | _(empty)_ | XML configuration file. |
| `DEFAULT_ON_HEAP_MEMORY` / `DEFAULT_OFF_HEAP_MEMORY` | `-OnHeap` / `-OffHeap` | `1024` / `2048` | Memory (MB), see [Configuring Memory](#configuring-memory). |
| `SPI_PORT` | `-SpiPort` | _(Ignite default)_ | Discovery port. |
| `CLUSTER_ENDPOINTS` | `-Cluster` | _(empty)_ | Static discovery endpoints: comma-separated, or `CLUSTER_ENDPOINTS__0`, `CLUSTER_ENDPOINTS__1`, ... |
| `USE_TCP_DISCOVERY_STATIC_IP_FINDER` | | `true` | Use the local address as the static discovery endpoint when no cluster endpoints are given. |
| `DEFAULT_CONSISTENT_ID` | `-ConsistentId` | _(empty)_ | Consistent id of the node. Must be **unique per node**; leave empty to let Ignite derive one. |
| `PERSISTENCE_ENABLED` | `-PersistenceEnabled` | `false` | Enables persistence. |
| `K8S_NAMESPACE` / `K8S_SERVICE_NAME` | | _(empty)_ | Kubernetes IP finder settings. |
| `PEER_ASSEMBLY_LOADING` | | `false` | Load .NET assemblies sent by other nodes (and so run their code); only for trusted clusters. |
| `REST_ENABLED` | | `false` | Start the REST API (Jetty, port 8080) and the TCP binary protocol (port 11211); both are unencrypted. |
| `ENABLE_AUTHENTICATION` | | `true` | Enables authentication (implies persistence). |
| `IGNITE_USER_PASSWORD` | | _(empty)_ | Password of the default `ignite` user (see [Authentication](#authentication)). |
| `IGNITE_USER_PREVIOUS_PASSWORD` | | _(empty)_ | The current password, when rotating it to `IGNITE_USER_PASSWORD`. |
| `ACTIVATION_MIN_SERVER_NODES` | | `1` | Persistent clusters are activated only after this many server nodes joined (see [Persistent Mode](#persistent-mode)). |
| `BASELINE_AUTO_ADJUST_TIMEOUT_IN_SECONDS` | | `30` | Delay of the baseline auto-adjust after topology changes. |
| `READY_WHEN_REBALANCED` / `REBALANCE_READINESS_MAX_WAIT_IN_SECONDS` | | `true` / `600` | The node is ready only once it has received the partitions of its caches (at most this long), so rolling restarts wait for the data before stopping the next node. |
| `AUTO_RESET_LOST_PARTITIONS` | | `true` | Reset lost partitions automatically so caches stay usable. **The lost data is not recovered**; data loss is always logged as an error. |
| `USE_SSL` | | `false` | Enables SSL between nodes; requires `SSL_KEY_STORE_*` and `SSL_TRUST_STORE_*`. |
| `USE_CLIENT_SSL` | | `false` | Enables SSL (with client certificates) for thin clients; requires the key/trust stores and `SSL_CLIENT_CERTIFICATE_*`. |
| `SSL_PROTOCOL` | | `TLS` | Java SSL protocol; `TLS` negotiates the highest version both sides support (TLS 1.3 on Java 17). |
| `RESTART_ON_SSL_FILE_CHANGE` | | `true` | Restart the node when its SSL files change (see [Certificate rotation](#certificate-rotation)). |
| `SSL_RESTART_MAX_JITTER_IN_SECONDS` | | `60` | Random delay (per node) added to SSL restarts. |
| `SSL_KEY_STORE_TYPE` / `SSL_TRUST_STORE_TYPE` | | `JKS` | Store types, e.g., `PKCS12` (as cert-manager creates). |
| `SSL_KEY_ALGORITHM` | | `SunX509` | Key manager algorithm. |
| `SSL_CLIENT_AUTH` | | `true` | Thin clients must present a trusted certificate (`USE_CLIENT_SSL`); without it, `SSL_CLIENT_CERTIFICATE_*` is optional. |
| `GRACEFUL_STOP_TIMEOUT_IN_SECONDS` | | `20` | Time the node may take to stop gracefully; less than `SHUTDOWN_TIMEOUT_IN_SECONDS`. |
| `SHUTDOWN_TIMEOUT_IN_SECONDS` | | `30` | Time the whole stop may take; keep it below the pod's `terminationGracePeriodSeconds`. |
| `MANAGEMENT_ENABLED` / `MANAGEMENT_PORT` | | `true` / `9464` | Health and metrics endpoints (see [Management](#management-health-and-metrics)). |
| `ENABLE_OFF_HEAP_METRICS` | | `true` | Data region metrics. |
| `METRICS_*` | | see `appsettings.json` | Ignite metrics update and log frequency. |

These settings apply only when set (otherwise the default configuration, or `-ConfigFile`, decides), as in the
[Ignite chart](https://github.com/dradoaica/helm-charts/tree/main/charts/ignite):

| Setting | Description |
|---|---|
| `JVM_MAX_METASPACE_MB` / `JVM_RESERVED_CODE_CACHE_MB` | JVM metaspace and code cache (defaults 256 / 240). |
| `JVM_MAX_DIRECT_MEMORY_MB` | `-XX:MaxDirectMemorySize` (e.g., off-heap + checkpoint buffer + system regions, see [Configuring Memory](#configuring-memory)). |
| `JVM_G1_HEAP_REGION_MB` | G1 heap region size (power of 2, 1-32). |
| `EXTRA_JVM_OPTS` | Extra JVM options, separated by spaces. |
| `EXTRA_CLASSPATH` | Extra Java classpath entries separated by `:` (a directory adds its jars), e.g., extra Ignite modules on a volume (Linux paths: `:` and `;` both separate entries). |
| `FAILURE_DETECTION_TIMEOUT_MS` / `CLIENT_FAILURE_DETECTION_TIMEOUT_MS` / `NETWORK_TIMEOUT_MS` | Timeouts (defaults 30000 / 60000 / 10000); the client one must not be less than `METRICS_UPDATE_FREQUENCY_IN_SECONDS`. |
| `PUBLIC_THREAD_POOL_SIZE`, `SYSTEM_THREAD_POOL_SIZE`, `QUERY_THREAD_POOL_SIZE`, `SERVICE_THREAD_POOL_SIZE`, `STRIPED_THREAD_POOL_SIZE`, `DATA_STREAMER_THREAD_POOL_SIZE` | Thread pool sizes. |
| `STORAGE_PATH` / `WAL_PATH` / `WAL_ARCHIVE_PATH` | Data and indexes, WAL and WAL archive directories (dedicated volumes recommended); default under `/app/work`. |
| `WAL_COMPACTION_ENABLED` / `WAL_MODE` | WAL archive compaction; WAL mode (`Fsync`, `LogOnly`, `Background`, `None`). |

Invalid settings are reported at startup (exit code `1`).

The keystores in `config/` (`server.jks`, `trust.jks`, `thin-client-cert.pfx`, password `123456`) are **samples for
local development and tests only**: they are copied to the output of Debug builds (and used by `launchSettings.json`),
but are not part of Release builds or the Docker image. Mount your own certificates and point the `SSL_*` settings at
them; `scripts/ssl/ssl.ps1` shows how such a set (CA, key store, trust store, client certificate) is generated.

### Management (health and metrics)

With `MANAGEMENT_ENABLED` (the default), the server listens on `MANAGEMENT_PORT` (9464):

| Endpoint | |
|---|---|
| `/health/liveness` | `200` while the process runs (liveness probe). |
| `/health/startup` | `200` once the node runs, also while it waits for activation (startup probe). |
| `/health/readiness` | `200` when the node is running, its cluster is active and it is not rebalancing (`READY_WHEN_REBALANCED`); `503` while starting, waiting for activation, rebalancing, restarting or stopping (readiness probe). |
| `/metrics` | Prometheus metrics: `ignite_node_*` (heap, CPU, threads, jobs, traffic), `ignite_cluster_*` (active, server and baseline nodes), `ignite_data_region_*` (off-heap, RAM, checkpoint buffer, dirty pages, fill factor, evictions; by `region`), `ignite_cache_local_entries` and `ignite_cache_rebalancing` (by `cache`), `ignite_node_rebalancing_caches`, the health checks, and the .NET runtime. The node is read once per scrape. |

### Logging

Logs go to the console (Serilog, `serilog.config.json`, level `Information`); the Docker image writes them as JSON
lines (`RenderedCompactJsonFormatter`). Other sinks can be added with the usual Serilog settings, e.g., a file:
`Serilog__WriteTo__1__Name=File`, `Serilog__WriteTo__1__Args__path=/app/work/logs/log-.log`,
`Serilog__WriteTo__1__Args__rollingInterval=Day`.

### Network security

Ignite 2 authenticates thin clients, JDBC and REST, not nodes: any host that reaches the discovery (47500) and
communication (47100) ports can join the cluster. Enable `USE_SSL` (nodes then need a certificate trusted by the trust
store) and/or restrict these ports to the cluster's nodes; the server warns when `USE_SSL` is off. Peer assembly loading
(`PEER_ASSEMBLY_LOADING`) lets any node run code on the others, so it is off by default.

### Authentication

With authentication enabled, every start makes `IGNITE_USER_PASSWORD` the password of the `ignite` user: nothing
happens when it is already accepted; otherwise it replaces `IGNITE_USER_PREVIOUS_PASSWORD` (to rotate the password,
set both) or the default password `ignite` (first start). If none of them is accepted, an error is logged. Without
`IGNITE_USER_PASSWORD` the well-known default password is kept and a warning is logged.

### Certificate rotation

When SSL is enabled, the key store, trust store and client certificate files are watched (by polling, so changes of
mounted Kubernetes secrets are seen too). Once they have not changed for 10 seconds, plus a random delay of up to
`SSL_RESTART_MAX_JITTER_IN_SECONDS`, the node restarts gracefully and picks up the renewed certificates.

The jitter spreads the restarts of nodes whose certificates are renewed at the same time (e.g., by cert-manager), but
it does not guarantee that they do not overlap. For in-memory clusters, where nodes restarting together lose data,
prefer rolling restarts by Kubernetes (e.g., [Reloader](https://github.com/stakater/Reloader)) and set
`RESTART_ON_SSL_FILE_CHANGE=false`.

## Client library (`AspNetCore.Ignite`)

```csharp
services.AddIgnite();                                         // the ASPNETCORE_IGNITE_* settings below
services.AddIgnite(configuration.GetSection("Ignite"));       // or a section bound to IgniteClientOptions
services.AddIgnite(options => options.Cache.Backups = 2);     // and/or code
services.AddHealthChecks().AddIgnite();                       // healthy when the cluster is reachable and active
```

`IIgniteManager` connects on first use (with retries), not when the service is resolved; concurrent callers share one
connection attempt (`GetClientAsync` waits for it without blocking a thread, which the health check relies on). It
creates and destroys caches and exposes the thin client (`IIgniteManager.Client`) for SQL, compute, transactions, etc.
The client logs through `Microsoft.Extensions.Logging` (category `Apache.Ignite`). For LINQ queries, add the
`Apache.Ignite.Linq` package.

| Setting | `IgniteClientOptions` | Default | Description |
|---|---|---|---|
| `ASPNETCORE_IGNITE_ENDPOINT` | `Endpoints` | `127.0.0.1` | Thin client endpoints (comma-separated). |
| `ASPNETCORE_IGNITE_USER_NAME` / `ASPNETCORE_IGNITE_PASSWORD` | `UserName` / `Password` | _(empty)_ | Credentials. |
| `ASPNETCORE_IGNITE_USE_SSL` | `UseSsl` | `false` | Enables SSL (the OS chooses the protocol, TLS 1.3 included). |
| `ASPNETCORE_IGNITE_SSL_CERTIFICATE_PATH` / `ASPNETCORE_IGNITE_SSL_CERTIFICATE_PASSWORD` | `SslCertificatePath` / `SslCertificatePassword` | _(empty)_ | Client certificate. |
| `ASPNETCORE_IGNITE_SSL_SKIP_SERVER_CERTIFICATE_VALIDATION` | `SslSkipServerCertificateValidation` | `false` | Skips server certificate validation (insecure, testing only). |
| `ASPNETCORE_IGNITE_SSL_CHECK_CERTIFICATE_REVOCATION` | `SslCheckCertificateRevocation` | `true` | Checks the revocation of the server certificate. |
| | `RetryLimit`, `SocketTimeout` | `5`, `30 s` | Thin client retries of read operations and socket timeout. |
| | `ConnectRetryCount`, `OperationRetryCount` | `6`, `3` | Retries (exponential backoff) of connecting and of transient operation failures. |
| | `Cache.Backups` | `1` | Backups of created caches. |
| | `Cache.EnablePlatformCache` | `false` | Keeps a deserialized copy of every entry in the .NET memory of the (.NET) server nodes: faster reads there, but roughly double the memory per entry. |
| | `Cache.GroupByValueType` | `true` | Puts the caches of the same value type into one cache group (shared partitions and storage). |

## Examples

* Runs the server locally with the specified amount of memory. It uses 1G of on-heap memory (for computation and
  queries) and 2G of off-heap memory (data and indexes). It uses default settings, e.g., the Spi port will be the first
  available port starting from 47500.

```
dotnet AspNetCore.IgniteServer.dll -OnHeap 1024 -OffHeap 2048 -Cluster "127.0.0.1:47500"
```

  `scripts/ignite-run-inmem.cmd` runs this from the build output directory on Windows.

## Persistent Mode

If the server is run in persistent mode, the situation is slightly more complicated and depends on whether the cluster
is run for the first time or it is resumed. It is because, for running a cluster with persistence enabled, Ignite needs
to form a topology before the cluster is activated.
If the cluster is created for the first time, the cluster has to be activated when it reaches the required topology. The
cluster records the topology information, which is used when the cluster is resumed.

The server does this automatically: a new persistent cluster is activated once `ACTIVATION_MIN_SERVER_NODES` server
nodes have joined (set it to the initial cluster size), while a resumed cluster that is already active is left as is.
Afterward, baseline auto-adjust (`BASELINE_AUTO_ADJUST_TIMEOUT_IN_SECONDS`) keeps the baseline in sync with the
topology.

In containers, mount a volume at `/app/work` (persistence and WAL) and give every node a unique, stable
`DEFAULT_CONSISTENT_ID`.

## Configuring Memory

```
memory calculation: ON_HEAP_MEMORY (JVM Heap max size)
    + 256MB (JVM Metaspace max size)
    + 240MB (JVM ReservedCodeCache size)
    + OFF_HEAP_MEMORY (default region max size; data + indexes (basic use cases will add a 30% increase on top of data))
    + MIN(256MB, OFF_HEAP_MEMORY) (OFF_HEAP_MEMORY < 1 GB)
        || OFF_HEAP_MEMORY/4 (OFF_HEAP_MEMORY between 1 GB and 8 GB)
        || 2GB (OFF_HEAP_MEMORY > 8 GB) 
            (default region checkpointing buffer size)
    + 100MB (sysMemPlc region max size) 
    + 100MB (metastoreMemPlc region max size) 
    + 100MB (TxLog region max size) 
    + 100MB (volatileDsMemPlc region max size)
    + ~512MB (other overheads; e.g., thread stacks, GC, symbols, etc.)
```

Caches created with `Cache.EnablePlatformCache` also keep their entries in the .NET heap of each server node.

## Java modules

The Ignite core jars come with the `Apache.Ignite` package. The optional modules the server uses (Kubernetes discovery,
REST API and JSON, with the runtime dependencies their Maven POMs declare) are listed in
`src/AspNetCore.IgniteServer/JavaLibs.props`: the build downloads them from Maven Central, verifies their SHA-256 and
copies them to `libs/` (not in IDE design-time builds). When upgrading Ignite (`IgniteVersion` in
`Directory.Packages.props`), run `scripts/update-java-libs.sh` to update the hashes.

## Tests

```
dotnet test tests/AspNetCore.Ignite.UnitTests
dotnet test tests/AspNetCore.Ignite.IntegrationTests
./scripts/check-vulnerable-packages.sh
```

The integration tests build the server image from the `Dockerfile` and run it with Docker (via Testcontainers):
authentication, the client library and its health check, SSL with client certificates and certificate rotation, a
two-node cluster activation, the management endpoints, REST, the shipped configuration files, and the exit codes. They are skipped when
Docker is not available. CI reports the unit test coverage in the job summary, and tests the image with its
[Helm chart](https://github.com/dradoaica/helm-charts/tree/main/charts/aspnetcore-ignite-server): a two-node persistent
cluster in kind, checked by the chart's probes and `helm test` (the `helm-charts-ref` input selects the chart revision).
A release opens a pull request on the chart repository that moves it to the released image (with a
`HELM_CHARTS_TOKEN` secret).

## Build aspnetcore.ignite-server

The image is based on the ASP.NET Core runtime image with a Java 17 runtime reduced to the modules Ignite needs
(`jlink`, from the Temurin JDK), and is published for `linux/amd64` and `linux/arm64` (each built natively), with SBOM
and provenance attestations. `--build-arg VERSION=...` sets the assembly version (the release workflow passes the tag).

```
docker build -t dradoaica/aspnetcore.ignite-server:2.18.1 -f Dockerfile .
docker buildx build --platform linux/amd64,linux/arm64 -t dradoaica/aspnetcore.ignite-server:2.18.1 -f Dockerfile .
```

## Push aspnetcore.ignite-server

docker push dradoaica/aspnetcore.ignite-server:2.18.1

## Run aspnetcore.ignite-server container

docker run -p 0.0.0.0:10800:10800/tcp --name aspnetcore.ignite-server -d dradoaica/aspnetcore.ignite-server:2.18.1

## Remove aspnetcore.ignite-server container

docker rm -f aspnetcore.ignite-server

## Remove aspnetcore.ignite-server image

docker rmi dradoaica/aspnetcore.ignite-server:2.18.1

## Kubernetes

The [Helm chart](https://github.com/dradoaica/helm-charts/tree/main/charts/aspnetcore-ignite-server) runs the server as
a StatefulSet. A deployment should:

* probe `/health/startup` (startup), `/health/liveness` and `/health/readiness` on port 9464 (and scrape `/metrics`),
* start the pods in parallel (`podManagementPolicy: Parallel`) when `ACTIVATION_MIN_SERVER_NODES` is above 1: with
  `OrderedReady`, the first pod is never ready (its cluster waits for the others) and the others never start,
* size the stop for real data: a node's graceful stop waits until its partitions have copies on other nodes, so set
  `GRACEFUL_STOP_TIMEOUT_IN_SECONDS` (e.g., 240) below `SHUTDOWN_TIMEOUT_IN_SECONDS` (e.g., 270) below
  `terminationGracePeriodSeconds` (e.g., 300); together with `READY_WHEN_REBALANCED`, a rolling restart moves on only
  once the restarted node holds its data again,
* add a PodDisruptionBudget (`maxUnavailable: 1`) so that drains evict one node at a time,
* give each pod a unique, stable `DEFAULT_CONSISTENT_ID` (e.g., the pod name) and, with persistence, a volume at
  `/app/work`,
* set `ACTIVATION_MIN_SERVER_NODES` to the initial number of replicas (persistence); until they joined, no pod is
  ready, so the Service has no endpoints and thin clients cannot connect yet (by design),
* enable `USE_SSL` and/or restrict the cluster ports with a NetworkPolicy:

```yaml
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: aspnetcore-ignite-server
spec:
  podSelector:
    matchLabels:
      app.kubernetes.io/name: aspnetcore-ignite-server
  policyTypes: [Ingress]
  ingress:
    # discovery and communication: the cluster's own pods only
    - from:
        - podSelector:
            matchLabels:
              app.kubernetes.io/name: aspnetcore-ignite-server
      ports:
        - port: 47500
        - port: 47100
    # thin clients and management (narrow "from" to the client and monitoring namespaces)
    - ports:
        - port: 10800
        - port: 9464
```

## Upgrading to 2.18.1

* SSL (`USE_SSL`, `USE_CLIENT_SSL`) is off by default, there are no default passwords or consistent id, and the image
  no longer contains the sample keystores: set these explicitly.
* The client validates the server certificate by default (`ASPNETCORE_IGNITE_SSL_SKIP_SERVER_CERTIFICATE_VALIDATION`
  to opt out), and platform cache is off by default (`Cache.EnablePlatformCache` to opt in).
* `IIgniteManager` has new `Client` and `GetClientAsync` members (implementations outside this library must add them),
  and the library no longer depends on `Apache.Ignite.Linq` (add it for LINQ queries).
* The server image no longer contains the Ignite binary distribution (`IGNITE_HOME`): the jars are in `/app/libs`.
* REST (8080) and the TCP binary protocol (11211) are off (`REST_ENABLED`), peer assembly loading is off
  (`PEER_ASSEMBLY_LOADING`), and OpenCensus metrics are replaced by `/metrics` on port 9464 (`MANAGEMENT_PORT`).
* Logs go to the console only (JSON in the image); the file sink is opt-in.
* SSL restarts are graceful and jittered; the process exits with `1` on failures (it always exited with `0`).
