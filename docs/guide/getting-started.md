# Getting started

OasDelta compares two OpenAPI descriptions and reports what changed and which changes break clients. It is a C#
port of [oasdiff](https://github.com/oasdiff/oasdiff), targets .NET 10, and compiles to a single self-contained
NativeAOT executable, `oasdelta`, with no runtime dependency.

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and NuGet access.

```sh
git clone https://github.com/fluentfoundation/OASDelta.git
cd OASDelta

dotnet build -c Release                                      # JIT build: src/OasDelta/bin/Release/net10.0/oasdelta
dotnet publish src/OasDelta -c Release -r linux-x64 -o out   # NativeAOT: out/oasdelta
dotnet test                                                  # unit tests plus 1,239 golden comparisons
```

For other platforms use `-r win-x64`, `-r osx-arm64`, `-r linux-arm64` and so on. NativeAOT needs the platform
linker: `clang` on Linux, Xcode command line tools on macOS, and the *Desktop development with C++* workload of
Visual Studio on Windows. See [NativeAOT prerequisites](https://aka.ms/nativeaot-prerequisites).

::: tip Use the NativeAOT build
The project disables tiered compilation, which makes the JIT build slow to start. The NativeAOT executable is the
one to ship and the one the benchmarks measure.
:::

Every CI run also uploads the NativeAOT binaries for `linux-x64` and `win-x64` as workflow artifacts.

## First run

```sh
oasdelta breaking base.yaml revision.yaml               # breaking changes, as text
oasdelta changelog base.yaml revision.yaml -f json      # every change, as JSON
oasdelta diff base.yaml revision.yaml                   # structural diff, as YAML
oasdelta summary base.yaml revision.yaml                # counts of what changed
```

## Fail a CI build on breaking changes

```sh
oasdelta breaking main:openapi.yaml openapi.yaml --fail-on ERR
```

`main:openapi.yaml` reads the base from a git revision. The command exits with a non-zero status when it finds
breaking changes at level `ERR` or above. In GitHub Actions, `-f githubactions` turns each finding into an
annotation on the pull request:

```yaml
- run: oasdelta breaking origin/main:openapi.yaml openapi.yaml -f githubactions --fail-on ERR
```

## Already using oasdiff?

```sh
alias oasdiff=oasdelta
```

OasDelta reads the same config files (`.oasdiff.yaml` and friends) and accepts the same flags. See
[Compatibility](./compatibility) for the handful of features that are not ported.
