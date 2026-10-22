# Contributing to AspNetCore.Ignite

Thank you for your interest in contributing to AspNetCore.Ignite! Contributions of all sizes are welcome — bug reports,
documentation fixes, new features and version upgrades.

## Repository Layout

| Path                          | Description                                                                                          |
|-------------------------------|------------------------------------------------------------------------------------------------------|
| `src/AspNetCore.Ignite`       | `netstandard2.1` library published as a NuGet package (cache factory, DI extensions, Ignite helpers) |
| `src/AspNetCore.IgniteServer` | `net10.0` host running Apache Ignite in embedded mode (CLI options, Spring XML configs, Java libs)   |
| `src/AspNetCore.Ignite.Tests` | MSTest tests                                                                                         |
| `cmd`                         | Helper scripts (running a local node, generating SSL key stores)                                     |
| `Dockerfile`                  | Server image (Eclipse Temurin 17 JRE + .NET runtime + Apache Ignite binaries)                        |
| `.github/workflows`           | Release pipelines for the NuGet package and the Docker image                                         |

## Prerequisites

- [.NET SDK 10](https://dotnet.microsoft.com/download) (see `global.json`)
- Java 17 JDK/JRE with `JAVA_HOME` set — Apache Ignite runs on the JVM, even when hosted from .NET
- Docker (optional, for building and testing the server image)

## Building and Testing

```bash
dotnet build AspNetCore.Ignite.slnx
```

The tests connect to a real node through the thin client on `127.0.0.1:10800`, so start a local server first:

```bash
cd src/AspNetCore.IgniteServer/bin/Debug/net10.0
dotnet AspNetCore.IgniteServer.dll -OnHeap 1024 -OffHeap 2048 -Cluster "127.0.0.1:47500"
```

or run the Docker image (see the [README](README.md)), then:

```bash
dotnet test AspNetCore.Ignite.slnx
```

If your change touches the server, also verify that the Docker image still builds and starts:

```bash
docker build -t aspnetcore.ignite-server:dev -f Dockerfile .
docker run --rm -p 10800:10800 aspnetcore.ignite-server:dev
```

## Reporting Bugs and Suggesting Features

Open an [issue](https://github.com/dradoaica/AspNetCore.Ignite/issues) and include:

- The version of AspNetCore.Ignite (and therefore Apache Ignite) you are using
- How the server is run (locally, Docker, Kubernetes / Helm chart) and relevant configuration (`appsettings.json`
  /environment variables, config file used, persistence, SSL, authentication)
- Steps to reproduce, expected vs. actual behavior, and relevant logs

For feature suggestions, describe the use case and say whether you'd like to implement it yourself. For larger changes,
please open an issue to discuss the approach before starting work.

**Security vulnerabilities must not be reported in public issues** — follow the [Security Policy](SECURITY.md) instead.

## Pull Requests

1. Fork the repository and create a branch from `main`.
2. Keep each PR focused on a single change. Small fixes (typos, docs) don't need an issue first.
3. Follow the existing code style; it is defined in `.editorconfig`. Run `dotnet format` before committing.
4. Add or update tests where it makes sense, and make sure the solution builds without new warnings.
5. Update `README.md` if you change CLI options, configuration keys, ports or Docker usage.
6. Never commit real certificates, key stores, passwords or other secrets.

## Contribution Guidelines

- **Upgrading Apache Ignite.** The project version follows the Apache Ignite version. An upgrade must keep these in
  sync:
    - the `Apache.Ignite*` package references and `<Version>` in the `.csproj` files
    - `IGNITE_VERSION` in the `Dockerfile`
    - the Ignite jars in `src/AspNetCore.IgniteServer/libs` (and their dependencies)
    - the version numbers in `README.md`
- **Java dependencies.** Jars in `src/AspNetCore.IgniteServer/libs` should come from the matching Apache Ignite
  distribution (`libs/optional`) or Maven Central. Mention their source and version in the PR.
- **Library compatibility.** `AspNetCore.Ignite` targets `netstandard2.1`; don't raise the target framework or add
  heavy dependencies without discussing it first.
- **Configuration.** New server settings should be configurable through `appsettings.json` and environment variables (so
  they work in Docker and Kubernetes), with safe, backward-compatible defaults.
- **Reliability and performance.** Changes to clustering, discovery, persistence or memory settings should explain the
  rationale and, where possible, include test scenarios, benchmarks or metrics.

## Code of Conduct

This project follows the [Contributor Covenant Code of Conduct](CODE_OF_CONDUCT.md). By participating, you are expected
to uphold it.
