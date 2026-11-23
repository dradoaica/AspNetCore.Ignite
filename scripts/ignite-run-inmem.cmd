@rem Runs one server node from the build output directory (the example of docs/configuration.md). Each further node on
@rem this machine needs a MANAGEMENT_PORT of its own, or MANAGEMENT_ENABLED=false.
dotnet AspNetCore.IgniteServer.dll -OnHeap 1024 -OffHeap 2048 -Cluster "127.0.0.1:47500"
