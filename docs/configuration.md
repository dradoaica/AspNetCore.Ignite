# Configuration

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

Option names are case-sensitive. Every option has a [setting](#settings) of the same meaning, so it can also be
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

## Settings

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
| `SPRING_EXTRA_BEANS_FILE` | | _(empty)_ | A Spring XML file with beans (e.g., a data source) next to the `IgniteConfiguration` bean, as `extraBeans` of the Ignite chart. |
| `SPRING_EXTRA_PROPERTIES_FILE` | | _(empty)_ | A Spring XML file with `<property>` elements of the `IgniteConfiguration` bean, as the configuration sections of the Ignite chart. Verified on a node: `userAttributes`, `binaryConfiguration`, `cacheConfiguration` (with its `keyConfiguration`), `executorConfiguration`, `transactionConfiguration`, and the collision, deployment, failover and load balancing SPIs. The .NET configuration wins for the properties it sets: `sqlConfiguration` (use `SQL_SCHEMAS`), `encryptionSpi` (use `-ConfigFile`, e.g., `<encryptionSpi type="KeystoreEncryptionSpi" .../>`), the communication and discovery SPIs, data storage and the client connector. |
| `SQL_SCHEMAS` | | _(empty)_ | SQL schemas created on start (comma-separated or `SQL_SCHEMAS__0`, ...). |
| `PEER_CLASS_LOADING_ENABLED` | | `false` | Java peer class loading (Zero Deployment); like peer assembly loading, it runs code of other nodes. |
| `PAGE_EVICTION_MODE` | | `Random2Lru` | Eviction of in-memory regions when full (`Disabled`, `RandomLru`, `Random2Lru`); ignored with persistence. |
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
| `METRICS_PER_CACHE` | | `true` | The `ignite_cache_*` metrics (one series per cache; turn off with many caches). |
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

The password belongs to the cluster: the first node that starts with a new `IGNITE_USER_PASSWORD` changes it for all
nodes, and clients still using the old one fail from then on. Change the clients' password together with the rollout
(the nodes accept the new one as soon as the first restarted node applied it).

### Certificate rotation

When SSL is enabled, the key store, trust store and client certificate files are watched (by polling, so changes of
mounted Kubernetes secrets are seen too). Once they have not changed for 10 seconds, plus a random delay of up to
`SSL_RESTART_MAX_JITTER_IN_SECONDS`, the node restarts gracefully and picks up the renewed certificates.

The jitter spreads the restarts of nodes whose certificates are renewed at the same time (e.g., by cert-manager), but
it does not guarantee that they do not overlap. For in-memory clusters, where nodes restarting together lose data,
prefer rolling restarts by Kubernetes (e.g., [Reloader](https://github.com/stakater/Reloader)) and set
`RESTART_ON_SSL_FILE_CHANGE=false`.

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

The JVM starts with its whole heap (`-Xms` = `-Xmx`, pre-touched) and the options of the Ignite chart: G1
(`MaxGCPauseMillis=200`, string deduplication), `ExitOnOutOfMemoryError` (an out-of-memory JVM ends the process, so the
container restarts instead of running half-dead) and a heap dump in `/tmp/heapdump.hprof`. `JVM_*` and `EXTRA_JVM_OPTS`
adjust them.
