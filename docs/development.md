# Development

## Java modules

The Ignite core jars come with the `Apache.Ignite` package. The optional modules the server uses (Kubernetes discovery,
REST API, JSON and the control utility, with the runtime dependencies their Maven POMs declare) are listed in
`src/AspNetCore.IgniteServer/JavaLibs.props`: the build downloads them from Maven Central, verifies their SHA-256 and
copies them to `libs/` (not in IDE design-time builds). When upgrading Ignite (`IgniteVersion` in
`Directory.Packages.props`), run `scripts/update-java-libs.sh`: it derives the dependencies of the Ignite modules listed
there from their POMs again (without what the package contains) and updates the hashes. An entry with an `Override`
attribute keeps its version (e.g., Jackson 2.21.7 instead of the 2.19.0 Ignite declares, for its fixed vulnerabilities).
The chart records the Ignite version of its image (the `ignite.apache.org/version` annotation of its `Chart.yaml`) and
refuses rolling upgrades across Ignite minor versions: keep it equal to `IgniteVersion` on the chart's release branch.
The Release workflow sets it in the pull requests it opens, and fails when the chart of the released version records
another one.

Dependabot bumps `Apache.Ignite`; the Ignite Upgrade workflow then runs the script on its branch and pushes the updated
modules. Dependabot stops rebasing a pull request once someone else pushed to it (merge or close it, and let it open a
new one if needed), and a push with `GITHUB_TOKEN` starts no workflows: re-run the Tests workflow of the pull request,
or set a `DEPENDABOT_PUSH_TOKEN` Dependabot secret (a token with `contents: write`) whose pushes do start them.

## Tests

```
dotnet test tests/AspNetCore.Ignite.UnitTests
dotnet test tests/AspNetCore.Ignite.IntegrationTests
./scripts/check-vulnerable-packages.sh
```

The unit tests need neither Docker nor a JVM. The node lifecycle (the runner, the hosted service, the SSL restarts and
the metrics) runs on fake nodes of `FakeNodeLauncher`, whose stops a test can hold back to reproduce the orderings that
the integration tests cannot time; `Fake` answers the calls of any other Ignite interface by member name.

The integration tests build the server image from the `Dockerfile` and run it with Docker (via Testcontainers):
authentication and password rotation, the client library and its health check, SSL with client certificates and
certificate rotation, cluster activation, rebalancing and lost partitions (in-memory and persistent), the management
endpoints, the control utility, REST, the shipped configuration files, and the exit codes. Two collections run in
parallel: the tests of one shared node, and those that start clusters of their own. They are skipped when Docker is not
available.
CI reports the unit test coverage and the image's vulnerabilities with a fix (Trivy; a critical one fails the build) in
the job summary, and tests the image with its
[Helm chart](https://github.com/dradoaica/helm-charts/tree/main/charts/aspnetcore-ignite-server): a two-node persistent
cluster in kind, checked by the chart's probes and `helm test` (the `helm-charts-ref` input selects the chart revision).
The logic of the workflows lives in scripts that also run locally: `scripts/test-chart.sh` (the chart test, against any
cluster that has the image; its header shows how to load the image into kind or microk8s) and `scripts/ci/` (which chart
revision is tested, the paired versions check, the chart update of a release, and `check-docs.sh`, which checks that
the names of [Architecture](architecture.md)'s "Where to change what" table exist and, with `--diagrams`, that its
Mermaid diagrams parse).

The integration tests wait for texts in the server's logs. Those texts are listed in
`tests/AspNetCore.Ignite.IntegrationTests/ServerLog.cs`, and a unit test fails when one of them is no longer part of a
message in the server's code: after rewording a log message, update that list.

Run by hand (Run workflow), the Release workflow is a dry run: it builds the package and the image for every
platform, and publishes nothing.

## Release

The image comes first: the chart repository publishes a chart on every push to its `main`, and its pull requests
install the chart with its image.

1. Merge the release branch of this repository into `main`.
2. Publish a GitHub release whose tag is the version (e.g., `2.19.0`). The Release workflow tests once more, and
   publishes the package (to GitHub Packages, and to nuget.org with a `NUGET_API_KEY` secret) and the image (also
   tagged `2.19`, `2` and `latest`). For a patch release of the image, it also opens a pull request on the chart
   repository that moves the chart to it (with a `HELM_CHARTS_TOKEN` secret): on the chart's branch of the same version
   if there is one, otherwise `main`; a new minor version of the image needs the chart released with it.
3. Merge the chart's release branch (e.g., `release/2.19.0`) into the `main` of the chart repository, which publishes
   the chart.

## Build aspnetcore.ignite-server

The image is based on the ASP.NET Core runtime image with a Java 17 runtime reduced to the modules Ignite needs
(`jlink`, from the Temurin JDK), and is published for `linux/amd64` and `linux/arm64` (each built natively), with SBOM
and provenance attestations. `--build-arg VERSION=...` sets the assembly version (the release workflow passes the tag).

```
docker build -t ghcr.io/dradoaica/aspnetcore.ignite-server:2.19.0 -f Dockerfile .
docker buildx build --platform linux/amd64,linux/arm64 -t ghcr.io/dradoaica/aspnetcore.ignite-server:2.19.0 -f Dockerfile .
```

## Push aspnetcore.ignite-server

Releases publish the image to GHCR (the Release workflow); to push one by hand:

docker push ghcr.io/dradoaica/aspnetcore.ignite-server:2.19.0

## Run aspnetcore.ignite-server container

docker run -p 0.0.0.0:10800:10800/tcp --name aspnetcore.ignite-server -d ghcr.io/dradoaica/aspnetcore.ignite-server:
2.19.0

## Remove aspnetcore.ignite-server container

docker rm -f aspnetcore.ignite-server

## Remove aspnetcore.ignite-server image

docker rmi ghcr.io/dradoaica/aspnetcore.ignite-server:2.19.0
