# Checker-rule coverage

oasdiff 1.35.0 has a rule for every kind of change it reports, 778 in all (`oasdiff checks changelog`). A rule is
checked by the [golden tests](./compatibility#how-it-is-checked) only if at least one fixture pair makes it fire. For a
rule that never fires, nothing compares OasDelta's output with oasdiff's. This page lists which rules the goldens
reach and which they don't.

<RuleCoverage />

## How it is counted

- **The rules** are oasdiff's own list, kept in
  [`tests/golden/checks-changelog.json`](https://github.com/fluentfoundation/OASDelta/blob/main/tests/golden/checks-changelog.json).
  A test checks that OasDelta's `checks changelog -f json` is identical to it.
- **A rule fires in a golden** when its id appears in one of the JSON golden outputs: `changelog` and `breaking`
  with `-f json`, and the `changelog` option sets. The count is how many golden comparisons fire it.
  The link opens one of them.
- **The page is built from the goldens** at every docs build, so it always matches the tests.
- **The count can't drop.** CI fails if fewer rules fire than
  [`tests/rule-coverage-floor.txt`](https://github.com/fluentfoundation/OASDelta/blob/main/tests/rule-coverage-floor.txt).
  Every pull request's checks list the rules it gains or loses.

## Rules no golden fires

The [nightly property tests](./compatibility#how-it-is-checked) reach many of these with generated pairs, comparing
OasDelta with oasdiff on each. Those cases aren't goldens yet. Issue
[#38](https://github.com/fluentfoundation/OASDelta/issues/38) tracks covering every rule. For each group of rules
it records why nothing fires them yet:

- **Fired only by generated pairs** ([#40](https://github.com/fluentfoundation/OASDelta/issues/40)).
- **Shapes the generator never writes** ([#41](https://github.com/fluentfoundation/OASDelta/issues/41)):
  `minContains`/`maxContains`, `contentSchema`, `itemSchema`, boolean schemas, and `allOf` entries with only
  annotations.
- **Keywords the generator writes whose rules still don't fire** ([#42](https://github.com/fluentfoundation/OASDelta/issues/42)),
  mostly bounds.
- **Sunset and stability rules** ([#43](https://github.com/fluentfoundation/OASDelta/issues/43)), which depend on dates.
- **Security-scheme URLs and scopes** ([#44](https://github.com/fluentfoundation/OASDelta/issues/44)).
