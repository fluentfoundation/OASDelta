#!/bin/bash
# Usage: parity.sh <subcommand-and-flags...>   e.g. parity.sh diff ; parity.sh diff -f json ; parity.sh summary
G=${GO:-oasdiff}
ROOT=$(cd "$(dirname "$0")/.." && pwd)
C=${CS:-$ROOT/src/OasDelta/bin/Release/net10.0/oasdelta}
D=$ROOT/tests/fixtures
OUT=${OUT:-/tmp/parity.$$}; rm -rf $OUT; mkdir -p $OUT
pass=0; fail=0
cd $D
while read b r; do
  name=$(echo "$b" | tr '/.' '__')
  $G "$@" "$b" "$r" > $OUT/$name.go 2>&1; ge=$?
  $C "$@" "$b" "$r" > $OUT/$name.cs 2>&1; ce=$?
  if cmp -s $OUT/$name.go $OUT/$name.cs && [ $ge = $ce ]; then pass=$((pass+1)); rm $OUT/$name.go $OUT/$name.cs; else fail=$((fail+1)); echo "FAIL $b $r (exit go=$ge cs=$ce)"; fi
done < <("$ROOT/tools/pairs.sh")
echo "pass=$pass fail=$fail"
