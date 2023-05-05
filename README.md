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
  of off heap memory (data storage). It uses default seetings, e.g., the Spi port will be the first available port
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
