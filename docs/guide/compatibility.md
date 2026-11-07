# Compatibility with oasdiff

OasDelta aims to be a drop-in replacement: the same input should produce the same bytes on stdout and the same exit
code as the Go binary.

## How it is checked

- **Golden tests.** `tests/golden` holds the output of the Go oasdiff for seven command/format combinations over
  every fixture pair in the upstream test data (180 pairs, 1,239 comparisons). `dotnet test` replays them in
  process, and CI replays them again with the published NativeAOT binary (`tools/check_golden.sh`), on Linux and
  Windows.
- **Live parity.** `tools/parity.sh <command> [flags]` runs both binaries over every fixture pair and compares
  stdout, stderr and exit codes.
- **Benchmarks.** Every benchmark case also compares the two binaries' output byte for byte, and the
  [results](/benchmarks) say whether they matched.

## Parity with the latest release

Upstream keeps moving. Against **oasdiff v1.35.0**, the release the benchmarks use:

| Command | Identical on fixture pairs |
|---|---|
| `diff` (yaml) | 180 / 180 |
| `summary` | 180 / 180 |
| `breaking` | 175 / 180 |
| `changelog -f json` | 170 / 180 |

The remaining differences come from wording changes upstream made after the port. For example, v1.35.0 prints
sub-schema property paths with a trailing slash (`data/contains/`), where the port prints `data/contains`.

## Reproduced exactly

- **YAML output (yaml.v3):** scalar quoting, "natural" map-key order (`resource2` sorts before `resource10`), and
  number resolution, including overflow and octal cases.
- **JSON output (encoding/json):** escaping and float formatting.
- **Go `fmt` verbs:** `%v`, `%q` and `%g`.
- **HTML templates:** CSS comments are dropped the way `html/template` drops them; the diff report reproduces
  goldmark's HTML, including heading ids.
- **Command line:** pflag usage text, cobra error messages and exit codes (100–123).

## Known differences

- **Program name.** Help text, `--version` and command-line errors say `oasdelta`. Changelog and diff output are
  unchanged.
- **Regular expressions.** `--match-path` and the other regex flags use .NET regular expressions instead of RE2.
  Common syntax errors are reworded to match Go's messages.
- **Extension order in the text diff report.** Upstream lists modified extensions in Go map order, which is
  random; the port sorts them.
- **Template errors.** Messages for broken custom templates are shorter than Go's.

## Not ported

- `--flatten-allof` (and the hidden `--flatten`): accepted, then reported as unsupported with exit code 101.
- `--auto-upgrade` and `--open` (which uploads to oasdiff.com): reported as unsupported.
- Commands: `validate`, `upgrade`, `flatten`, `breaking-files`, `git-diff-driver`, `checks validate`,
  `checks changelog coverage`, `completion`.
- Output format: `sarif`.
