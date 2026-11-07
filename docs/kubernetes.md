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
* add a PodDisruptionBudget (`maxUnavailable: 1`) so that drains evict one node at a time,
* give each pod a unique, stable `DEFAULT_CONSISTENT_ID` (e.g., the pod name) and, with persistence, a volume at
  `/app/work`,
* set `ACTIVATION_MIN_SERVER_NODES` to the initial number of replicas (persistence); until they joined, no pod is
  ready, so the Service has no endpoints and thin clients cannot connect yet (by design),
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

The Kubernetes IP finder of Ignite 2.18 reads the `endpoints` of the headless service (the chart's role allows it).
Kubernetes deprecates the Endpoints API in favor of EndpointSlices (warnings from 1.33); it still works, and is to be
revisited with Ignite upgrades.
