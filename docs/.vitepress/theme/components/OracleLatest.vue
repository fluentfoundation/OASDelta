<script setup lang="ts">
import { computed } from 'vue'
import { data } from '../../../data/oracle.data'
import { repo, fmtDate, fmtInt, fmtMinutes, short, status, icon } from '../oracle'

const night = computed(() => data[0] ?? null)
const r = computed(() => night.value?.report ?? null)
const s = computed(() => (night.value ? status(night.value) : null))
const counted = computed(() =>
  r.value
    ? [
        [r.value.reordered, 'command lines with the same output in another order (oasdiff\'s own nondeterminism)'],
        [r.value.alias_named, 'command lines that differ only in which name of an aliased schema is the shared one (Go map order)'],
        [r.value.load_errors, 'cases neither tool could load (same exit code)'],
        [r.value.oasdiff_crashes, 'command lines on which oasdiff crashed (OasDelta gave an error or a result)'],
        [r.value.dangling_mappings, 'cases skipped: the revision\'s discriminator mapping points at nothing (a known difference)'],
      ] as [number, string][]
    : [],
)
</script>

<template>
  <div class="oracle">
    <p v-if="!night" class="empty">
      The first report appears here after the next scheduled run of the
      <a :href="`${repo}/actions/workflows/properties.yml`">Properties</a> workflow.
    </p>
    <template v-else>
      <p class="headline">
        <span :class="['badge', s!.state]"><span class="icon" aria-hidden="true">{{ icon[s!.state] }}</span>{{ s!.label }}</span>
        <span class="when">{{ fmtDate(night.date) }}</span>
      </p>

      <div v-if="r" class="tiles">
        <div class="tile">
          <div class="label">Generated pairs compared</div>
          <div class="value">{{ fmtInt(r.cases) }}</div>
          <div class="note">{{ r.command_lines_per_case }} command lines each, {{ fmtInt(r.cases * r.command_lines_per_case) }} in all</div>
        </div>
        <div class="tile">
          <div class="label">Kinds of mismatch</div>
          <div class="value">{{ r.mismatches.length }}</div>
          <div class="note">{{ r.mismatches.length ? 'each shrunk to a small case, below' : 'every command line printed what oasdiff printed' }}</div>
        </div>
        <div class="tile">
          <div class="label">Checker rules fired</div>
          <div class="value">{{ fmtInt(r.rules_fired) }}</div>
          <div class="note">{{ r.new_rules.length }} that no golden fires</div>
        </div>
        <div class="tile">
          <div class="label">Time</div>
          <div class="value">{{ fmtMinutes(r.seconds) }}</div>
          <div class="note">searching, then shrinking each kind</div>
        </div>
      </div>
      <p v-else class="empty">
        This run wrote no report: it ran before reports were kept, or stopped before comparing. The
        <a :href="night.url">run</a> has the details.
      </p>

      <p class="meta">
        Commit <a :href="`${repo}/commit/${night.commit}`"><code>{{ short(night.commit) }}</code></a>
        <template v-if="r?.oasdiff_version"> · oasdiff {{ r.oasdiff_version }}</template>
        · <a :href="night.url">workflow run</a>
      </p>

      <template v-if="r?.mismatches.length">
        <h3>Mismatches</h3>
        <div class="scroll">
          <table>
            <thead>
              <tr><th>Kind of mismatch</th><th class="num">Seen again</th><th>Seed</th><th>Shrunk from its seed</th></tr>
            </thead>
            <tbody>
              <tr v-for="m in r.mismatches" :key="m.kind">
                <td>{{ m.kind }}</td>
                <td class="num">{{ fmtInt(m.seen_again) }}</td>
                <td><code v-if="m.seed">{{ m.seed }}</code><span v-else class="muted">none</span></td>
                <td>{{ m.shrunk ? 'yes' : 'no' }}</td>
              </tr>
            </tbody>
          </table>
        </div>
        <p class="hint">
          <em>Seen again</em> counts command lines with that kind of mismatch after it was first found.
          <em>Shrunk from its seed</em> is "no" when no smaller case turned up; the case is still the smallest found
          while searching. A kind with no seed was first seen while another kind was being shrunk.
        </p>
        <details v-for="m in r.mismatches" :key="m.kind + '-case'">
          <summary>{{ m.kind }}</summary>
          <pre>{{ m.case }}</pre>
        </details>
      </template>

      <template v-if="r?.failure">
        <h3>Failure</h3>
        <details open>
          <summary>The run stopped on something other than a mismatch</summary>
          <pre>{{ r.failure }}</pre>
        </details>
      </template>

      <template v-if="r?.new_rules.length">
        <h3>Rules no golden fires</h3>
        <p class="hint">
          Pairs that make these rules fire, with oasdelta and oasdiff agreeing, are kept in the run's
          <code>properties</code> artifact as fixture candidates.
        </p>
        <div class="scroll">
          <table>
            <thead><tr><th>Rule</th><th class="num">Pairs that fired it</th></tr></thead>
            <tbody>
              <tr v-for="n in r.new_rules" :key="n.id"><td><code>{{ n.id }}</code></td><td class="num">{{ fmtInt(n.fired) }}</td></tr>
            </tbody>
          </table>
        </div>
      </template>

      <template v-if="r">
        <h3>Also counted</h3>
        <ul class="counted">
          <li v-for="[n, what] in counted" :key="what"><b>{{ fmtInt(n) }}</b> {{ what }}</li>
        </ul>
      </template>
    </template>
  </div>
