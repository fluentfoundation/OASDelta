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
- **Timing.** Wall time of the whole process, from spawn to exit, start-up included, because that is what a CI
  step pays. Each synthetic case gets 3 untimed warm-up runs, then 15 timed runs per binary, alternating oasdiff
  and oasdelta so background noise lands on both. The real-world cases take seconds each, so they get 1 warm-up
  and 5 timed runs. The chart shows the median; the table adds the standard deviation.
- **Memory.** Peak resident set size from `wait4` on Linux; peak working set from `GetProcessMemoryInfo` on
  Windows.
- **Correctness.** Each case compares the two binaries' stdout and exit code byte for byte. A speed-up only
  counts if the answer is the same.

The harness is [`tools/bench.py`](https://github.com/fluentfoundation/OASDelta/blob/main/tools/bench.py), using only
the Python standard library.

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

On Windows, use `-r win-x64` and `out\oasdelta.exe`. Add `--json results.json` to write a file in the format this
site reads; put it in `docs/data/benchmarks/` and run `npm run dev` in `docs/` to see it here.

## Caveats

- Shared CI runners are noisy. The medians are stable from run to run, but treat a difference under about 10% as a
  tie.
- The synthetic inputs are built to exercise every stage of the pipeline, and the real-world pairs show three
  large public APIs. Your API's shape may still favour one implementation more or less.
- Memory is a trade-off, not a free win. oasdelta peaks lower on small specs and on large YAML, but on large JSON
  oasdiff's peak is usually lower. .NET's GC is tuned here for throughput on a short-lived process (see
  [Why C# and .NET](/why-dotnet#garbage-collection-tuned-for-a-batch-process)), so check the memory view before
  running oasdelta in a tight container.
