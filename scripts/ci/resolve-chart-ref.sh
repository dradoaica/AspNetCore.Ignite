#!/usr/bin/env bash
# Prints the revision of dradoaica/helm-charts whose aspnetcore-ignite-server chart is tested with this branch:
# the first argument if it is not empty, else the branch of the same name as the second argument (the chart branch
# released together with this branch) if it exists, else main.
#   scripts/ci/resolve-chart-ref.sh "" release/2.19.0
set -euo pipefail

requested="${1:-}"
branch="${2:?usage: resolve-chart-ref.sh <requested-ref-or-empty> <branch>}"
repository="${HELM_CHARTS_REPOSITORY:-https://github.com/dradoaica/helm-charts.git}"

if [[ -n "$requested" ]]; then
  echo "$requested"
elif git ls-remote --exit-code --heads "$repository" "$branch" > /dev/null; then
  echo "$branch"
else
  echo main
fi
