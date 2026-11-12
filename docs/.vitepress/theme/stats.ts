import type { BenchCase, BenchReport } from '../../data/benchmarks.data'

export const geomean = (xs: number[]) =>
  xs.length ? Math.exp(xs.reduce((s, x) => s + Math.log(x), 0) / xs.length) : NaN

export function summarize(r: BenchReport) {
  const cases = r.results
  const real = cases.filter((c) => c.kind === 'real-world')
  const large = cases.filter((c) => c.input !== 'small' && c.kind !== 'real-world')
  const small = cases.filter((c) => c.input === 'small')
  const speedups = cases.map((c) => c.speedup)
  return {
    cases: cases.length,
    speedup: geomean(speedups),
    speedupReal: geomean(real.map((c) => c.speedup)),
    realCases: real.length,
    speedupLarge: geomean(large.map((c) => c.speedup)),
    speedupSmall: geomean(small.map((c) => c.speedup)),
    minSpeedup: Math.min(...speedups),
    maxSpeedup: Math.max(...speedups),
    faster: cases.filter((c) => c.speedup > 1).length,
    leaner: cases.filter((c) => c.memory_ratio > 1).length,
    memory: geomean(cases.map((c) => c.memory_ratio)),
    identical: cases.filter((c) => c.identical).length,
    reordered: cases.filter((c) => c.output === 'reordered'),
  }
}

/** "4 orders in 5 runs" for one side of a reordered case. */
const orders = (n: number, runs: number) => `${n} ${n === 1 ? 'order' : 'orders'} in ${runs} runs`

/** Why some cases are not byte for byte identical, when they have the same entries in another order. Built from
 * what the benchmark recorded, so it only claims what the run saw. */
export function reorderNote(reordered: BenchCase[]): string {
  if (!reordered.length) return ''
  const names = reordered.map((c) => `${c.input} ${c.command}`)
  const list = names.length === 1 ? names[0] : `${names.slice(0, -1).join(', ')} and ${names.at(-1)}`
  let note = `${reordered.length === 1 ? 'One case' : `${reordered.length} cases`} (${list}) ` +
    `${reordered.length === 1 ? 'prints' : 'print'} the same entries as oasdiff in a different order.`
  const measured = reordered.filter((c) => c.order_runs && c.go_orders && c.cs_orders)
  if (measured.length !== reordered.length) return note
  if (measured.every((c) => c.go_orders! > 1 && c.cs_orders === 1)) {
    note += ' oasdiff’s own order there changes from run to run (' +
      measured.map((c) => orders(c.go_orders!, c.order_runs!)).join(', ') +
      '); OasDelta’s does not. oasdiff sorts changes with a comparison that ignores some of their fields, and ' +
      'leaves changes that differ only in those fields in Go’s random map order.'
  } else {
    note += ' Distinct outputs over repeated runs: ' +
      measured.map((c) => `${c.input} ${c.command}: oasdiff ${orders(c.go_orders!, c.order_runs!)}, ` +
        `OasDelta ${orders(c.cs_orders!, c.order_runs!)}`).join('; ') + '.'
  }
  return note
}

/** The Output column of a case. */
export const outputLabel = (c: BenchCase) =>
  c.output === 'reordered' ? 'same entries, other order' : c.identical ? 'identical' : 'differs'

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
