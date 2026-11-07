#!/usr/bin/env bash
# Runs a built oasdelta binary (typically the NativeAOT one) over every fixture pair and compares stdout and the exit
# code with tests/golden, for every combination in tools/golden-commands.txt. The xUnit golden tests do the same
# in-process with the JIT build; this catches anything trimming or NativeAOT changes. Cases listed in
# tests/golden-known.txt are handled as that file describes. Writes a summary (with rule coverage) to
# $GITHUB_STEP_SUMMARY when set.
# usage: tools/check_golden.sh /path/to/oasdelta [JOBS]
set -u
# Git Bash on Windows rewrites POSIX-looking arguments (e.g. --prefix-revision /v2 -> C:/Program Files/Git/v2)
# when it runs a Windows executable; turn that off. No effect elsewhere.
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL="*"
export LC_ALL=C   # pairs.sh pairs files by sort order; keep it locale-independent
BIN=$(cd "$(dirname "${1:?path to the oasdelta binary}")" && pwd)/$(basename "$1")
JOBS=${2:-4}
ROOT=$(cd "$(dirname "$0")/.." && pwd)
PAIRS=$("$ROOT/tools/pairs.sh")
KNOWN=$(grep -v '^#' "$ROOT/tests/golden-known.txt" | grep . || true)
RESULTS=$(mktemp -d)

check_slug() {
  local slug=$1 args=$2 dir="$ROOT/tests/golden/$1" seen=" " pass=0 fail=0
  cd "$ROOT/tests/fixtures"
  while read -r b r; do
    local name golden expected_code actual expected kind
    name=$(echo "$b--$r" | sed 's|\./||g; s|[/.]|_|g')
    case "$seen" in *" $name "*) continue ;; esac
    seen="$seen$name "
    golden="$dir/$name.out"
    [ -f "$golden" ] || continue
    expected_code=$(awk -v n="$name" '$1 == n { print $2 }' "$dir/exit-codes.txt")
    # shellcheck disable=SC2086 # args are split on spaces by design
    actual=$("$BIN" $args "$b" "$r" 2>/dev/null; echo "exit=$?")
    expected=$(cat "$golden"; echo "exit=$expected_code")
    kind=$(awk -v s="$slug" -v n="$name" '$1 == s && $2 == n { print $3 }' <<< "$KNOWN")
    if [ "$actual" == "$expected" ]; then
      if [[ "$kind" == \#* ]]; then fail=$((fail+1)); echo "NOW MATCHES [$slug] $b $r: remove it from tests/golden-known.txt ($kind)"
      else pass=$((pass+1)); fi
    elif [ "$kind" == "unordered" ] && [ "$(sort <<< "$actual")" == "$(sort <<< "$expected")" ]; then
      pass=$((pass+1))
    elif [[ "$kind" == \#* ]]; then
      pass=$((pass+1))   # a tracked bug: expected to differ
    else
      fail=$((fail+1)); echo "FAIL [$slug] $b $r"
    fi
  done <<< "$PAIRS"
  echo "$pass $fail" > "$RESULTS/$slug"
}

while IFS='|' read -r slug args; do
  [[ -z "$slug" || "$slug" == \#* ]] && continue
  # poll rather than `wait -n`: macOS ships bash 3.2, which doesn't have it
  while [ "$(jobs -rp | wc -l)" -ge "$JOBS" ]; do sleep 0.2; done
  check_slug "$slug" "$args" &
done < "$ROOT/tools/golden-commands.txt"
wait

pass=0; fail=0; sets=0
for f in "$RESULTS"/*; do read -r p q < "$f"; pass=$((pass+p)); fail=$((fail+q)); sets=$((sets+1)); done
rm -rf "$RESULTS"
total=$(find "$ROOT/tests/golden" -name "*.out" | wc -l | tr -d " ")
IDS=$(grep -oE '"[a-z0-9-]+";$' "$ROOT/src/OasDelta/Checker/Generated/RuleIds.g.cs" | tr -d '";' | sort -u)
rules=$(grep -c . <<< "$IDS")
# grep -r, not cat with a glob: ~10k paths exceed the argument-length limit on macOS
fired=$(grep -rhoE '"id": ?"[a-z0-9-]+"' --include='*.out' "$ROOT/tests/golden" | sed -E 's/.*"([a-z0-9-]+)"$/\1/' | sort -u | grep -cxFf <(echo "$IDS"))
floor=$(tr -d '[:space:]' < "$ROOT/tests/rule-coverage-floor.txt")
known=$(grep -c . <<< "$KNOWN" || true)
echo "golden: pass=$pass fail=$fail of $total in $sets sets; rules firing: $fired of $rules (floor $floor); known cases: $known"
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    echo "### Golden comparisons ($(basename "$BIN"))"
    echo
    echo "| Comparisons | Passed | Failed | Sets | Rules firing in goldens | Known cases |"
    echo "|---:|---:|---:|---:|---:|---:|"
    echo "| $total | $pass | $fail | $sets | $fired of $rules (floor $floor) | $known |"
    echo
  } >> "$GITHUB_STEP_SUMMARY"
fi
[ $((pass + fail)) -eq "$total" ] || { echo "only $((pass + fail)) of $total golden files were compared"; exit 1; }
[ "$fired" -ge "$floor" ] || { echo "rule coverage dropped below the floor ($fired < $floor)"; exit 1; }
[ "$fail" -eq 0 ]
