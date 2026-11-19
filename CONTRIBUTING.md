# Contributing to AspNetCore.Ignite

Thank you for your interest in contributing to AspNetCore.Ignite! Contributions of all sizes are welcome — bug reports,
documentation fixes, new features and version upgrades.

## Repository Layout

| Path                                       | Description                                                                                        |
|--------------------------------------------|----------------------------------------------------------------------------------------------------|
| `src/AspNetCore.Ignite`                    | `net10.0` library published as a NuGet package (thin client, DI extensions, health check)          |
| `src/AspNetCore.IgniteServer`              | `net10.0` host running Apache Ignite in embedded mode (settings, Spring XML configs, Java modules) |
| `tests/AspNetCore.Ignite.UnitTests`        | xUnit tests that need neither Docker nor a JVM (the node lifecycle runs on fake nodes)             |
| `tests/AspNetCore.Ignite.IntegrationTests` | xUnit tests that build the server image and run it with Docker (Testcontainers)                    |
| `scripts`                                  | CI logic that also runs locally, the chart test, Java module updates, SSL key store generation     |
| `docs`                                     | Configuration, client library, Kubernetes, architecture, development and upgrade notes             |
| `Dockerfile`                               | Server image (ASP.NET Core runtime + a Java 17 runtime reduced with `jlink`)                       |
| `.github/workflows`                        | Tests, Release (NuGet package and image) and Ignite Upgrade workflows                              |

[Architecture](docs/architecture.md) describes the components and has a "Where to change what" table;
[Development](docs/development.md) covers the Java modules, the tests and releases in detail.

## Prerequisites

- [.NET SDK 10](https://dotnet.microsoft.com/download) (see `global.json`)
- Docker, for the integration tests and for building the server image (locally, the tests are skipped without it)
- Java 17 with `JAVA_HOME` set, only to run the server outside Docker: Apache Ignite runs on the JVM, even when hosted
  from .NET

## Building and Testing

```bash
dotnet build AspNetCore.Ignite.slnx
dotnet test tests/AspNetCore.Ignite.UnitTests
dotnet test tests/AspNetCore.Ignite.IntegrationTests
./scripts/check-vulnerable-packages.sh
```

The integration tests start their own server containers; no local node is needed. In CI (`CI=true`), warnings fail the
build, so check with `CI=true dotnet build AspNetCore.Ignite.slnx` before pushing.

To run the server locally (with Java 17):

```bash
cd src/AspNetCore.IgniteServer/bin/Debug/net10.0
dotnet AspNetCore.IgniteServer.dll -OnHeap 1024 -OffHeap 2048 -Cluster "127.0.0.1:47500"
```

or build and run the image:

```bash
docker build -t aspnetcore.ignite-server:dev -f Dockerfile .
docker run --rm -p 10800:10800 -p 9464:9464 -e IGNITE_USER_PASSWORD='<password>' aspnetcore.ignite-server:dev
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
5. Update the docs if you change settings, CLI options, ports or Docker usage: `docs/configuration.md` for the server,
   `docs/client.md` for the client library, and `docs/upgrading.md` for breaking changes.
6. After rewording a log message that the integration tests wait for, update
   `tests/AspNetCore.Ignite.IntegrationTests/ServerLog.cs` (a unit test checks the list against the code).
7. Never commit real certificates, key stores, passwords or other secrets.

## Contribution Guidelines

- **Upgrading Apache Ignite.** The project version follows the Apache Ignite version. An upgrade must keep these in
  sync:
    - `IgniteVersion` in `Directory.Packages.props` (the `Apache.Ignite` package and the Java modules)
    - the Java modules in `src/AspNetCore.IgniteServer/JavaLibs.props`: run `scripts/update-java-libs.py`
    - `<Version>` in `src/Directory.Build.props` (the package and the image are released together)
    - the version numbers in `README.md`, `SECURITY.md` and `docs/upgrading.md`
    - the Ignite version recorded by the Helm chart (see [Development](docs/development.md#java-modules))
- **Java dependencies.** Java modules come from Maven Central, as the POMs of the Ignite modules declare them, and are
  verified by SHA-256. An `Override` attribute keeps a newer version (e.g., for a fixed vulnerability): say why in it.
- **Library compatibility.** `AspNetCore.Ignite` targets .NET 10 (`net10.0`); don't raise the target framework or add
  heavy dependencies without discussing it first.
- **Configuration.** New server settings should be configurable through `appsettings.json` and environment variables (so
  they work in Docker and Kubernetes), with safe, backward-compatible defaults, and validated in
  `IgniteServerSettings.Validate`.
- **Reliability and performance.** Changes to clustering, discovery, persistence or memory settings should explain the
  rationale and, where possible, include test scenarios, benchmarks or metrics.

## Code of Conduct

This project follows the [Contributor Covenant Code of Conduct](CODE_OF_CONDUCT.md). By participating, you are expected
to uphold it.
