import type { BenchCase, BenchReport } from '../../data/benchmarks.data'

export const geomean = (xs: number[]) =>
  xs.length ? Math.exp(xs.reduce((s, x) => s + Math.log(x), 0) / xs.length) : NaN

export function summarize(r: BenchReport) {
  const cases = r.results
  const large = cases.filter((c) => c.input !== 'small')
  const small = cases.filter((c) => c.input === 'small')
  const speedups = cases.map((c) => c.speedup)
  return {
    cases: cases.length,
    speedup: geomean(speedups),
    speedupLarge: geomean(large.map((c) => c.speedup)),
    speedupSmall: geomean(small.map((c) => c.speedup)),
    minSpeedup: Math.min(...speedups),
    maxSpeedup: Math.max(...speedups),
    faster: cases.filter((c) => c.speedup > 1).length,
    leaner: cases.filter((c) => c.memory_ratio > 1).length,
    memory: geomean(cases.map((c) => c.memory_ratio)),
    identical: cases.filter((c) => c.identical).length,
  }
}

export const fmtX = (x: number) => `${x >= 10 ? x.toFixed(0) : x.toFixed(1)}×`
export const fmtMs = (ms: number) =>
  ms >= 1000 ? `${(ms / 1000).toFixed(2)} s` : ms >= 100 ? `${ms.toFixed(0)} ms` : `${ms.toFixed(1)} ms`
export const fmtMb = (mb: number) => `${mb.toFixed(mb >= 100 ? 0 : 1)} MB`

/** "2.4× faster" / "1.3× slower" from a Go÷C# ratio. */
export const relTime = (ratio: number) => (ratio >= 1 ? `${fmtX(ratio)} faster` : `${fmtX(1 / ratio)} slower`)
export const relMem = (ratio: number) => (ratio >= 1 ? `${fmtX(ratio)} less` : `${fmtX(1 / ratio)} more`)

export const inputs = (r: BenchReport) => {
  const seen = new Map<string, BenchCase[]>()
  for (const c of r.results) {
    if (!seen.has(c.input)) seen.set(c.input, [])
    seen.get(c.input)!.push(c)
  }
  return [...seen.entries()].map(([key, cases]) => ({ key, title: cases[0].input_title, cases }))
}
