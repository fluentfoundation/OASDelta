import fs from 'node:fs'
import path from 'node:path'
import { defineLoader } from 'vitepress'

/** One checker rule, as `oasdiff checks changelog -f json` lists it, and where the goldens fire it. */
export interface RuleCoverage {
  id: string
  level: string
  direction: string
  area: string
  kind: string
  description: string
  /** How many golden comparisons (JSON outputs) fire the rule. */
  comparisons: number
  /** How many fixture pairs fire it in at least one of them. */
  pairs: number
  /** One golden output that fires it, relative to tests/golden; null when none does. */
  example: string | null
}

export interface Coverage {
  rules: RuleCoverage[]
  fired: number
  /** Golden comparisons in all, JSON or not. */
  comparisons: number
  /** The pairs and command sets the goldens cover. */
  fixturePairs: number
  sets: number
}

declare const data: Coverage
export { data }

const golden = path.resolve(__dirname, '../../tests/golden')
const ruleId = /"id": ?"([a-z0-9-]+)"/g

// Read at build time from the goldens themselves: tests/golden/checks-changelog.json is oasdiff's rule list (the port's
// is the same, checked by CheckerRulesMatchOasdiff), and the JSON outputs say which rules fire. Nothing to regenerate.
export default defineLoader({
  watch: ['../../tests/golden/checks-changelog.json'],
  load(): Coverage {
    const list = JSON.parse(fs.readFileSync(path.join(golden, 'checks-changelog.json'), 'utf-8')) as Omit<RuleCoverage, 'comparisons' | 'pairs' | 'example'>[]
    const seen = new Map<string, { comparisons: number; pairs: Set<string>; example: string }>()
    let comparisons = 0
    const pairNames = new Set<string>()
    const sets = fs.readdirSync(golden, { withFileTypes: true }).filter((d) => d.isDirectory()).map((d) => d.name).sort()
    for (const set of sets) {
      for (const file of fs.readdirSync(path.join(golden, set)).sort()) {
        if (!file.endsWith('.out')) continue
        comparisons++
        const pair = file.slice(0, -4)
        pairNames.add(pair)
        const text = fs.readFileSync(path.join(golden, set, file), 'utf-8')
        if (!text.includes('"id"')) continue
        for (const id of new Set(Array.from(text.matchAll(ruleId), (m) => m[1]))) {
          const s = seen.get(id) ?? { comparisons: 0, pairs: new Set<string>(), example: `${set}/${file}` }
          s.comparisons++
          s.pairs.add(pair)
          seen.set(id, s)
        }
      }
    }
    const byId = new Map<string, RuleCoverage>()
    for (const r of list) {
      if (byId.has(r.id)) continue
      const s = seen.get(r.id)
      byId.set(r.id, {
        id: r.id, level: r.level, direction: r.direction, area: r.area, kind: r.kind, description: r.description,
        comparisons: s?.comparisons ?? 0, pairs: s?.pairs.size ?? 0, example: s?.example ?? null,
      })
    }
    const rules = [...byId.values()].sort((a, b) => a.area.localeCompare(b.area) || a.id.localeCompare(b.id))
    return { rules, fired: rules.filter((r) => r.comparisons > 0).length, comparisons, fixturePairs: pairNames.size, sets: sets.length }
  },
})
