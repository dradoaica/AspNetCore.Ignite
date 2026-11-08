# Development

## Java modules

The Ignite core jars come with the `Apache.Ignite` package. The optional modules the server uses (Kubernetes discovery,
REST API, JSON and the control utility, with the runtime dependencies their Maven POMs declare) are listed in
`src/AspNetCore.IgniteServer/JavaLibs.props`: the build downloads them from Maven Central, verifies their SHA-256 and
copies them to `libs/` (not in IDE design-time builds). When upgrading Ignite (`IgniteVersion` in
`Directory.Packages.props`), run `scripts/update-java-libs.sh`: it derives the dependencies of the Ignite modules listed
there from their POMs again (without what the package contains) and updates the hashes.

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

The integration tests build the server image from the `Dockerfile` and run it with Docker (via Testcontainers):
authentication and password rotation, the client library and its health check, SSL with client certificates and
certificate rotation, cluster activation, rebalancing and lost partitions (in-memory and persistent), the management
endpoints, REST, the shipped configuration files, and the exit codes. They are skipped when Docker is not available.
CI reports the unit test coverage and the image's vulnerabilities with a fix (Trivy; a critical one fails the build) in
the job summary, and tests the image with its
[Helm chart](https://github.com/dradoaica/helm-charts/tree/main/charts/aspnetcore-ignite-server): a two-node persistent
cluster in kind, checked by the chart's probes and `helm test` (the `helm-charts-ref` input selects the chart revision).
A release opens a pull request on the chart repository that moves it to the released image (with a
`HELM_CHARTS_TOKEN` secret). Run by hand (Run workflow), the Release workflow is a dry run: it builds the package and
the image for every platform, and publishes nothing.

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
