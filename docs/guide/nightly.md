# Nightly comparison with oasdiff

Once a day the [Properties](https://github.com/fluentfoundation/OASDelta/actions/workflows/properties.yml) workflow
generates OpenAPI document pairs, runs OasDelta and the official oasdiff release on each, and checks that they print
the same thing. This page shows the most recent scheduled run, and is rebuilt each time one ends.

<OracleLatest />

## Recent nights

<OracleHistory />

## How a night runs

- **Pairs.** [CsCheck](https://github.com/AnthonyLloyd/CsCheck) generates an OpenAPI 3.0 or 3.1 document, with names
  and values that are awkward for YAML, JSON, Markdown and HTML, and applies random edits to make the revision:
  types, formats, enums, bounds, required, deprecation and stability, composition, parameters, headers, media types,
  responses, security, paths.
- **Command lines.** Each pair is given three command lines drawn from the 33 the goldens use
  ([`tools/golden-commands.txt`](https://github.com/fluentfoundation/OASDelta/blob/main/tools/golden-commands.txt)),
  so the output formats, languages and options vary too. OasDelta runs in process; oasdiff is the release binary,
  checksum verified.
- **Agreement.** The exit code and stdout must be the same, byte for byte. Where oasdiff's own output varies from run
  to run (Go's random map order), the same lines or JSON entries in another order also count as the same. Those
  command lines are counted under *Also counted*.
- **Kinds of mismatch.** A mismatch is grouped by command and output format, and by what differs: the exit code, only
  the source locations (`baseSource`, `revisionSource`), or the output itself.
- **Shrinking.** The first mismatch of each kind is shrunk to a small case, and the search goes on, so a known
  mismatch does not hide new ones. When time is up, each kind is shrunk further from its seed for three minutes.
- **Result.** The run fails if anything mismatched, and its summary lists every kind with its shrunk case.
- **New rules.** A pair that makes a checker rule fire that no golden fires is kept as a fixture candidate, once
  oasdiff agrees on it. The [coverage page](./coverage) lists the rules no golden fires.

The run's `properties` artifact keeps each case's two documents and the edits that made the revision.

## Replaying a case

Run the Properties workflow from the Actions tab with the seed from the table, or replay it locally with an oasdiff
binary of the version in
[`tools/oasdiff-version.txt`](https://github.com/fluentfoundation/OASDelta/blob/main/tools/oasdiff-version.txt):

::: code-group

```sh [bash]
OASDIFF=/path/to/oasdiff CsCheck_Seed=<seed> \
  dotnet test tests/OasDelta.Tests -c Release --filter Category=Oracle
```

```powershell [PowerShell]
$env:OASDIFF = 'C:\path\to\oasdiff.exe'; $env:CsCheck_Seed = '<seed>'
dotnet test tests/OasDelta.Tests -c Release --filter Category=Oracle
```

:::
