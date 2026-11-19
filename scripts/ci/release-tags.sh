#!/usr/bin/env bash
# Which floating tags of the image a release moves: latest, <major> and <major>.<minor>, each only when the release is
# the highest version of its line, so that a patch of an older line (e.g., 2.18.3 after 2.19.0) does not move them back.
# A prerelease moves none. The released versions (tags, with or without a leading v) come on stdin.
#   gh release list --json tagName --jq '.[].tagName' | scripts/ci/release-tags.sh <version>
# Prints latest=, major= and minor= (true or false), for $GITHUB_OUTPUT.
set -euo pipefail

version="$1"
if [[ ! "$version" =~ ^([0-9]+)\.([0-9]+)\.[0-9]+$ ]]; then
  printf 'latest=false\nmajor=false\nminor=false\n'
  exit 0
fi
major="${BASH_REMATCH[1]}"
minor="${BASH_REMATCH[2]}"
released="$(sed 's/^v//' | grep -E '^[0-9]+\.[0-9]+\.[0-9]+$' || true)"

# whether the version is the highest of those that start with the prefix (a regular expression)
is_highest() {
  local highest
  highest="$({ printf '%s\n' "$version"; printf '%s\n' "$released" | grep -E "^$1" || true; } | sort -V | tail -n 1)"
  [[ "$highest" == "$version" ]] && echo true || echo false
}

echo "latest=$(is_highest '')"
echo "major=$(is_highest "${major}\.")"
echo "minor=$(is_highest "${major}\.${minor}\.")"
