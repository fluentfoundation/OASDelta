---
layout: home

hero:
  name: OasDelta
  text: OpenAPI breaking-change detection, compiled with .NET
  tagline: A C# port of oasdiff. Same commands, same flags, same output, in a single NativeAOT executable that runs measurably faster.
  image:
    src: /logo.svg
    alt: OasDelta
  actions:
    - theme: brand
      text: Get started
      link: /guide/getting-started
    - theme: alt
      text: See the benchmarks
      link: /benchmarks
    - theme: alt
      text: GitHub
      link: https://github.com/fluentfoundation/OASDelta

features:
  - title: Drop-in for oasdiff
    details: "Argument-compatible with oasdiff: <code>alias oasdiff=oasdelta</code> keeps your scripts, config files and CI checks working, with the same exit codes."
  - title: Byte-for-byte output
    details: Every command and format is checked against the Go binary over the upstream test corpus, 10,395 golden comparisons on every commit, on Linux and Windows.
  - title: Faster than oasdiff
    details: NativeAOT start-up, a tuned GC compiled into the binary and allocation-light parsing. CI benchmarks synthetic specs and real releases of the GitHub, Stripe and Kubernetes APIs, on Linux and Windows, against the official oasdiff release.
  - title: One self-contained file
    details: About 10 MB with no runtime to install. Builds for Linux, Windows and macOS on x64 and Arm64.
---

## Measured on every push

These numbers come from the latest run of the
[`Benchmarks & site`](https://github.com/fluentfoundation/OASDelta/actions/workflows/benchmarks.yml) workflow:
the NativeAOT build of oasdelta against the official oasdiff release binary, on GitHub-hosted runners.
[Full results and methodology →](/benchmarks)

<BenchHeadline />
