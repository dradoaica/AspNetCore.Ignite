# Kubernetes

The [Helm chart](https://github.com/dradoaica/helm-charts/tree/main/charts/aspnetcore-ignite-server) runs the server as
a StatefulSet. A deployment should:

* probe `/health/startup` (startup), `/health/liveness` and `/health/readiness` on port 9464 (and scrape `/metrics`),
* start the pods in parallel (`podManagementPolicy: Parallel`) when `ACTIVATION_MIN_SERVER_NODES` is above 1: with
  `OrderedReady`, the first pod is never ready (its cluster waits for the others) and the others never start,
* size the stop for real data: a node's graceful stop waits until its partitions have copies on other nodes, so set
  `GRACEFUL_STOP_TIMEOUT_IN_SECONDS` (e.g., 240) below `SHUTDOWN_TIMEOUT_IN_SECONDS` (e.g., 270) below
  `terminationGracePeriodSeconds` (e.g., 300); together with `READY_WHEN_REBALANCED`, a rolling restart moves on only
  once the restarted node holds its data again,
* deactivate the cluster before stopping all its pods at once (`helm uninstall`, scaling to 0):
  `kubectl exec <pod> -- control.sh --set-state INACTIVE --yes`; otherwise its last nodes wait for each other until
  `GRACEFUL_STOP_TIMEOUT_IN_SECONDS` and stop uncleanly (a single node deactivates it by itself),
* restart a pod within `BASELINE_AUTO_ADJUST_TIMEOUT_IN_SECONDS` (300 s with persistence): a node away longer leaves
  the baseline, and its data is moved to the others and back once it returns; a new pod (scale-out) is ready once the
  auto-adjust added it to the baseline and it received its data,
* spread the pods across Kubernetes nodes (a soft `topologySpreadConstraints` by `kubernetes.io/hostname`), so that a
  Kubernetes node failure takes as few Ignite nodes, and copies of the same partitions, as possible,
* add a PodDisruptionBudget (`maxUnavailable: 1`) so that drains evict one node at a time,
* alert on data loss and missing nodes ([Alerts](#alerts)), and use the image's control utility for the baseline, lost
  partitions and snapshots (`kubectl exec <pod> -- control.sh --baseline`): a persistent node that never returns has to
  be removed from the baseline (`--baseline remove`) before its partitions are reset,
* give `helm upgrade --wait` a `--timeout` above its 5 minute default: a rolling restart waits for each node to
  rebalance, up to `REBALANCE_READINESS_MAX_WAIT_IN_SECONDS` per node (e.g., `--timeout 60m` for 5 nodes and the
  default 10 minutes; less with little data),
* give each pod a unique, stable `DEFAULT_CONSISTENT_ID` (e.g., the pod name) and, with persistence, a volume at
  `/app/work`,
* set `ACTIVATION_MIN_SERVER_NODES` to the number of replicas (persistence; `env.ACTIVATION_MIN_SERVER_NODES` in the
  chart): a new cluster, or one stopped completely, waits for them before it is activated, so that it does not start
  without the partitions of the nodes still starting; until then, no pod is ready, so the Service has no endpoints and
  thin clients cannot connect yet (by design),
* enable `USE_SSL` and/or restrict the cluster ports with a NetworkPolicy:

```yaml
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: aspnetcore-ignite-server
spec:
  podSelector:
    matchLabels:
      app.kubernetes.io/name: aspnetcore-ignite-server
  policyTypes: [Ingress]
  ingress:
    # discovery and communication: the cluster's own pods only
    - from:
        - podSelector:
            matchLabels:
              app.kubernetes.io/name: aspnetcore-ignite-server
      ports:
        - port: 47500
        - port: 47100
    # thin clients and management (narrow "from" to the client and monitoring namespaces)
    - ports:
        - port: 10800
        - port: 9464
```

## Alerts

Prometheus rules for the metrics of `/metrics`:

```yaml
groups:
  - name: ignite
    rules:
      - alert: IgnitePartitionsLost
        expr: max by (namespace) (ignite_cluster_lost_partitions) > 0
        annotations:
          summary: Partitions lost their data (reset once their nodes return, or by hand).
      - alert: IgniteBaselineNodeOffline
        expr: max by (namespace) (ignite_cluster_baseline_nodes) > max by (namespace) (ignite_cluster_server_nodes)
        for: 10m
        annotations:
          summary: A baseline node is offline, so some partitions have fewer copies.
      - alert: IgniteClusterInactive
        expr: max by (namespace) (ignite_cluster_active) == 0
        for: 15m
        annotations:
          summary: The cluster is inactive (waiting for ACTIVATION_MIN_SERVER_NODES, or deactivated).
      - alert: IgniteNodeNotReady
        expr: ignite_node_ready == 0
        for: 15m
        annotations:
          summary: A node is not ready (starting, joining the baseline, or receiving its data).
      - alert: IgniteEvictingEntries
        expr: ignite_data_region_eviction_rate > 0
        for: 5m
        annotations:
          summary: An in-memory data region is full and evicts entries.
```

## Endpoints API

The Kubernetes IP finder of Ignite 2.18 reads the `endpoints` of the headless service (the chart's role allows it).
Kubernetes deprecates the Endpoints API in favor of EndpointSlices (warnings from 1.33); it still works, and is to be
revisited with Ignite upgrades.
