import fs from 'node:fs'
import path from 'node:path'
import { defineLoader } from 'vitepress'

/** One kind of mismatch the night found: how often it came back, and its smallest case. */
export interface OracleMismatch {
  /** e.g. "output: diff -f json", "source locations only: changelog -f json", "exit code 1 where oasdiff exits 0: …" */
  kind: string
  /** Command lines with this kind of mismatch after it was first found. */
  seen_again: number
  /** The CsCheck seed that replays the case; null for a kind first seen while another was being shrunk. */
  seed: string | null
  /** Shrinking from the seed found a smaller case. */
  shrunk: boolean
  /** The command line and both outputs. Older reports omit it: their case, clipped, may not reach the outputs. */
  difference?: string | null
  /** The case as CsCheck reports it: the pair, its edits and the command lines (clipped). */
  case: string
}

/** oracle.json, written by tests/OasDelta.Tests/Properties/OracleProperties.cs. */
export interface OracleReport {
  date: string
  seconds: number
  outcome: 'pass' | 'fail'
  seed_replayed: string | null
  commit: string | null
  run_url: string | null
  oasdiff_version: string | null
  cases: number
  command_lines_per_case: number
  reordered: number
  alias_named: number
  load_errors: number
  oasdiff_crashes: number
  dangling_mappings: number
  /** Absent in reports from before 2026-11-21. */
  path_item_refs?: number
  rules_fired: number
  new_rules: { id: string; fired: number }[]
  mismatches: OracleMismatch[]
  /** A failure that is no kind of mismatch (OasDelta crashed); null otherwise. */
  failure: string | null
}

/** One scheduled run of the Properties workflow, and its report if it wrote one. */
export interface Night {
  id: number
  /** The run's conclusion: success, failure, cancelled, timed_out… */
  conclusion: string
  date: string
  commit: string
  url: string
  report: OracleReport | null
}

declare const data: Night[]
export { data }

const dir = path.resolve(__dirname, 'oracle')

// Written by tools/oracle-reports.sh, which benchmarks.yml runs before building the site (and again when a nightly
// run ends). Nothing is committed: without the directory, the page says there are no reports yet.
export default defineLoader({
  watch: ['./oracle/**/*.json'],
  load(): Night[] {
    const list = path.join(dir, 'runs.json')
    if (!fs.existsSync(list)) return []
    const runs = JSON.parse(fs.readFileSync(list, 'utf-8')) as
      { databaseId: number; conclusion: string; createdAt: string; headSha: string; url: string }[]
    return runs
      .map((r) => {
        const file = path.join(dir, String(r.databaseId), 'oracle.json')
        return {
          id: r.databaseId,
          conclusion: r.conclusion,
          date: r.createdAt,
          commit: r.headSha,
          url: r.url,
          report: fs.existsSync(file) ? (JSON.parse(fs.readFileSync(file, 'utf-8')) as OracleReport) : null,
        }
      })
      .sort((a, b) => b.date.localeCompare(a.date))
  },
})
