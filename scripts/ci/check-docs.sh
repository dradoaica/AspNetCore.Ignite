#!/usr/bin/env bash
# Checks the documentation: every name in the "Where to change what" table of docs/architecture.md is a file or a
# directory of the repository, and, with --diagrams, every Mermaid diagram in docs/ parses (that needs Node.js and
# Google Chrome, as on GitHub's runners).
#   scripts/ci/check-docs.sh [--diagrams]
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
failed=0

# a tracked file or directory of that name, anywhere; a name without extension may be a type (its .cs file)
exists() {
  local name="${1%/}" files
  files="$(git ls-files -- ":(glob)**/${name}" ":(glob)**/${name}/**" ":(glob)**/${name}.*")"
  [[ -n "$files" ]]
}

# the backticked names of the table's second column
while IFS= read -r name; do
  if ! exists "$name"; then
    echo "::error file=docs/architecture.md::'${name}' (Where to change what) is not a file of the repository"
    failed=1
  fi
done < <(sed -n '/^## Where to change what/,/^## [^W]/p' docs/architecture.md | grep '^|' | tail -n +3 \
  | cut -d '|' -f 3 | grep -o '`[^`]*`' | tr -d '`')

if [[ "${1:-}" == --diagrams ]]; then
  out="$(mktemp -d)"
  trap 'rm -rf "$out"' EXIT
  # GitHub's Ubuntu 24.04 runners do not let Chrome create its sandbox
  echo '{"args": ["--no-sandbox"]}' > "$out/puppeteer.json"
  # the runner's Chrome, rather than one that puppeteer would download
  export PUPPETEER_SKIP_DOWNLOAD=true
  export PUPPETEER_EXECUTABLE_PATH="${PUPPETEER_EXECUTABLE_PATH:-$(command -v google-chrome)}"
  for file in $(grep -l '^```mermaid' docs/*.md); do
    if ! npx --yes @mermaid-js/mermaid-cli@11.4.2 --quiet --puppeteerConfigFile "$out/puppeteer.json" \
      --input "$file" --output "$out/$(basename "$file")"; then
      echo "::error file=${file}::A Mermaid diagram does not parse."
      failed=1
    fi
  done
fi

exit "$failed"
