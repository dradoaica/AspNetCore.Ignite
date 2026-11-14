# AspNetCore.IgniteServer (extracted from [Tarzan](https://github.com/awesomedotnetcore/Tarzan) and improved)

AspNetCore.IgniteServer is an implementation of Apache Ignite 2 embedded mode that references necessary assemblies
to provide a server-side component for the ASP.NET Core platform. Kubernetes ready, authentication and SSL enabled with
auto-discovery clustering capabilities. Usually, every node runs one or more instances of the server.

The repository also contains `AspNetCore.Ignite`, a thin client library for ASP.NET Core applications (see
[Client library](docs/client.md)).

This version (2.19.0) runs Apache Ignite 2.18.0 until Apache Ignite 2.19.0 is released.

## Quick start

```
docker run -d --name aspnetcore.ignite-server -p 10800:10800 -p 9464:9464 \
  -e IGNITE_USER_PASSWORD='<password>' ghcr.io/dradoaica/aspnetcore.ignite-server:2.19.0
curl http://localhost:9464/health/readiness
```

Thin clients connect to port 10800 (user `ignite`); `/metrics` on port 9464 serves Prometheus metrics.

## Documentation

* [Configuration](docs/configuration.md): command line options, settings, authentication, SSL and certificate rotation,
  health and metrics, the control utility and snapshots, logging, network security, persistence and memory
* [Client library](docs/client.md): `AspNetCore.Ignite` for ASP.NET Core applications
* [Kubernetes](docs/kubernetes.md): deployment guidance, alerts and the
  [Helm chart](https://github.com/dradoaica/helm-charts/tree/main/charts/aspnetcore-ignite-server)
* [Architecture](docs/architecture.md): the components of the server and the client library, the node lifecycle,
  configuration, certificate rotation and exit codes, with diagrams
* [Development](docs/development.md): Java modules, tests, building the image
* [Upgrading to 2.19.0](docs/upgrading.md)
