#!/usr/bin/env bash
# Checks a built oasdelta binary (typically the NativeAOT one) against tests/golden: every fixture pair, for every
# combination in tools/golden-commands.txt, comparing stdout and the exit code byte for byte. The xUnit golden tests do
# the same in-process with the JIT build; this catches anything trimming or NativeAOT changes. Cases listed in
# tests/golden-known.txt are handled as that file describes. Writes a summary (with rule coverage) to
# $GITHUB_STEP_SUMMARY when set.
# usage: tools/check_golden.sh /path/to/oasdelta [SLUG...]   (SLUGs: check only those sets)
#
# All cases run inside one oasdelta process (OASDELTA_GOLDEN_BATCH, see src/OasDelta/Cli/GoldenBatch.cs): starting
# ~10k processes took 10 minutes on Windows runners and over 30 on Windows Arm64.
set -u
export LC_ALL=C   # pairs.sh pairs files by sort order; keep it locale-independent
BIN=$(cd "$(dirname "${1:?path to the oasdelta binary}")" && pwd)/$(basename "$1")
shift
ROOT=$(cd "$(dirname "$0")/.." && pwd)
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

"$ROOT/tools/pairs.sh" > "$WORK/pairs"
grep -v '^#' "$ROOT/tests/golden-known.txt" | grep . > "$WORK/known" || true
ONLY=" $* "

# One line per case: exit code, golden file (relative to tests/fixtures), kind, arguments; tab-separated.
total=0; sets=0
while IFS='|' read -r slug args; do
  [[ -z "$slug" || "$slug" == \#* ]] && continue
  [ "$ONLY" == "  " ] || case "$ONLY" in *" $slug "*) ;; *) continue ;; esac
  sets=$((sets + 1))
  total=$((total + $(find "$ROOT/tests/golden/$slug" -name '*.out' | wc -l)))
  awk -v slug="$slug" -v args="$args" -F' ' '
    FILENAME == ARGV[1] { code[$1] = $2; next }
    FILENAME == ARGV[2] { if ($1 == slug) kind[$2] = $3; next }
    {
      n = $1 "--" $2; gsub(/\.\//, "", n); gsub(/[\/.]/, "_", n)   # as gen_golden.sh names them
      if (seen[n]++ || !(n in code)) next
      a = args; gsub(/ /, "\t", a)
      printf "%s\t../golden/%s/%s.out\t%s\t%s\t%s\t%s\n", code[n], slug, n, (n in kind ? kind[n] : "-"), a, $1, $2
    }' "$ROOT/tests/golden/$slug/exit-codes.txt" "$WORK/known" "$WORK/pairs" >> "$WORK/cases"
done < "$ROOT/tools/golden-commands.txt"

cases=$(wc -l < "$WORK/cases" | tr -d ' ')
result=$(cd "$ROOT/tests/fixtures" && OASDELTA_GOLDEN_BATCH="$WORK/cases" "$BIN")
status=$?
grep -v '^batch:' <<< "$result"
pass=$(sed -nE 's/^batch: pass=([0-9]+) fail=([0-9]+)$/\1/p' <<< "$result"); pass=${pass:-0}
fail=$(sed -nE 's/^batch: pass=([0-9]+) fail=([0-9]+)$/\2/p' <<< "$result"); fail=${fail:-0}

# Every rule, the generated bound rules too: the binary's own list, which CheckerRulesMatchOasdiff keeps equal to oasdiff's
"$BIN" checks changelog -f json | grep -oE '"id":"[a-z0-9-]+"' | cut -d'"' -f4 | sort -u > "$WORK/rules"
rules=$(grep -c . "$WORK/rules")
# The rules that fire in the JSON goldens. grep -r, not cat with a glob: ~10k paths exceed the argument-length limit on macOS
fired_ids() { sed -E 's/.*"([a-z0-9-]+)"$/\1/' | sort -u | grep -xFf "$WORK/rules"; }
grep -rhoE '"id": ?"[a-z0-9-]+"' --include='*.out' "$ROOT/tests/golden" | fired_ids > "$WORK/fired"
fired=$(grep -c . "$WORK/fired")
floor=$(tr -d '[:space:]' < "$ROOT/tests/rule-coverage-floor.txt")
known=$(grep -c . "$WORK/known" || true)
echo "golden: pass=$pass fail=$fail of $total in $sets sets; rules firing: $fired of $rules (floor $floor); known cases: $known"
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  # On a pull request, the rules the goldens of its base branch fire, to show what the PR gains or loses
  base=""
  if [ -n "${GITHUB_BASE_REF:-}" ] && git -C "$ROOT" fetch -q --depth=1 origin "$GITHUB_BASE_REF" 2>/dev/null; then
    git -C "$ROOT" grep -hoE '"id": ?"[a-z0-9-]+"' FETCH_HEAD -- 'tests/golden/*.out' | fired_ids > "$WORK/base-fired" && base=$GITHUB_BASE_REF
  fi
  {
    echo "### Golden comparisons ($(basename "$BIN"))"
    echo
    echo "| Comparisons | Passed | Failed | Sets | Rules firing in goldens | Known cases |"
    echo "|---:|---:|---:|---:|---:|---:|"
    echo "| $total | $pass | $fail | $sets | $fired of $rules (floor $floor) | $known |"
    echo
    if [ -n "$base" ]; then
      gained=$(comm -13 "$WORK/base-fired" "$WORK/fired"); lost=$(comm -23 "$WORK/base-fired" "$WORK/fired")
      echo "Against \`$base\`: $(grep -c . <<< "$gained") rules gained, $(grep -c . <<< "$lost") lost."
      [ -z "$gained" ] || { echo; echo "Gained:"; sed 's/.*/- `&`/' <<< "$gained"; }
      [ -z "$lost" ] || { echo; echo "Lost:"; sed 's/.*/- `&`/' <<< "$lost"; }
      echo
    fi
    echo "<details><summary>$((rules - fired)) rules no golden fires (also on the <a href=\"https://fluentfoundation.github.io/OASDelta/guide/coverage\">coverage page</a>)</summary>"
    echo
    comm -23 "$WORK/rules" "$WORK/fired" | sed 's/.*/- `&`/'
    echo
    echo "</details>"
    echo
  } >> "$GITHUB_STEP_SUMMARY"
fi
[ "$cases" -eq "$total" ] || { echo "only $cases of $total golden files have a case"; exit 1; }
[ $((pass + fail)) -eq "$total" ] || { echo "only $((pass + fail)) of $total golden files were compared"; exit 1; }
[ "$fired" -ge "$floor" ] || { echo "rule coverage dropped below the floor ($fired < $floor)"; exit 1; }
[ "$status" -eq 0 ] && [ "$fail" -eq 0 ]
