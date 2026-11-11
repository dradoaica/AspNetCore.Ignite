# Upgrading to 2.19.0

2.19.0 changes defaults, settings and the image layout (below), so it is a minor version rather than a patch. It targets
Apache Ignite 2.19.0 and is built on Apache Ignite 2.18.0 until 2.19.0 is released (`IgniteVersion` in
`Directory.Packages.props`); the data format, the client protocol and the cluster are those of Ignite 2.18.0.

The published images 2.17.0 and 2.18.0 (also tagged `2.18`, `2` and `latest`) do not start: their JVM options include
`-server`, which the JVM embedded in the server rejects (`Unrecognized option: -server`, `JNI_CreateJavaVM failed`).
2.19.0 is the first image that runs; the steps below concern clusters of images built otherwise.

* SSL (`USE_SSL`, `USE_CLIENT_SSL`) is off by default, there are no default passwords or consistent id, and the image
  no longer contains the sample keystores: set these explicitly.
* The client validates the server certificate by default (`ASPNETCORE_IGNITE_SSL_SKIP_SERVER_CERTIFICATE_VALIDATION`
  to opt out), and platform cache is off by default (`Cache.EnablePlatformCache` to opt in).
* The client library targets .NET 10 (`net10.0`) instead of .NET Standard 2.1: applications on earlier .NET versions
  keep using 2.18.x.
* `IIgniteManager` has new `Client` and `GetClientAsync` members (implementations outside this library must add them),
  and the library no longer depends on `Apache.Ignite.Linq` (add it for LINQ queries).
* The client retries only transient failures, a lost connection or a cluster that is not active (yet): other errors of
  the server (e.g., an invalid cache configuration) surface at once instead of after the retries.
* The server image no longer contains the Ignite binary distribution (`IGNITE_HOME`): the jars are in `/app/libs`.
* REST (8080) and the TCP binary protocol (11211) are off (`REST_ENABLED`), peer assembly loading is off
  (`PEER_ASSEMBLY_LOADING`), and OpenCensus metrics are replaced by `/metrics` on port 9464 (`MANAGEMENT_PORT`).
* Logs go to the console only (JSON in the image); the file sink is opt-in.
* SSL restarts are graceful and jittered; the process exits with `1` on failures (it always exited with `0`).
* The settings inserted into the Spring configuration (`K8S_*` and, with `USE_CLIENT_SSL`, `SSL_*`) must not contain
  `#{`, which Spring would evaluate as an expression: the server refuses to start with one.
* Node attributes no longer contain the environment variables and Java system properties (they showed secrets such as
  `IGNITE_USER_PASSWORD` to every node and client): code that reads them, e.g., a node filter, uses `userAttributes`.
* Baseline auto-adjust waits 5 minutes with persistence (it waited 30 seconds, as in-memory clusters still do), so that
  restarted nodes keep their data: set `BASELINE_AUTO_ADJUST_TIMEOUT_IN_SECONDS` for another delay.
* Lost partitions (`AUTO_RESET_LOST_PARTITIONS`) are reset once a node that owns them is back (persistence), or after
  the departed nodes are dropped from the baseline (in-memory); the reset used to fail while their nodes were away.
* `GroupByValueType` names the cache groups of generic value types with several type arguments without assembly
  versions: new caches of such types may join another group than the existing ones, which keep theirs.
