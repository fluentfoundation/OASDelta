#!/usr/bin/env bash
# Downloads the reports of the latest scheduled Properties runs on main, for the site's Nightly page
# (docs/data/oracle.data.ts). Writes OUT_DIR/runs.json (each run: id, conclusion, date, commit, URL, newest first)
# and OUT_DIR/<run id>/oracle.json for each run that uploaded one. Needs gh, signed in or with GH_TOKEN set.
#
#   tools/oracle-reports.sh docs/data/oracle [NIGHTS]
set -euo pipefail

out=${1:?usage: tools/oracle-reports.sh OUT_DIR [NIGHTS]}
nights=${2:-14}
repo=${GITHUB_REPOSITORY:-fluentfoundation/OASDelta}

rm -rf "$out"
mkdir -p "$out"
gh run list -R "$repo" --workflow properties.yml --branch main --event schedule --status completed --limit "$nights" \
  --json databaseId,conclusion,createdAt,headSha,url > "$out/runs.json"

for id in $(grep -o '"databaseId":[0-9]*' "$out/runs.json" | cut -d: -f2); do
  # runs before the report existed, or that failed before comparing, have none; the page says so
  gh run download "$id" -R "$repo" -n oracle-report -D "$out/$id" > /dev/null 2>&1 || echo "run $id: no report"
done
echo "$(grep -o '"databaseId"' "$out/runs.json" | wc -l | tr -d ' ') runs listed in $out/runs.json"
