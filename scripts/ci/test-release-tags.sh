#!/usr/bin/env bash
# The cases of scripts/ci/release-tags.sh, which decides at a release whether the floating image tags (latest, <major>,
# <major>.<minor>) move: a mistake there shows only on a real release, and then publicly.
#   scripts/ci/test-release-tags.sh
set -euo pipefail

script="$(dirname "${BASH_SOURCE[0]}")/release-tags.sh"
failed=0

# check <description> <version> <prerelease> <released tags, space-separated> <expected latest major minor>
check() {
  local description="$1" version="$2" prerelease="$3" released="$4" expected="$5" actual
  actual="$(printf '%s\n' $released | PRERELEASE="$prerelease" "$script" "$version" \
    | sed 's/^[a-z]*=//' | tr '\n' ' ' | sed 's/ $//')"
  if [[ "$actual" == "$expected" ]]; then
    echo "ok   $description"
  else
    echo "FAIL $description: expected '$expected', got '$actual'"
    failed=1
  fi
}

check "the highest release moves all"                 2.19.0      false "2.18.0 2.18.1"           "true true true"
check "a patch of an older line moves only its minor" 2.18.3      false "2.19.0 2.18.2"           "false false true"
check "versions compare as numbers (2.9 < 2.10)"      2.9.0       false "2.10.0"                  "false false true"
check "major 2 is not major 20"                       2.0.1       false "20.0.0"                  "false true true"
check "tags with a leading v count"                   2.19.0      false "v2.18.0 v2.19.1"         "false false false"
check "the same release again moves all"              2.19.0      false "2.18.0 2.19.0"           "true true true"
check "release candidates of others are ignored"      3.0.0       false "2.19.0 3.0.1-rc.1"       "true true true"
check "the first release moves all"                   1.0.0       false ""                        "true true true"
check "a version with a suffix moves none"            2.19.0-rc.1 false "2.18.0"                  "false false false"
check "a GitHub pre-release moves none"               2.19.0      true  "2.18.0"                  "false false false"

exit "$failed"
