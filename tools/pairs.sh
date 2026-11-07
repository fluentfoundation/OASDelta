#!/bin/bash
# Emits "base revision" fixture pairs from the upstream data directory.
D=${FIXTURES:-$(cd "$(dirname "$0")/../tests/fixtures" && pwd)}
cd $D
for b in $(find . -name '*_base*.yaml' -o -name '*_base*.json' | sort); do
  r=${b/_base/_revision}; [ -f "$r" ] && echo "$b $r"
done
for b in $(find . -name '*1.yaml' | sort); do
  r=${b%1.yaml}2.yaml; [ -f "$r" ] && echo "$b $r"
done
ls openapi-test*.yaml | sort | awk 'NR>1{print prev" "$0}{prev=$0}'
echo "openapi-test1.yaml openapi-test3.yaml"
echo "home-iot-api-1.yaml home-iot-api-2.yaml"