</template>

<style scoped>
.oracle { margin: 24px 0; }
.headline { display: flex; flex-wrap: wrap; align-items: baseline; gap: 12px; margin: 0 0 16px; }
.badge { display: inline-flex; align-items: baseline; gap: 6px; font-weight: 600; font-size: 18px; color: var(--vp-c-text-1); }
.icon { font-size: 18px; }
.badge.pass .icon { color: var(--vp-c-success-1); }
.badge.fail .icon { color: var(--vp-c-danger-1); }
.badge.none .icon { color: var(--vp-c-text-3); }
.when { color: var(--vp-c-text-2); font-size: 14px; font-variant-numeric: tabular-nums; }
.tiles { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; }
.tile { background: var(--vp-c-bg-soft); border-radius: 12px; padding: 16px; min-width: 0; }
.label { font-size: 13px; color: var(--vp-c-text-2); line-height: 1.3; }
.value { font-size: 30px; font-weight: 600; color: var(--vp-c-text-1); line-height: 1.2; margin: 6px 0 4px; font-variant-numeric: tabular-nums; }
.note { font-size: 12px; color: var(--vp-c-text-3); line-height: 1.35; }
.meta { font-size: 14px; color: var(--vp-c-text-2); margin: 16px 0 0; }
.empty { color: var(--vp-c-text-2); }
.muted { color: var(--vp-c-text-3); }
h3 { margin: 32px 0 8px; font-size: 18px; }
.scroll { overflow-x: auto; }
table { margin: 8px 0; }
.num { text-align: right; font-variant-numeric: tabular-nums; }
.hint { font-size: 13px; color: var(--vp-c-text-2); line-height: 1.5; }
details { margin: 8px 0; border: 1px solid var(--vp-c-divider); border-radius: 8px; padding: 8px 12px; }
summary { cursor: pointer; font-weight: 500; }
pre { margin: 8px 0 0; max-height: 480px; overflow: auto; font-size: 12px; line-height: 1.45; white-space: pre; background: var(--vp-c-bg-soft); padding: 12px; border-radius: 6px; }
.counted { font-size: 14px; color: var(--vp-c-text-2); }
.counted b { color: var(--vp-c-text-1); font-variant-numeric: tabular-nums; }
</style>
