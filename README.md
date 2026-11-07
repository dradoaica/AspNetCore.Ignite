# AspNetCore.IgniteServer (extracted from [Tarzan](https://github.com/awesomedotnetcore/Tarzan) and improved)

AspNetCore.IgniteServer is an implementation of Apache Ignite 2 embedded mode that references necessary assemblies
to provide a server-side component for the ASP.NET Core platform. Kubernetes ready, authentication and SSL enabled with
auto-discovery clustering capabilities. Usually, every node runs one or more instances of the server.

## Usage

The server can be executed with various options:

```
dotnet AspNetCore.IgniteServer.dll [options]

Options:
  -?|-Help       Show help information
  -ConfigFile    XML configuration file. If no file is specified then the default configuration is used.
  -OffHeap       Size of off-heap memory given in megabytes.
  -OnHeap        Size of on-heap memory given in megabytes.
  -SpiPort       Specifies port for Discovery Spi.
  -Cluster       Specifies IP address and port of a cluster node. Multiple nodes can be specified.
  -ConsistentId  Specifies as a consistent id of the node. This value is used in topology.
  -PersistenceEnabled  If set, it enables persistence mode.
```

The default configuration uses the default parameters of the Ignite environment.
There are two predefined configuration files available in `config` folder:

* `default-config.xml` - this is minimal configuration for running memory only Ignite server.
* `persistent-config.xml` - this is minimal configuration for running persistent Ignite server.

## Configuration

Settings are read from `appsettings.json` and can be overridden with environment variables of the same name.

| Setting | Default | Description |
|---|---|---|
| `DEFAULT_ON_HEAP_MEMORY` / `DEFAULT_OFF_HEAP_MEMORY` | `1024` / `2048` | Memory (MB) used when `-OnHeap` / `-OffHeap` are not given. |
| `DEFAULT_CONSISTENT_ID` | _(empty)_ | Consistent id used when `-ConsistentId` is not given. Must be **unique per node**; leave empty to let Ignite derive one. |
| `USE_TCP_DISCOVERY_STATIC_IP_FINDER` | `true` | Use the local address as the static discovery endpoint when `-Cluster` is not given. |
| `K8S_NAMESPACE` / `K8S_SERVICE_NAME` | _(empty)_ | Kubernetes IP finder settings. |
| `ENABLE_AUTHENTICATION` | `true` | Enables authentication (implies persistence). |
| `IGNITE_USER_PASSWORD` | _(empty)_ | New password of the default `ignite` user. If empty, the well-known default password is kept and a warning is logged. |
| `ACTIVATION_MIN_SERVER_NODES` | `1` | Persistent clusters are activated only after this many server nodes joined (see [Persistent Mode](#persistent-mode)). |
| `AUTO_RESET_LOST_PARTITIONS` | `true` | Reset lost partitions automatically so caches stay usable. **The lost data is not recovered**; data loss is always logged as an error. |
| `USE_SSL` | `false` | Enables SSL between nodes; requires `SSL_KEY_STORE_*` and `SSL_TRUST_STORE_*`. |
| `USE_CLIENT_SSL` | `false` | Enables SSL for thin clients; requires the key/trust stores and `SSL_CLIENT_CERTIFICATE_*`. |
| `METRICS_*`, `ENABLE_OFF_HEAP_METRICS` | see `appsettings.json` | Metrics settings. |

When SSL is enabled the key store, trust store and client certificate files are watched, and the node restarts
(after a 10 seconds debounce) when they change, so rotated certificates are picked up.

The keystores in `config/` (`server.jks`, `trust.jks`, `thin-client-cert.pfx`, password `123456`) are **samples for
local development only**: they are copied to the output of Debug builds (and used by `launchSettings.json`), but are
not part of Release builds or the Docker image. Mount your own certificates and point the `SSL_*` settings at them.

### Client library (`AspNetCore.Ignite`)

`services.AddIgnite()` registers `IIgniteManager`, configured with:

| Setting | Default | Description |
|---|---|---|
| `ASPNETCORE_IGNITE_ENDPOINT` | `127.0.0.1` | Thin client endpoint. |
| `ASPNETCORE_IGNITE_USER_NAME` / `ASPNETCORE_IGNITE_PASSWORD` | _(empty)_ | Credentials. |
| `ASPNETCORE_IGNITE_USE_SSL` | `false` | Enables SSL. |
| `ASPNETCORE_IGNITE_SSL_CERTIFICATE_PATH` / `ASPNETCORE_IGNITE_SSL_CERTIFICATE_PASSWORD` | _(empty)_ | Client certificate. |
| `ASPNETCORE_IGNITE_SSL_SKIP_SERVER_CERTIFICATE_VALIDATION` | `false` | Skips server certificate validation (insecure, testing only). |
| `ASPNETCORE_IGNITE_SSL_CHECK_CERTIFICATE_REVOCATION` | `true` | Checks the revocation of the server certificate. |

The connection is established on first use (with retries), not when the service is resolved.

## Examples

* Runs the server locally with the specified amount of memory. It uses 1G of on-heap memory (for computation and
  queries) and 2G of off-heap memory (data and indexes). It uses default settings, e.g., the Spi port will be the first
  available port starting from 47500.

```
dotnet AspNetCore.IgniteServer.dll  -Onheap 1024 -Offheap 2048 -Cluster "127.0.0.1:47500"
```

## Persistent Mode

If the server is run in persistent mode, the situation is slightly more complicated and depends on whether the cluster
is run for the first time or it is resumed. It is because, for running a cluster with persistence enabled, Ignite needs
to form a topology before the cluster is activated.
If the cluster is created for the first time, the cluster has to be activated when it reaches the required topology. The
cluster records the topology information, which is used when the cluster is resumed.

The server does this automatically: a new persistent cluster is activated once `ACTIVATION_MIN_SERVER_NODES` server
nodes have joined (set it to the initial cluster size), while a resumed cluster that is already active is left as is.
Afterward, baseline auto-adjust (30 seconds) keeps the baseline in sync with the topology.

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

## Build aspnetcore.ignite-server

docker build -t dradoaica/aspnetcore.ignite-server:2.18.1 -f Dockerfile .

## Push aspnetcore.ignite-server

docker push dradoaica/aspnetcore.ignite-server:2.18.1

## Run aspnetcore.ignite-server container

docker run -p 0.0.0.0:10800:10800/tcp --name aspnetcore.ignite-server -d dradoaica/aspnetcore.ignite-server:2.18.1

## Remove aspnetcore.ignite-server container

docker rm -f aspnetcore.ignite-server

## Remove aspnetcore.ignite-server image

docker rmi dradoaica/aspnetcore.ignite-server:2.18.1

## Helm Chart

https://github.com/dradoaica/helm-charts/tree/main/charts/aspnetcore-ignite-server
