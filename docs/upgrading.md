# Upgrading to 2.19.0

2.19.0 changes defaults, settings and the image layout (below), so it is a minor version rather than a patch. It targets
Apache Ignite 2.19.0 and is built on Apache Ignite 2.18.0 until 2.19.0 is released (`IgniteVersion` in
`Directory.Packages.props`); the data format, the client protocol and the cluster are those of Ignite 2.18.0.

* SSL (`USE_SSL`, `USE_CLIENT_SSL`) is off by default, there are no default passwords or consistent id, and the image
  no longer contains the sample keystores: set these explicitly.
* The client validates the server certificate by default (`ASPNETCORE_IGNITE_SSL_SKIP_SERVER_CERTIFICATE_VALIDATION`
  to opt out), and platform cache is off by default (`Cache.EnablePlatformCache` to opt in).
* The client library targets .NET 10 (`net10.0`) instead of .NET Standard 2.1: applications on earlier .NET versions
  keep using 2.18.x.
* `IIgniteManager` has new `Client` and `GetClientAsync` members (implementations outside this library must add them),
  and the library no longer depends on `Apache.Ignite.Linq` (add it for LINQ queries).
* The server image no longer contains the Ignite binary distribution (`IGNITE_HOME`): the jars are in `/app/libs`.
* REST (8080) and the TCP binary protocol (11211) are off (`REST_ENABLED`), peer assembly loading is off
  (`PEER_ASSEMBLY_LOADING`), and OpenCensus metrics are replaced by `/metrics` on port 9464 (`MANAGEMENT_PORT`).
* Logs go to the console only (JSON in the image); the file sink is opt-in.
* SSL restarts are graceful and jittered; the process exits with `1` on failures (it always exited with `0`).
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
  `persistence.storageVolume.claimName=persistence`, so the existing data volumes are reused (only the WAL volume is
  new):

  ```
  kubectl scale statefulset <fullname>-cluster --replicas=0
  kubectl delete statefulset <fullname>-cluster --cascade=orphan
  helm upgrade <release> <chart> --set persistence.storageVolume.claimName=persistence ...
  ```

  The `chart-upgrade` job of the Tests workflow (`upgrade-test` input) runs these steps with data. The chart's values
  also change:
  * The probes use `/health/startup`, `/health/liveness` and `/health/readiness` on the management port (9464).
  * `container.ports`/`service.ports`: `thinClients` (10800) replaces `sql`, `management` is new, `rest` and `restTcp`
    (11211, was `jdbc`) exist only with `rest.enabled`, and `jmx` and 10900 are gone.
  * The values follow the ignite chart: `persistence.storageVolume` (`/app/work`) and `persistence.walVolume` (`/wal`),
    `memory.*`, `authentication.*`, `timeouts.*`, `threadPools.*`, `dataStorage.*` and `ssl.*` (Secrets `<fullname>-auth`
    and `<fullname>-ssl`); `secrets.*` and most `env` keys are gone.
  * `authentication.igniteUserPassword` is empty by default: set it. A new password applies to the whole cluster as soon
    as the first restarted node sets it, so change the clients' password together with the rollout.
  * The root file system is read-only, a PodDisruptionBudget is created for more than one replica, the termination grace
    period is 300 seconds, a node is ready only once it holds its data after starting, and the pods spread across
    Kubernetes nodes (`topologySpreadConstraints`).
