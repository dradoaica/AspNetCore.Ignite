#!/usr/bin/env bash
# Installs the aspnetcore-ignite-server chart with a locally built server image as a two-node persistent cluster,
# waits for the chart's probes and runs `helm test`. CI runs it in kind; run it by hand against any cluster that has
# the image, e.g.:
#   docker build -t ghcr.io/dradoaica/aspnetcore.ignite-server:e2e .
#   # kind:      kind load docker-image ghcr.io/dradoaica/aspnetcore.ignite-server:e2e
#   # microk8s:  docker save ghcr.io/dradoaica/aspnetcore.ignite-server:e2e -o image.tar && microk8s images import image.tar
#   KUBE_CONTEXT=microk8s NAMESPACE=ignite-e2e scripts/test-chart.sh ../helm-charts/charts/aspnetcore-ignite-server
#
# Environment: KUBE_CONTEXT (default: the current context), NAMESPACE (default: default; created if missing),
# RELEASE (default: ignite), IMAGE_TAG (default: e2e), KEEP=1 to leave the release installed afterward.
set -euo pipefail

chart_dir="${1:?usage: test-chart.sh <chart-directory>}"
release="${RELEASE:-ignite}"
namespace="${NAMESPACE:-default}"
context=(${KUBE_CONTEXT:+--context "$KUBE_CONTEXT"})
helm_context=(${KUBE_CONTEXT:+--kube-context "$KUBE_CONTEXT"})
selector=app.kubernetes.io/name=aspnetcore-ignite-server

diagnostics() {
  kubectl "${context[@]}" -n "$namespace" get pods,pvc,svc -o wide || true
  kubectl "${context[@]}" -n "$namespace" describe pods || true
  kubectl "${context[@]}" -n "$namespace" logs -l "$selector" --all-containers --tail=200 || true
}
on_exit() {
  local status=$?
  [[ $status -eq 0 ]] || diagnostics
  exit "$status"
}
trap on_exit EXIT

kubectl "${context[@]}" get namespace "$namespace" > /dev/null 2>&1 || kubectl "${context[@]}" create namespace "$namespace"

helm "${helm_context[@]}" -n "$namespace" install "$release" "$chart_dir" \
  --set image.tag="${IMAGE_TAG:-e2e}" --set image.pullPolicy=Never \
  --set replicaCount=2 --set env.ACTIVATION_MIN_SERVER_NODES=2 \
  --set authentication.igniteUserPassword=e2e-secret \
  --set resources.requests.cpu=500m --set resources.limits.cpu=1 \
  --set resources.requests.memory=3Gi --set resources.limits.memory=3Gi \
  --set memory.onHeapMB=512 --set memory.offHeapMB=256 --set memory.maxDirectMemorySizeMB=1024 \
  --set persistence.storageVolume.size=1Gi --set persistence.walVolume.size=1Gi \
  --wait --timeout 10m

helm "${helm_context[@]}" -n "$namespace" test "$release" --logs

if [[ "${KEEP:-}" != 1 ]]; then
  # deactivated first, so that the two nodes do not wait for each other's partitions while they stop
  pod="$(kubectl "${context[@]}" -n "$namespace" get pods -l "$selector" -o name | head -n 1)"
  kubectl "${context[@]}" -n "$namespace" exec "$pod" -- control.sh --set-state INACTIVE --yes > /dev/null
  helm "${helm_context[@]}" -n "$namespace" uninstall "$release" --wait
  kubectl "${context[@]}" -n "$namespace" delete pvc -l "$selector" --ignore-not-found
fi
