import fs from 'node:fs'
import { defineLoader } from 'vitepress'

/** One measured side of a case: timings over the timed runs, the highest peak memory seen. */
export interface Measurement {
  median_ms: number
  mean_ms: number
  min_ms: number
  stddev_ms: number
  peak_mb: number
  exit_code: number
}

export interface BenchCase {
  input: string
  input_title: string
  input_bytes: number
  /** "synthetic" (generated or upstream fixtures) or "real-world" (API releases from OASDelta-TestData). Older reports omit it. */
  kind?: 'synthetic' | 'real-world'
  runs?: number
  command: string
  go: Measurement
  cs: Measurement
  /** Go median ÷ C# median: above 1 means oasdelta is faster. */
  speedup: number
  /** Go peak ÷ C# peak: above 1 means oasdelta uses less memory. */
  memory_ratio: number
  /** stdout and exit code byte for byte the same (output === 'identical'). */
  identical: boolean
  /** 'reordered': the same lines or JSON array entries, in another order. Older reports omit it. */
  output?: 'identical' | 'reordered' | 'different'
  /** For a reordered case: how many distinct outputs each binary printed in order_runs runs. */
  order_runs?: number
  go_orders?: number
  cs_orders?: number
}

export interface BenchReport {
  meta: {
    label: string
    os: string
    arch: string
    cpu: string
    cpu_count: number
    go_version: string
    cs_version: string
    go_binary_mb: number
    cs_binary_mb: number
    runs: number
    warmup: number
    date: string
    commit: string
    run_url: string
    real_world_runs?: number
    test_data_commit?: string
    /** What timed the runs, e.g. "hyperfine 2.0.0". Older reports (bench.py's own timer) omit it. */
    harness?: string
    /** How peak memory was measured. */
    memory?: string
    /** Linux: the peak RSS hyperfine reports for a program that does nothing (its own share of every reading). */
    memory_floor_mb?: number | null
  }
  results: BenchCase[]
}

declare const data: BenchReport[]
export { data }

const order = ['Linux', 'Windows', 'macOS']

// Written by tools/bench.py. CI (benchmarks.yml) overwrites these with the results of the run that builds the site.
export default defineLoader({
  watch: ['./benchmarks/*.json'],
  load(files: string[]): BenchReport[] {
    return files
      .map((f) => JSON.parse(fs.readFileSync(f, 'utf-8')) as BenchReport)
      .sort((a, b) => order.indexOf(a.meta.label) - order.indexOf(b.meta.label))
  },
})
