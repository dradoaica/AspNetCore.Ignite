#!/usr/bin/env bash
# Paired release branches must agree: the server version of this repository (src/Directory.Build.props) is the
# appVersion of the chart. Fails when they differ, except for the chart of main, which may still be on the previous
# version (a notice only).
#   scripts/ci/check-paired-versions.sh ../helm-charts/charts/aspnetcore-ignite-server release/2.19.0
set -euo pipefail

chart_dir="${1:?usage: check-paired-versions.sh <chart-directory> <chart-ref>}"
chart_ref="${2:?usage: check-paired-versions.sh <chart-directory> <chart-ref>}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

server="$(sed -n -E 's/.*<Version>(.*)<\/Version>.*/\1/p' "$root/src/Directory.Build.props")"
app="$(sed -n -E 's/^appVersion: "?([^"]*)"?$/\1/p' "$chart_dir/Chart.yaml")"
echo "server ${server}, chart appVersion ${app} (helm-charts@${chart_ref})"

if [[ "$server" == "$app" ]]; then
  exit 0
fi

if [[ "$chart_ref" == main ]]; then
  echo "::notice::The chart on main has appVersion ${app}, this branch builds ${server}."
else
  echo "::error::helm-charts@${chart_ref} has appVersion ${app}, but this branch builds ${server}."
  exit 1
fi
