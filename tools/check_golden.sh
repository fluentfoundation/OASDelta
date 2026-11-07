#!/usr/bin/env bash
# Runs a built oasdelta binary (typically the NativeAOT one) over every fixture pair and compares stdout and the exit
# code with tests/golden, which tools/gen_golden.sh produced from the Go oasdiff. The xUnit golden tests do the same
# in-process with the JIT build; this catches anything trimming or NativeAOT changes.
# usage: tools/check_golden.sh /path/to/oasdelta
set -u
export LC_ALL=C   # pairs.sh pairs files by sort order; keep it locale-independent
BIN=$(cd "$(dirname "${1:?path to the oasdelta binary}")" && pwd)/$(basename "$1")
ROOT=$(cd "$(dirname "$0")/.." && pwd)
cd "$ROOT/tests/fixtures"
pass=0; fail=0
for dir in "$ROOT"/tests/golden/*/; do
  slug=$(basename "$dir")
  case $slug in
    diff-yaml) args="diff" ;;
    diff-json) args="diff -f json" ;;
    diff-text) args="diff -f text" ;;
    summary) args="summary" ;;
    changelog-json) args="changelog -f json" ;;
    changelog-markdown) args="changelog -f markdown" ;;
    breaking-text) args="breaking" ;;
    *) echo "unknown golden set $slug"; exit 2 ;;
  esac
  seen=" "   # plain string, not an associative array: macOS ships bash 3.2
  while read -r b r; do
    name=$(echo "$b--$r" | sed 's|\./||g; s|[/.]|_|g')
    case "$seen" in *" $name "*) continue ;; esac
    seen="$seen$name "
    golden="$dir/$name.out"
    [ -f "$golden" ] || continue
    expected_code=$(awk -v n="$name" '$1 == n { print $2 }' "$dir/exit-codes.txt")
    actual=$("$BIN" $args "$b" "$r" 2>/dev/null; echo "exit=$?")
    expected=$(cat "$golden"; echo "exit=$expected_code")
    if [ "$actual" == "$expected" ]; then pass=$((pass+1)); else fail=$((fail+1)); echo "FAIL [$slug] $b $r"; fi
  done < <("$ROOT/tools/pairs.sh")
done
total=$(find "$ROOT/tests/golden" -name "*.out" | wc -l | tr -d " ")
echo "golden: pass=$pass fail=$fail of $total"
[ $((pass + fail)) -eq "$total" ] || { echo "only $((pass + fail)) of $total golden files were compared"; exit 1; }
[ "$fail" -eq 0 ]
