<script setup lang="ts">
import { data } from '../../../data/oracle.data'
import { repo, fmtDate, fmtInt, short, status, icon } from '../oracle'

const rows = data.map((n) => ({ n, s: status(n) }))
</script>

<template>
  <div class="history">
    <p v-if="!rows.length" class="empty">No scheduled runs yet.</p>
    <div v-else class="scroll">
      <table>
        <thead>
          <tr><th>Started</th><th>Result</th><th class="num">Pairs</th><th>Kinds of mismatch</th><th>Commit</th><th>Run</th></tr>
        </thead>
        <tbody>
          <tr v-for="{ n, s } in rows" :key="n.id">
            <td class="date">{{ fmtDate(n.date) }}</td>
            <td class="result"><span :class="['icon', s.state]" aria-hidden="true">{{ icon[s.state] }}</span> {{ s.label }}</td>
            <td class="num">{{ n.report ? fmtInt(n.report.cases) : '' }}</td>
            <td>
              <ul v-if="n.report?.mismatches.length" class="kinds">
                <li v-for="m in n.report.mismatches" :key="m.kind">{{ m.kind }}</li>
              </ul>
            </td>
            <td><a :href="`${repo}/commit/${n.commit}`"><code>{{ short(n.commit) }}</code></a></td>
            <td><a :href="n.url">#{{ n.id }}</a></td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>

<style scoped>
.empty { color: var(--vp-c-text-2); }
.scroll { overflow-x: auto; }
.num { text-align: right; font-variant-numeric: tabular-nums; }
.date { white-space: nowrap; font-variant-numeric: tabular-nums; }
.result { white-space: nowrap; }
.icon.pass { color: var(--vp-c-success-1); }
.icon.fail { color: var(--vp-c-danger-1); }
.icon.none { color: var(--vp-c-text-3); }
.kinds { margin: 0; padding-left: 16px; font-size: 13px; }
.kinds li + li { margin-top: 2px; }
</style>
