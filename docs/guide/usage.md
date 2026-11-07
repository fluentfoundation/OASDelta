# Commands & flags

The command line follows upstream oasdiff: same commands, flags, defaults, config files, error messages and exit
codes.

```sh
oasdelta breaking base.yaml revision.yaml                        # breaking changes, text
oasdelta changelog base.yaml revision.yaml -f json               # full changelog as JSON
oasdelta diff base.yaml revision.yaml -f text                    # structural diff report
oasdelta summary base.yaml revision.yaml
oasdelta breaking main:openapi.yaml openapi.yaml --fail-on ERR   # base from a git revision
oasdelta changelog 'specs/v1/*.yaml' 'specs/v2/*.yaml' -c        # composed mode (globs, ** supported)
oasdelta checks changelog -t request -s error                    # list the rules
oasdelta schema                                                  # JSON Schema of the json output
```

## Spec sources

A spec argument can be any of:

- a file path
- an `http(s)` URL
- `-` for stdin
- a git revision, `ref:path`, read with `git show` (add `--fetch` to fetch first)

## Commands

| Command | Formats and notes |
|---|---|
| `diff` | yaml, json, text, markup, markdown, html |
| `summary` | yaml, json |
| `breaking`, `changelog` | text, singleline, yaml, json, markup, markdown, html, githubactions, junit; custom `--template` for markup, markdown and html |
| `checks changelog` | text, json, yaml; `--severity`, `--tags`, `--id`, `--location`, `--lang` |
| `schema` | JSON Schema of the changelog JSON output |

## Flags

All of upstream's common flags are supported:

- **Filtering:** `--match-path`, `--unmatch-path`, `--filter-extension`, `--exclude-elements`,
  `--exclude-extensions`
- **Path handling:** `--prefix-base`, `--prefix-revision`, `--strip-prefix-base`, `--strip-prefix-revision`,
  `--include-path-params`, `--case-insensitive-headers`
- **Matching:** `--match-inline-refs`, `--flatten-params`, `--allow-external-refs`
- **Failing builds:** `--fail-on`, `--fail-on-diff`, `--level`, `--severity-levels`, `--err-ignore`, `--warn-ignore`
- **Deprecation policy:** `--deprecation-days-beta`, `--deprecation-days-stable`, `--stability-level`
- **Output:** `--lang` (en, es, pt-br, ru), `--color`, `--attributes`

## Configuration files

OasDelta reads `.oasdelta.{json,yaml,yml}` or `oasdelta.*` from the current directory, then upstream's
`.oasdiff.*` and `oasdiff.*`, so existing configs keep working. `--config`, `$OASDELTA_CONFIG` or
`$OASDIFF_CONFIG` names a file explicitly.

- Unknown keys and invalid values are rejected the way upstream rejects them.
- Paths inside a config file resolve relative to that file.

## Profiling

Set `OASDELTA_TIMING=1` to print per-phase timings and allocation counts to stderr.
