# AspNetCore.IgniteServer (extracted from [Tarzan](https://github.com/awesomedotnetcore/Tarzan) and improved)

AspNetCore.IgniteServer.dll is an implementation of Ignite server that references necessary assemblies
to provide server-side component for AspNetCore platform. Kubernetes ready, authentication and SSL enabled. Usually,
every node runs one or more instances
of the server.

## Usage

The server can be executed with various options:

```
dotnet AspNetCore.IgniteServer.dll [options]

Options:
  -?|-Help       Show help information
  -ConfigFile    XML configuration file. If not specified then default configuration is used.
  -OffHeap       Size of off-heap memory given in megabytes.
  -OnHeap        Size of on-heap memory given in megabytes.
  -SpiPort       Specifies port for Discovery Spi.
  -Cluster       Specifies IP address and port of a cluster node. Multiple nodes can be specified.
  -ConsistentId  Specifies as a consistent id of the node. This value is used in topology.
```

The default configuration uses the default parameters of the Ignite environment.
There are two predefined configuration files available in ```config``` folder:

* `default-config.xml` - this is minimal configuration for running memory only Ignite server.


* `persistent-config.xml` - this is minimal configuration for running persistent Ignite server.

## Examples

* Runs the server locally with the specified amount of memory. It uses 1G of heap memory (for computation and queries)
  and 2G
  of off heap memory (data storage). It uses default settings, e.g., the Spi port will be the first available port
  starting from 47500.

```
dotnet AspNetCore.IgniteServer.dll  -Onheap 1024 -Offheap 2048 -Cluster "127.0.0.1:47500"
```

## Persistent Mode

If the server is run in persistent mode, the situation is slightly more complicated and depends on
whether the cluster is run for the first time or it is resumed. It is because, for running cluster with persistence
enabled, Ignite needs to form a topology before the cluster is activated.
If the cluster is created for the first
time, the cluster has to be activated when it reaches the required topology. The cluster records the topology
information,
which is used when the cluster is resumed.

## Configuring Memory

```
ON_HEAP_MEMORY (JVM heap max size)
    + OFF_HEAP_MEMORY (default region max size)
    + OFF_HEAP_MEMORY * 0.3 (indexes also require memory; basic use cases will add a 30% increase) 
    + MIN(256MB, OFF_HEAP_MEMORY) (OFF_HEAP_MEMORY < 1 GB)
        || OFF_HEAP_MEMORY/4 (OFF_HEAP_MEMORY between 1 GB and 8 GB)
        || 2GB (OFF_HEAP_MEMORY > 8 GB) 
            (default region checkpointing buffer size)
    + 100MB (sysMemPlc region max size) 
    + 100MB (metastoreMemPlc region max size) 
    + 100MB (TxLog region max size) 
    + 100MB (volatileDsMemPlc region max size)
```

## Build aspnetcore.ignite-server docker image

docker build -t dradoaica/aspnetcore.ignite-server:2.16 -f Dockerfile .

## Push aspnetcore.ignite-server

docker push dradoaica/aspnetcore.ignite-server:2.16

## Run aspnetcore.ignite-server container

docker run -p 0.0.0.0:10800:10800/tcp --name aspnetcore.ignite-server -d dradoaica/aspnetcore.ignite-server:2.16

## Remove aspnetcore.ignite-server container

docker rm -f aspnetcore.ignite-server

## Remove aspnetcore.ignite-server image

docker rmi dradoaica/aspnetcore.ignite-server:2.16