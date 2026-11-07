#!/bin/bash
# Regenerates tests/golden from the upstream Go oasdiff (the oracle) over every fixture pair.
# usage: tools/gen_golden.sh /path/to/go/oasdiff
set -e
GO=${1:?path to the Go oasdiff binary}
ROOT=$(cd "$(dirname "$0")/.." && pwd)
cd "$ROOT/tests/fixtures"
rm -rf "$ROOT/tests/golden"
while IFS='|' read -r slug args; do
  mkdir -p "$ROOT/tests/golden/$slug"
  : > "$ROOT/tests/golden/$slug/exit-codes.txt"
  while read -r b r; do
    name=$(echo "$b--$r" | sed 's|\./||g; s|[/.]|_|g')
    [ -e "$ROOT/tests/golden/$slug/$name.out" ] && continue   # a pair listed twice (with and without ./) is checked once
    set +e
    $GO $args "$b" "$r" > "$ROOT/tests/golden/$slug/$name.out" 2>/dev/null
    code=$?
    set -e
    echo "$name $code" >> "$ROOT/tests/golden/$slug/exit-codes.txt"
  done < <("$ROOT/tools/pairs.sh")
done <<'CMDS'
diff-yaml|diff
diff-json|diff -f json
diff-text|diff -f text
summary|summary
changelog-json|changelog -f json
changelog-markdown|changelog -f markdown
breaking-text|breaking
CMDS
