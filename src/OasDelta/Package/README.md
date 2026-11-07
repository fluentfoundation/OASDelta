# OasDelta

OpenAPI diff and breaking-change detection. OasDelta is a C# port of [oasdiff](https://github.com/oasdiff/oasdiff), compiled to a native executable with .NET NativeAOT. Install it as a .NET tool and no .NET runtime is needed to run it.

- **Drop-in for oasdiff.** Same commands, flags, config files and exit codes: `alias oasdiff=oasdelta`.
- **Byte-for-byte output.** Checked against the Go binary over the upstream test corpus (10,131 comparisons) on every platform it ships for.
- **Fast.** Benchmarked in CI against the official oasdiff release: [latest results](https://fluentfoundation.github.io/OASDelta/benchmarks).

## Install

```sh
dotnet tool install --global OasDelta     # then: oasdelta --help
dnx OasDelta breaking base.yaml revision.yaml   # or run once without installing (.NET 10 SDK)
```

Native builds ship for Linux, Windows and macOS on x64 and Arm64. Standalone binaries are also on [GitHub Releases](https://github.com/fluentfoundation/OASDelta/releases).

## Usage

```sh
oasdelta breaking base.yaml revision.yaml                        # breaking changes, as text
oasdelta changelog base.yaml revision.yaml -f json               # every change, as JSON
oasdelta diff base.yaml revision.yaml                            # structural diff
oasdelta breaking main:openapi.yaml openapi.yaml --fail-on ERR   # base from git; non-zero exit on breaking changes
```

Specs can be files, URLs, `-` for stdin, or git revisions (`ref:path`).

## Links

- [Documentation](https://fluentfoundation.github.io/OASDelta/)
- [Commands and flags](https://fluentfoundation.github.io/OASDelta/guide/usage)
- [Compatibility with oasdiff](https://fluentfoundation.github.io/OASDelta/guide/compatibility)
- [Source and issues](https://github.com/fluentfoundation/OASDelta)

Apache License 2.0. OasDelta is a derivative work of oasdiff and is not affiliated with or endorsed by the oasdiff project.
