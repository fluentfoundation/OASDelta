# Compatibility with oasdiff

OasDelta aims to be a drop-in replacement: the same input should produce the same bytes on stdout and the same exit
code as the Go binary.

## How it is checked

- **Golden tests.** `tests/golden` holds the output of **oasdiff v1.35.0** for 33 command, format, language and
  option combinations ([`tools/golden-commands.txt`](https://github.com/fluentfoundation/OASDelta/blob/main/tools/golden-commands.txt)):
  every output format of `diff`, `summary`, `changelog` and `breaking`; `--lang` es, pt-br and ru; and option sets
  such as `--level`, `--fail-on`, `--flatten-params`, `--include-path-params`, `--match-path`, `--prefix-revision`
  and deprecation days. They run over 327 fixture pairs: the upstream test data paired by file name, the pairs
  upstream's own checker tests compare (`tests/extra-pairs.txt`), and regression cases found by the property-based
  tests. That is 10,791 comparisons, in which 384 of the 617 checker rules fire. `dotnet test` replays them in
  process. CI replays them with the NativeAOT binary
  (`tools/check_golden.sh`) on Linux and Windows, and the release workflow replays them on all six release
  platforms. The few cases that are expected to differ are listed in `tests/golden-known.txt`, each with its
  issue. CI fails if a listed case starts to match, so the list cannot go stale.
- **Property-based tests.** [CsCheck](https://github.com/AnthonyLloyd/CsCheck) generates OpenAPI 3.0 and 3.1
  documents, with names and values that are awkward for YAML, JSON, Markdown and HTML, and applies random edits to
  get a revision: types, formats, enums, bounds, required, deprecation and stability, composition, parameters,
  headers, media types, responses, security, paths. A nightly
  [workflow](https://github.com/fluentfoundation/OASDelta/actions/workflows/properties.yml) runs both binaries on
  thousands of such pairs, with command lines drawn from the golden set, and fails on any difference, shrunk to a
  minimal case. Pairs that make a rule fire that no golden does are kept as new fixtures. Properties that need no
  oracle run with every `dotnet test`: a document has no changes against itself, YAML and JSON input give the same
  changes, `breaking` is the changelog's errors and warnings, differences are symmetric, and emitting, parsing and
  emitting again is stable. Each of these holds for oasdiff too.
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
- **`$ref` resolution (kin-openapi):** what a `$ref` may point at. A `$ref` must reach the kind of object it stands
  for (a parameter pointing at a schema fails with `bad data in "#/components/schemas/Limit" (expecting ref to
  parameter object)`). It may go through another `$ref` (`#/components/schemas/Alias/properties/id`), and `#` names
  a whole file that is itself a schema. Where kin-openapi's resolver is buggy, the port departs from it (below).

## Known differences

- **Program name.** Help text, `--version` and command-line errors say `oasdelta`. Changelog and diff output are
  unchanged.
- **Regular expressions.** `--match-path` and the other regex flags use .NET regular expressions instead of RE2.
  Common syntax errors are reworded to match Go's messages.
- **Extension order in the text diff report.** Upstream lists modified extensions in Go map order, which is
  random; the port sorts them.
- **The name of a schema two components share.** With `Owner: {$ref: '#/components/schemas/Error'}`, both names
  belong to one schema. Which one oasdiff shows in a change's "shared schema" detail depends on Go map order and
  changes from run to run. The port always shows the first in sorted order, one of the names oasdiff shows.
- **Template errors.** Messages for broken custom templates are shorter than Go's.
- **Missing files.** A spec or `$ref`'d file that doesn't exist is reported with Go's Linux wording
  (`open sub/defs.yaml: no such file or directory`) on every OS. On Windows, oasdiff prints the Windows text and a
  backslash path instead.
- **Bugs in kin-openapi's `$ref` resolver.** When a `$ref`'s pointer doesn't resolve in the typed document,
  kin-openapi 0.151.0 follows it through the file's plain data, which is what lets it reach extensions. The port
  does the same, without three bugs in that code:
  - **The wrong file.** kin follows the pointer through the file that holds the `$ref`, not the file the `$ref`
    names. With `$ref: './defs.yaml#/components/schemas/Pet'` and no `Pet` in `defs.yaml`, oasdiff silently uses the
    referencing file's own `Pet` and reports changes against it. The port fails to load the spec (exit 102):
    `failed to resolve "Pet" in fragment in URI: "./defs.yaml#/components/schemas/Pet": map key "Pet" not found`.
  - **Lost errors.** The fallback overwrites kin's own error. oasdiff prints only the last step (`map key "Pet" not
    found`, without the `$ref` or the file), and a pointer through a null (`#/x-defs/A/B` with `A: null`) gets no
    error at all. The port prints the error kin builds and then loses, as above, and fails on a pointer through a
    null.
  - **`#` on an OpenAPI document.** `#` is meant for a file that is itself the object (`$ref: 'pet.yaml#'`). On an
    OpenAPI document, kin builds the object from the document's `x-` keys, so a schema `$ref: '#'` silently becomes
    the "any" schema. The port fails with `bad data in "#" (expecting ref to schema object)`.

  A `$ref` to a `null` still resolves to nothing, as in oasdiff (for a schema, `diff failed: revision schema value
  is nil`).
- **Crashes.** Where oasdiff crashes, the port reports an error or a result instead.
  - A parameter or response that is only a reference to itself (`P: {$ref: '#/components/parameters/P'}`) makes
    oasdiff 1.35.0 panic while it lowercases header names (exit 2, with a Go stack trace). The port fails with
    `diff failed: parameter reference is nil` (exit 104), which is what oasdiff reports for it with
    `--case-insensitive-headers=false`.
  - An array whose items are the array itself (`Item: {type: array, items: {$ref: '#/components/schemas/Item'}}`)
    makes `changelog` and `breaking` in oasdiff 1.35.0 run out of stack when a type change involves it
    (`fatal error: stack overflow`, exit 2). The port reports the change and shows the repeated schema as `…`:
    ``the request's body `type` changed from `array<…>` to `string` ``.
  - A `null` in a few places makes oasdiff 1.35.0 panic with a nil pointer dereference (exit 2). The port reads
    them as follows:
    - an entry of `tags` or `servers` is skipped, so `tags: [null]` compares like `tags: []`
    - a server variable (`variables: {h: null}`) and a link that is a `$ref` to a null `components.links` entry
      are kept as empty objects and compared like any other.
  - A `$ref` to a field that isn't there (`#/components/schemas/Pet/not`, with no `not` in `Pet`) makes oasdiff
    1.35.0 panic with a nil pointer dereference (exit 2). The port fails to load the spec (exit 102): `failed to
    resolve "not" in fragment in URI: "#/components/schemas/Pet/not"`.

## Not ported

- `--flatten-allof` (and the hidden `--flatten`): accepted, then reported as unsupported with exit code 101.
- `--auto-upgrade` and `--open` (which uploads to oasdiff.com): reported as unsupported.
- Commands: `validate`, `upgrade`, `flatten`, `breaking-files`, `git-diff-driver`, `checks validate`,
  `checks changelog coverage`, `completion`.
- Output format: `sarif`.
