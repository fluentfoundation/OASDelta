#!/usr/bin/env bash
# Regenerates tests/golden from the upstream Go oasdiff (the oracle) over every fixture pair, for every
# command/flag combination in tools/golden-commands.txt.
# usage: tools/gen_golden.sh /path/to/go/oasdiff [JOBS] [SLUG...]
#        With SLUGs, only those sets are regenerated; otherwise tests/golden is rebuilt from scratch.
set -e
# Git Bash on Windows rewrites POSIX-looking arguments (e.g. --prefix-revision /v2 -> C:/Program Files/Git/v2)
# when it runs a Windows executable; turn that off. No effect elsewhere.
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL="*"
export LC_ALL=C   # pairs.sh pairs files by sort order; keep it locale-independent
GO=${1:?path to the Go oasdiff binary}
JOBS=${2:-8}
ROOT=$(cd "$(dirname "$0")/.." && pwd)
GO=$(cd "$(dirname "$GO")" && pwd)/$(basename "$GO")
PAIRS=$("$ROOT/tools/pairs.sh")

gen_slug() {
  local slug=$1 args=$2 out="$ROOT/tests/golden/$1" seen=" "
  mkdir -p "$out"
  : > "$out/exit-codes.txt"
  cd "$ROOT/tests/fixtures"
  while read -r b r; do
    local name
    name=$(echo "$b--$r" | sed 's|\./||g; s|[/.]|_|g')
    case "$seen" in *" $name "*) continue ;; esac   # a pair listed twice (with and without ./) is checked once
    seen="$seen$name "
    set +e
    # shellcheck disable=SC2086 # args are split on spaces by design
    "$GO" $args "$b" "$r" > "$out/$name.out" 2>/dev/null
    echo "$name $?" >> "$out/exit-codes.txt"
    set -e
  done <<< "$PAIRS"
}

ONLY=" ${*:3} "
if [ "$ONLY" == "  " ]; then rm -rf "$ROOT/tests/golden"; fi
while IFS='|' read -r slug args; do
  [[ -z "$slug" || "$slug" == \#* ]] && continue
  if [ "$ONLY" != "  " ]; then
    case "$ONLY" in *" $slug "*) rm -rf "$ROOT/tests/golden/$slug" ;; *) continue ;; esac
  fi
  while (( $(jobs -rp | wc -l) >= JOBS )); do wait -n; done
  gen_slug "$slug" "$args" &
done < "$ROOT/tools/golden-commands.txt"
wait
echo "$(find "$ROOT/tests/golden" -name '*.out' | wc -l) golden files in $(find "$ROOT/tests/golden" -mindepth 1 -maxdepth 1 -type d | wc -l) sets"