* With the Helm chart, the StatefulSet has to be recreated (its pod management policy and volume claims change). Stop
  the cluster cleanly (so the WAL is checkpointed), delete the StatefulSet, and upgrade with
  `persistence.storageVolume.claimName=persistence`, so the existing data volumes are reused, and
  `persistence.walVolume.enabled=false`, so that their WAL stays in them (a node whose data volume is mounted without
  its WAL does not recover, even after a clean stop):

  ```
  kubectl scale statefulset <fullname>-cluster --replicas=0
  kubectl delete statefulset <fullname>-cluster --cascade=orphan
  helm upgrade <release> <chart> --set persistence.storageVolume.claimName=persistence \
    --set persistence.walVolume.enabled=false ...
  ```

  The `chart-upgrade` job of the Tests workflow (`upgrade-test` input) runs these steps with data. The chart's values
  also change:
    * The probes use `/health/startup`, `/health/liveness` and `/health/readiness` on the management port (9464).
    * `container.ports`/`service.ports`: `thinClients` (10800) replaces `sql`, `management` is new, `rest` and `restTcp`
      (11211, was `jdbc`) exist only with `rest.enabled`, and `jmx` and 10900 are gone.
    * The values follow the ignite chart: `persistence.storageVolume` (`/app/work`) and `persistence.walVolume`
      (`/wal`),
      `memory.*`, `authentication.*`, `timeouts.*`, `threadPools.*`, `dataStorage.*` and `ssl.*` (Secrets
      `<fullname>-auth`
      and `<fullname>-ssl`); `secrets.*` and most `env` keys are gone.
    * `authentication.igniteUserPassword` is empty by default: set it. A new password applies to the whole cluster as
      soon
      as the first restarted node sets it, so change the clients' password together with the rollout.
    * The client Service has the thin client and management ports (and REST); the discovery and communication ports are
      on the headless Service only.
    * The root file system is read-only, a PodDisruptionBudget is created for more than one replica, the termination
      grace
      period is 300 seconds, a node is ready only once it holds its data after starting, and the pods spread across
      Kubernetes nodes (`topologySpreadConstraints`).
    * The nodes keep no SQL query history (`sqlQueryHistorySize: 0`): a node that applies a new password would keep it
      there, readable by every SQL user. Set it empty for Ignite's default.
    * `env.ACTIVATION_MIN_SERVER_NODES` defaults to `replicaCount`: a new cluster, or one stopped completely, is
      activated once all its replicas joined, as with the ignite chart's activation job (it was activated by its first
      node). The pods read it from the ConfigMap `<fullname>-activation` when they start, so scaling restarts no
      running pod.
    * `networkPolicy.egress` and `networkPolicy.ingress` hold additional rules only, so that adding one keeps the rules
      the cluster needs: the DNS and Kubernetes API server rules are `networkPolicy.allowDns`,
      `networkPolicy.kubeApiServerCidrs` (narrow it to the API server's addresses) and
      `networkPolicy.kubeApiServerPorts` (an `egress` list that repeats them still works), and
      `networkPolicy.metricsFrom` allows the scraping of `/metrics`. The API server ports include 16443 (MicroK8s),
      which the earlier rules did not allow, so that the nodes did not find each other there.
    * `/tmp` is limited to `memory.onHeapMB` + 512 MiB (`tmpVolume.sizeLimit`), room for a heap dump, and the Ingress
      routes to the Service port named `rest` unless a path sets `port` or `portName`.
    * The `checksum/secrets` pod annotation is derived through scrypt instead of SHA-256 (whoever can read the pods
      cannot test guesses of the passwords against it at speed), so the pods restart once with the upgrade.
    * Upgrade with `--reset-then-reuse-values`, not `--reuse-values`, which keeps the old defaults and misses the new
      settings (the chart then fails with a message).

## Moving to Apache Ignite 2.19.0

Once Apache Ignite 2.19.0 is released, `IgniteVersion` moves to it (with `scripts/update-java-libs.sh`), and its upgrade
notes apply. From its `ignite-2.19` branch:

* Ignite 2.19 nodes cannot join a cluster of 2.18 nodes: the discovery protocol header changes (IGNITE-28601), so 2.18
  nodes drop the connections of 2.19 ones, and a 2.19 node of a Kubernetes cluster starts a second cluster beside them.
  Move as a full cluster restart: deactivate the cluster (`control.sh --set-state INACTIVE --yes`), stop every node,
  then start them on the new image. The chart refuses a rolling upgrade across Ignite minor versions (it records the
  Ignite version of its image).
* SQL runs on the Calcite engine by default (IGNITE-29117), with H2 still available (the `QUERY_ENGINE('h2')` hint, or
  `IndexingQueryEngineConfiguration` as the default engine of `SqlConfiguration`); the package has to bring the
  `ignite-calcite` module.
* The REST API moves to Jetty 12.1, which removes the reason for the REST caveat of the chart's Ingress.
* `MemoryConfiguration` and the OpenCensus tracing are removed (the server uses neither), and the `namespace` and
  `serviceName` properties of the Kubernetes IP finder are deprecated (`KubernetesConnectionConfiguration` replaces
  them in the Spring templates).

