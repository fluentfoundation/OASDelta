# OasDelta

[![CI](https://github.com/fluentfoundation/OASDelta/actions/workflows/ci.yml/badge.svg)](https://github.com/fluentfoundation/OASDelta/actions/workflows/ci.yml)
[![Benchmarks & site](https://github.com/fluentfoundation/OASDelta/actions/workflows/benchmarks.yml/badge.svg)](https://github.com/fluentfoundation/OASDelta/actions/workflows/benchmarks.yml)

**Docs and live benchmarks: <https://fluentfoundation.github.io/OASDelta/>**

OasDelta is a C# port of [oasdiff](https://github.com/oasdiff/oasdiff), the OpenAPI diff and breaking-change detector. It targets .NET 10 and compiles to a single self-contained NativeAOT executable, `oasdelta`, with no runtime dependency.

The command line is argument-compatible with oasdiff, so `alias oasdiff=oasdelta` is a drop-in replacement. Output matches upstream byte for byte. On every fixture pair in the upstream test data (180 pairs), each supported command and output format prints the same bytes and returns the same exit code as the Go binary.

## Install

```sh
dotnet tool install --global OasDelta          # NativeAOT build for your platform, from nuget.org
dnx OasDelta breaking base.yaml revision.yaml  # or run once without installing (.NET 10 SDK)
```

Or download a binary for Linux, Windows or macOS (x64 or Arm64) from [Releases](https://github.com/fluentfoundation/OASDelta/releases). Each archive has a `checksums.txt` entry and a build-provenance attestation (`gh attestation verify <file> --repo fluentfoundation/OASDelta`).

## Build

```sh
dotnet build -c Release                                                       # JIT build: src/OasDelta/bin/Release/net10.0/oasdelta
dotnet publish src/OasDelta -c Release -r linux-x64 -o out                    # NativeAOT: out/oasdelta (~10.6 MB)
dotnet test                                                                   # unit tests plus 10,461 golden comparisons
```

For other platforms, use `-r osx-arm64`, `-r win-x64` and so on.

Toolchain notes:

- **SDK.** The build uses SDK 10.0.1xx. It needs NuGet access for YamlDotNet and Markdig.
- **C# version.** SDK 10.0.1xx offers C# 14 plus `preview`, so the project uses `LangVersion=preview`. That is C# 15 wherever the compiler supports it.

## Usage

The command line follows upstream: same commands, flags, defaults, config files, error messages and exit codes.

```sh
oasdelta breaking base.yaml revision.yaml                     # breaking changes, text
oasdelta changelog base.yaml revision.yaml -f json            # full changelog as JSON
oasdelta diff base.yaml revision.yaml -f text                 # structural diff report
oasdelta summary base.yaml revision.yaml
oasdelta breaking main:openapi.yaml openapi.yaml --fail-on ERR   # base from a git revision
oasdelta changelog 'specs/v1/*.yaml' 'specs/v2/*.yaml' -c     # composed mode (globs, ** supported)
oasdelta checks changelog -t request -s error                 # list the rules
oasdelta schema                                               # JSON Schema of the json output
```

### Spec sources

A spec can come from any of these:

- a file
- an `http(s)` URL
- `-` for stdin
- a git revision `ref:path`, read with `git show`, with optional `--fetch`

### Commands

| Command | Formats / notes |
|---|---|
| `diff` | yaml, json, text, markup, markdown, html |
| `summary` | yaml, json |
| `breaking`, `changelog` | text, singleline, yaml, json, markup, markdown, html, githubactions, junit; custom `--template` for markup/markdown/html |
| `checks changelog` | text, json, yaml; `--severity`, `--tags`, `--id`, `--location`, `--lang` |
| `schema` | JSON Schema of the changelog JSON output |

### Flags and configuration

- **Common flags.** All of upstream's common flags are supported:
  - `--match-path`, `--unmatch-path`, `--filter-extension`
  - `--prefix-*`, `--strip-prefix-*`
  - `--exclude-elements`, `--exclude-extensions`
  - `--include-path-params`, `--match-inline-refs`
  - `--flatten-params`, `--case-insensitive-headers`
  - `--allow-external-refs`
  - `--fail-on`, `--fail-on-diff`, `--level`
  - `--lang` (en, es, pt-br, ru)
  - `--color`
  - `--err-ignore`, `--warn-ignore`
  - `--severity-levels`
  - `--deprecation-days-*`, `--stability-level`
  - `--attributes`
- **Config files.** The tool reads `.oasdelta.{json,yaml,yml}` or `oasdelta.*` from the current directory, then upstream's `.oasdiff.*` / `oasdiff.*`, so existing configs keep working. `--config`, `$OASDELTA_CONFIG` or `$OASDIFF_CONFIG` names a file explicitly.
  - Unknown keys and invalid values are rejected the way upstream rejects them.
  - Paths inside a config file resolve relative to that file.

### Not ported

These upstream features are not in this port:

- **`--flatten-allof` (and the hidden `--flatten`).** These need upstream's 1,700-line allOf merger. The flag is accepted and then reported as unsupported, with exit code 101.
- **`--auto-upgrade` and `--open`.** Both are reported as unsupported. `--open` uploads to oasdiff.com.
- **Commands:**
  - `validate`
  - `upgrade`
  - `flatten`
  - `breaking-files`
  - `git-diff-driver`
  - `checks validate`
  - `checks changelog coverage`
  - `completion`
- **Output format:** `sarif`.

## Fidelity

Golden files in `tests/golden`, generated by oasdiff 1.35.0, cover 33 command/format/language/option combinations (`tools/golden-commands.txt`) over 317 fixture pairs. Those are the upstream test data paired by file name, the pairs upstream's own checker tests compare (`tests/extra-pairs.txt`), and regression cases found by the property-based tests (`tests/fixtures/properties`). That is 10,461 comparisons, in which 383 of the 617 checker rules fire. Property-based tests (CsCheck, `tests/OasDelta.Tests/Properties`) generate OpenAPI documents and edits: invariants are checked on every `dotnet test`, and a nightly [workflow](.github/workflows/properties.yml) compares both binaries on thousands of generated pairs. The goldens are checked on every CI run with both the JIT build (`dotnet test`) and the NativeAOT binary (`tools/check_golden.sh`). Cases expected to differ are listed in `tests/golden-known.txt`, each with its issue. Rule coverage may not drop below `tests/rule-coverage-floor.txt`. To regenerate: `tools/gen_golden.sh /path/to/oasdiff [JOBS] [SLUG...]`. The version the port tracks is in `tools/oasdiff-version.txt`. The weekly [Upstream watch](.github/workflows/upstream-watch.yml) workflow regenerates the goldens when oasdiff releases a newer version, pushes them to a branch and opens an issue that lists what changed. Real-world parity across releases of the GitHub, Stripe and Kubernetes APIs is reported weekly by [OASDelta-TestData](https://github.com/fluentfoundation/OASDelta-TestData).

`tools/parity.sh <command> [flags]` runs both binaries over all fixture pairs and compares stdout, stderr and exit codes. When the port was made, every check below passed 180/180 against its upstream version, for both the JIT and the NativeAOT builds:

- **diff:** yaml, json, text, html
- **summary:** yaml, json
- **changelog:** text, yaml, json, markdown, html, singleline, githubactions, junit; also with `--level`
- **breaking:** text, json
- **Languages:** ru, es and pt-br

Several upstream details are reproduced exactly:

- **YAML output (yaml.v3):**
  - scalar quoting
  - "natural" map-key order, so that `resource2` sorts before `resource10`
  - number resolution, including overflow and octal cases
- **JSON output (encoding/json):** escaping and float formatting.
- **Go `fmt` verbs:** `%v`, `%q` and `%g`.
- **CSS comments in HTML templates:** they are dropped the way `html/template` drops them.
- **Diff HTML report:** the HTML that goldmark produces, including heading ids and `<br>`/`<hr>`.
- **CLI output:**
  - pflag usage text
  - cobra error messages and exit codes (100–123)

Known differences:

- **Program name:** help text, `--version` and command-line error messages say `oasdelta` where upstream says `oasdiff`. Changelog and diff output are unchanged.
- **Regular expressions:** `--match-path` and the other regex flags use .NET regular expressions instead of RE2. Common syntax errors are reworded to match Go's messages.
- **Extension order in the text diff report:** upstream lists modified extensions in Go map order, which is random. The port sorts them.
- **Template errors:** messages for broken custom templates are shorter than Go's.

## Performance

CI benchmarks the NativeAOT build against the official oasdiff release on Linux and Windows on every push to `main`. The latest results are on the [benchmarks page](https://fluentfoundation.github.io/OASDelta/benchmarks), and each run's table is in its [workflow summary](https://github.com/fluentfoundation/OASDelta/actions/workflows/benchmarks.yml).

The original measurement below uses the NativeAOT build against the upstream Go binary on a 2-core VM. Times are medians; memory is peak RSS.

| Input | Command | Go | C# | Speed-up |
|---|---|---|---|---|
| Small (openapi-test1 → test3) | `diff` | 21 ms / 23 MB | 6 ms / 15 MB | 3.6× |
| | `breaking` | 20 ms / 24 MB | 6 ms / 16 MB | 3.1× |
| 1.5 MB YAML, 2,400 paths, 650 schemas | `diff` | 2,069 ms / 358 MB | 668 ms / 272 MB | 3.1× |
| | `changelog -f json` | 1,819 ms / 316 MB | 714 ms / 277 MB | 2.5× |
| 1.1 MB JSON, same API | `diff` | 1,118 ms / 249 MB | 250 ms / 238 MB | 4.5× |
| | `breaking` | 878 ms / 124 MB | 487 ms / 273 MB | 1.8× |

The large specs come from `tools/gen_bench_spec.py`; `tools/bench.py` runs the comparison. The executable is 10.6 MB; the Go binary is 26 MB.

What made the difference:

- **NativeAOT build settings:**
  - invariant globalization
  - system resource keys
  - no stack-trace metadata
- **GC settings.** Non-concurrent GC suits a short-lived batch process whose heap is nearly all live. The LOH threshold is raised, so large arrays no longer force full collections. Both settings are compiled into the binary.
- **Templated-path index.** Path matching with `{param}` templates uses an index instead of upstream's quadratic scan.
- **JSON fast path.** JSON specs are parsed straight into the node tree with `Utf8JsonReader`.
- **Hashed duplicate-key detection** for large YAML mappings.

To see where time goes, set `OASDELTA_TIMING=1`. The tool then prints per-phase timings and allocation counts to stderr.

## Layout

```
src/OasDelta/
  Yaml/        YAML/JSON node reader, yaml.v3-compatible emitter, Go-compatible JSON emitter, value helpers
  Model/       OpenAPI model (kin-openapi equivalent)
  Load/        spec loader ($ref resolution, origins, external refs), sources (stdin/URL/git/glob), load options
  Diff/        diff engine: schema graph + cycle handling, paths/operations/parameters/... diffs
  Checker/     rule engine: 617 rule ids, ~150 checks, localizations, transitions, versioning/stability policies
  Formatters/  changelog renderers, Go text/template interpreter, diff text/HTML report, checks listing
  Cli/         command line (cobra/pflag/viper-compatible)
tests/
  fixtures/    upstream test data (Apache-2.0)
  golden/      expected outputs produced by the Go oracle (tools/gen_golden.sh)
  OasDelta.Tests/
tools/         parity.sh, check_parity.py, gen_golden.sh, check_golden.sh, gen_checker_tables.py, gen_stubs.sh, bench.py, gen_bench_spec.py
docs/          VitePress site (npm ci && npm run dev); benchmark results in docs/data/benchmarks
.github/       CI (build, test, NativeAOT golden check on Linux and Windows) and benchmarks + GitHub Pages deploy
```

`Checker/Generated/*.g.cs` is generated from the upstream Go sources by `tools/gen_checker_tables.py`. It holds the rule ids, the rule table and the four localizations.

## Contributing and releasing

`main` is protected: changes go through a pull request, and the Linux, Windows and docs checks must pass. No approving review is required, so a maintainer can merge their own PR.

To release, tag a commit on `main` and push the tag:

```sh
git tag v1.1.0 && git push origin v1.1.0       # v1.1.0-rc.1 publishes a pre-release
```

The [Release workflow](.github/workflows/release.yml) builds NativeAOT binaries for linux, windows and darwin on amd64 and arm64. It runs the golden comparisons on each one and publishes them with `checksums.txt`, build-provenance attestations and generated release notes. It then pushes the `OasDelta` .NET tool to nuget.org: six native per-platform packages plus the top-level package. The push uses Trusted Publishing, so no API key is stored, and it runs in the `nuget` environment, which only `v*` tags can deploy to. The tag sets the version that `oasdelta --version` reports. `v*` tags are protected, so a published tag cannot be moved or deleted.

## License

Apache License 2.0, the same as upstream. OasDelta is a derivative work of oasdiff and is not affiliated with or endorsed by the oasdiff project; the test fixtures come from the upstream repository.
