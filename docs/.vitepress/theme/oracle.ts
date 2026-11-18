import type { Night } from '../../data/oracle.data'

export const repo = 'https://github.com/fluentfoundation/OASDelta'

/** UTC, the same on the server and in the browser (no hydration mismatch). */
export const fmtDate = (iso: string) => `${iso.slice(0, 10)} ${iso.slice(11, 16)} UTC`
export const fmtInt = (n: number) => n.toLocaleString('en-US')
export const fmtMinutes = (seconds: number) => `${Math.round(seconds / 60)} min`
export const short = (sha: string | null) => (sha ?? '').slice(0, 7)

export type State = 'pass' | 'fail' | 'none'

/** What a night came to, as a state (for the icon) and words (always shown: never colour alone). */
export function status(n: Night): { state: State; label: string } {
  const r = n.report
  if (!r) {
    if (n.conclusion === 'success') return { state: 'pass', label: 'Passed (no report)' }
    if (n.conclusion === 'failure') return { state: 'fail', label: 'Failed (no report)' }
    return { state: 'none', label: `${n.conclusion.replace('_', ' ')} (no report)` }
  }
  if (r.outcome === 'pass') return { state: 'pass', label: 'Matched oasdiff' }
  const k = r.mismatches.length
  if (k > 0) return { state: 'fail', label: `${k} kind${k === 1 ? '' : 's'} of mismatch` }
  return { state: 'fail', label: 'Failed' }
}

export const icon: Record<State, string> = { pass: '✓', fail: '✗', none: '–' }
