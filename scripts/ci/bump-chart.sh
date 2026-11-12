#!/usr/bin/env bash
# Moves the aspnetcore-ignite-server chart to a released server image: sets appVersion and the recorded Apache Ignite
# version in the given Chart.yaml, and bumps the chart's patch version. It only edits the file; the Release workflow
# commits it and opens the pull request.
#   scripts/ci/bump-chart.sh ../helm-charts/charts/aspnetcore-ignite-server/Chart.yaml 2.19.1 2.18.0
#
# Prints the outcome as key=value lines (the format of $GITHUB_OUTPUT):
#   result=bumped       the file was changed; notes= holds a sentence for the pull request when Ignite's minor
#                       version changes
#   result=up-to-date   the chart already has this appVersion (e.g., both release branches were prepared together)
#   result=other-minor  the chart is on another minor version: it needs its own release, not a patch bump
# Fails when the chart already has this appVersion but records another Ignite version.
set -euo pipefail

chart="${1:?usage: bump-chart.sh <Chart.yaml> <server-version> <ignite-version>}"
version="${2:?usage: bump-chart.sh <Chart.yaml> <server-version> <ignite-version>}"
ignite_version="${3:?usage: bump-chart.sh <Chart.yaml> <server-version> <ignite-version>}"

app="$(sed -n -E 's/^appVersion: "?([^"]*)"?$/\1/p' "$chart")"
# the chart refuses rolling upgrades across Ignite minor versions, so it records the Ignite version of its image
recorded="$(sed -n -E 's/^  ignite\.apache\.org\/version: "?([^"]*)"?$/\1/p' "$chart")"

if [[ "$app" == "$version" ]]; then
  if [[ "$recorded" != "$ignite_version" ]]; then
    echo "::error::The chart has appVersion ${version} but records Ignite ${recorded:-(none)}; the image runs" \
      "${ignite_version}: set its ignite.apache.org/version annotation." >&2
    exit 1
  fi
  echo "result=up-to-date"
  exit 0
fi

if [[ "${app%.*}" != "${version%.*}" ]]; then
  echo "result=other-minor"
  exit 0
fi

current="$(sed -n -E 's/^version: (.*)/\1/p' "$chart")"
sed -i -E "s/^appVersion: .*/appVersion: \"${version}\"/" "$chart"
sed -i -E "s/^(  ignite\.apache\.org\/version: ).*/\1\"${ignite_version}\"/" "$chart"
sed -i -E "s/^version: .*/version: ${current%.*}.$(( ${current##*.} + 1 ))/" "$chart"

echo "result=bumped"
if [[ "${recorded%.*}" != "${ignite_version%.*}" ]]; then
  echo "notes= It moves from Ignite ${recorded} to ${ignite_version}, whose nodes cannot form one cluster:" \
    "upgrading a running release needs a full stop."
else
  echo "notes="
fi
