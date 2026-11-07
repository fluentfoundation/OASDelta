# Compatibility with oasdiff

OasDelta aims to be a drop-in replacement: the same input should produce the same bytes on stdout and the same exit
code as the Go binary.

## How it is checked

- **Golden tests.** `tests/golden` holds the output of **oasdiff v1.35.0** for seven command/format combinations
  (`diff` as yaml, json and text; `summary`; `changelog` as json and markdown; `breaking`). They cover every fixture
  pair in the upstream test data plus a few of OasDelta's own: 179 pairs, 1,253 comparisons. `dotnet test`
  replays them in process. CI replays them with the NativeAOT binary (`tools/check_golden.sh`) on Linux and Windows,
  and the release workflow replays them on all six release platforms. All of them pass.
- **Real-world parity.** [OASDelta-TestData](https://github.com/fluentfoundation/OASDelta-TestData) holds
  consecutive releases of the GitHub, Stripe, Kubernetes and European Patent Office APIs. Its weekly
  [parity report](https://github.com/fluentfoundation/OASDelta-TestData/actions/workflows/parity.yml) runs the
  official oasdiff release and the latest OasDelta from nuget.org over every pair.
- **Live parity.** `tools/parity.sh <command> [flags]` runs both binaries over every fixture pair and compares
  stdout, stderr and exit codes.
- **Benchmarks.** Every benchmark case also compares the two binaries' output byte for byte, and the
  [results](/benchmarks) say whether they matched.

## Real-world results

Against oasdiff v1.35.0, OasDelta 1.0.2 gives identical output for `diff`, `breaking` and `changelog -f json` on
the real-world pairs, with one exception. For the two Kubernetes 1.36 → 1.37 pairs, `changelog -f json` lists the
same entries in a different order. That is **oasdiff's own nondeterminism**: those entries tie on every sort key,
and oasdiff's order for them changes from one run to the next. Two consecutive oasdiff runs on the same input
differed in over a thousand positions. OasDelta always produces the same order. The parity report marks these cases
as "reordered".

For Stripe, oasdiff's YAML `diff` is over 5 GB per pair. OasDelta streams it, and the output is identical: for
v2300 → v2500, both write 5,467,926,206 bytes with the same SHA-256. The weekly report skips that command because
of its size.

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
