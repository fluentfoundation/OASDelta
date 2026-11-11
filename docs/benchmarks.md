# Benchmarks

OasDelta against oasdiff, measured on GitHub-hosted runners by the
[`Benchmarks & site`](https://github.com/fluentfoundation/OASDelta/actions/workflows/benchmarks.yml) workflow on
every push to `main` and weekly. The numbers on this page come from the run that built it.

<BenchHeadline />

## Results

<BenchChart />

## What is measured

- **Binaries.** oasdelta is the NativeAOT build that `dotnet publish` produces from this repository. oasdiff is the
  **official release binary** downloaded from [oasdiff/oasdiff releases](https://github.com/oasdiff/oasdiff/releases),
  with its checksum verified. Neither is rebuilt or tuned for the benchmark.
- **Inputs.**
  - *Small:* `openapi-test1.yaml` → `openapi-test3.yaml` from the upstream test data, about 10 KB. This case is
    dominated by process start-up.
  - *Large YAML / JSON:* a synthetic API from [`tools/gen_bench_spec.py`](https://github.com/fluentfoundation/OASDelta/blob/main/tools/gen_bench_spec.py)
    with 600 resources (1,200 paths, 2,400 operations, 650 schemas). The revision changes about one resource in
    ten: removed and added properties, type and enum changes, new required fields, removed endpoints and
    responses. So every stage, from parsing through diffing and checks to rendering, has work to do.
  - *Real-world:* two consecutive releases each of the GitHub REST API (YAML, ~10 MB), Stripe (JSON, ~8 MB) and
    Kubernetes `core/v1` (JSON, ~2 MB), from
    [OASDelta-TestData](https://github.com/fluentfoundation/OASDelta-TestData). Each source's license is recorded
    there.
- **Commands.** `diff`, `summary`, `breaking`, `changelog -f json` and `changelog -f html` on the small and synthetic
  inputs. `diff`, `breaking` and `changelog -f json` on the real-world pairs, except Stripe's `diff`, whose YAML
  output is over 5 GB.
- **Timing.** [hyperfine](https://github.com/sharkdp/hyperfine) 2.0 measures the wall time of the whole process,
  from spawn to exit, start-up included, because that is what a CI step pays. It starts each binary directly, with
  no shell in between, and discards its output. Each synthetic case gets 3 untimed warm-up runs, then 15 timed runs
  per binary. The real-world cases take seconds each, so they get 1 warm-up and 5 timed runs. hyperfine runs one
  binary's runs and then the other's, and which binary goes first alternates from case to case. The chart shows the
  median; the table adds the standard deviation.
- **Memory.** On Linux, peak resident set size as hyperfine measures it, the highest over the timed runs. A process
  that hyperfine starts inherits hyperfine's own peak of a few MB, so no reading is lower than that. The environment
  table shows the reading for a program that does nothing. hyperfine cannot measure memory on Windows, so there
  each binary runs 3 more times, untimed, and the peak working set comes from `GetProcessMemoryInfo`, which counts
  only the process itself.
- **Correctness.** Before timing, each binary runs once with its output saved to a file, and the two outputs and
  exit codes are compared byte for byte. A speed-up only counts if the answer is the same. Where the bytes differ
  but the lines, or the JSON array entries, are the same in another order, the case is reported as *same entries,
  other order*. Both binaries then run 5 times each, and the report records how many different outputs each one
  printed.

The harness is [`tools/bench.py`](https://github.com/fluentfoundation/OASDelta/blob/main/tools/bench.py). It runs the
output check and drives hyperfine, measuring no time itself.

### Environment

<BenchMeta />

### All numbers

<BenchTable />

## Reproduce it

```sh
dotnet publish src/OasDelta -c Release -r linux-x64 -o out
mkdir specs && python3 tools/gen_bench_spec.py specs 600
python3 tools/bench.py --go "$(command -v oasdiff)" --cs out/oasdelta --specs specs --runs 15
```

This needs [hyperfine](https://github.com/sharkdp/hyperfine/releases) 2.0 or later on `PATH`, or passed with
`--hyperfine`. On Windows, use `-r win-x64` and `out\oasdelta.exe`. Add `--json results.json` to write a file in the format this
site reads; put it in `docs/data/benchmarks/` and run `npm run dev` in `docs/` to see it here.

## Caveats

- Shared CI runners are noisy. The medians are stable from run to run, but treat a difference under about 10% as a
  tie.
- Until November 2026 the harness timed runs itself in Python and read each binary's output through a pipe, so
  those numbers included reading the output. On Linux its memory readings were too high: they included memory held
  by the Python process that started each binary, which kept every output it had read.
- The synthetic inputs are built to exercise every stage of the pipeline, and the real-world pairs show three
  large public APIs. Your API's shape may still favour one implementation more or less.
- Memory is a trade-off, not a free win. oasdelta peaks lower on small specs and on large YAML, but on large JSON
  oasdiff's peak is usually lower. .NET's GC is tuned here for throughput on a short-lived process (see
  [Why C# and .NET](/why-dotnet#garbage-collection-tuned-for-a-batch-process)), so check the memory view before
  running oasdelta in a tight container.
