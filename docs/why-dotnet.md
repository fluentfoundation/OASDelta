# Why C# and .NET

oasdiff is good Go. OasDelta is a faithful port of its behaviour, down to the bytes it prints, so the two
programs do the same work. The difference in the [benchmarks](/benchmarks) comes from what the platform lets you do
with that work: how the program starts, how memory is managed, and how cheaply bytes become objects. Modern C#
gives a lot of control over all three without leaving safe, high-level code.

This page explains where the time goes and which parts are .NET, and which are the port's own algorithms.

## NativeAOT: no JIT, no runtime to start

`PublishAot` compiles the whole program ahead of time into one native executable, about 10 MB, with no runtime to
install. There is no JIT warm-up and no IL to load, so a small diff finishes in a few milliseconds. That matters for
the most common use of this tool: one invocation per CI job, per pull request.

The project goes further than the defaults:

```xml
<PublishAot>true</PublishAot>
<OptimizationPreference>Speed</OptimizationPreference>
<InvariantGlobalization>true</InvariantGlobalization>   <!-- no ICU to load -->
<UseSystemResourceKeys>true</UseSystemResourceKeys>      <!-- no resource strings for framework exceptions -->
<StackTraceSupport>false</StackTraceSupport>             <!-- no stack-trace metadata -->
```

Trimming removes everything the program does not reach, so the binary ends up smaller than the Go one.

## Garbage collection tuned for a batch process

A diff run builds two large document trees, compares them, prints, and exits. Nearly the whole heap stays live until
the end, which is the worst case for a collector that keeps trying to reclaim memory along the way.

.NET lets the application choose its GC strategy, and compile the choice into the binary:

- **Non-concurrent GC.** There is no background thread competing with the program for a heap that is almost all
  live.
- **A higher large-object-heap threshold** (`System.GC.LOHThreshold` = 32 MB). Big arrays no longer count against
  the LOH budget, so they no longer trigger full collections.

Go has one concurrent collector, steered by `GOGC` and `GOMEMLIMIT` (or `debug.SetGCPercent` at run time). .NET
offers different collector modes (workstation or server, concurrent or not) and heap-layout settings such as the
LOH threshold. These go in the project file, so the program ships with the collector tuned for its workload. The
trade-off shows up in the memory column: on some inputs oasdelta
holds more memory at peak in exchange for collecting less often.

## Allocation-light parsing

- **JSON fast path.** JSON specs are read straight into the document tree with `System.Text.Json`'s
  `Utf8JsonReader`. That is a forward-only reader over UTF-8 bytes that never builds an intermediate DOM and never
  decodes to UTF-16 strings it doesn't keep.
- **Spans and value types.** `Span<T>`, `stackalloc` and `readonly struct` let hot paths work on slices of existing
  buffers instead of allocating new strings and objects.
- **Hashed duplicate-key detection** for large YAML mappings, where a straightforward port would compare keys
  pairwise.

## Better algorithms, too

Not every gain is the runtime's. Upstream matches templated paths such as `/users/{id}` against each other with a
quadratic scan. OasDelta builds an index (`TemplateIndex` in `Diff/Structure.cs`) and gets the same answer in near
linear time. A Go implementation could do the same. We list it here so the comparison stays honest about where the
time goes.

## See where the time goes

```sh
OASDELTA_TIMING=1 oasdelta changelog base.yaml revision.yaml -f json > /dev/null
```

prints per-phase timings and allocation counts to stderr.
