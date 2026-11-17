<script setup lang="ts">
import { computed, ref } from 'vue'
import { data } from '../../../data/coverage.data'

const repo = 'https://github.com/fluentfoundation/OASDelta/blob/main/tests/golden/'
const total = data.rules.length
const pct = (n: number) => `${Math.round((100 * n) / total)}%`
const fmt = (n: number) => n.toLocaleString('en-US')

const show = ref<'uncovered' | 'covered' | 'all'>('uncovered')
const area = ref('')
const query = ref('')
const areas = [...new Set(data.rules.map((r) => r.area))].sort()

const rows = computed(() => {
  const q = query.value.trim().toLowerCase()
  return data.rules.filter((r) =>
    (show.value === 'all' || (show.value === 'covered') === r.comparisons > 0) &&
    (!area.value || r.area === area.value) &&
    (!q || r.id.includes(q) || r.description.toLowerCase().includes(q)))
})
</script>

<template>
  <div class="cov">
    <div class="cov-stats">
      <div><b>{{ fmt(data.fired) }}</b> of {{ fmt(total) }} rules fire in the goldens <span>({{ pct(data.fired) }})</span></div>
      <div><b>{{ fmt(total - data.fired) }}</b> fire in none <span>({{ pct(total - data.fired) }})</span></div>
      <div><b>{{ fmt(data.comparisons) }}</b> comparisons: {{ fmt(data.fixturePairs) }} fixture pairs × {{ data.sets }} command sets</div>
    </div>
    <div class="cov-bar" role="img" :aria-label="`${data.fired} of ${total} rules covered`">
      <span :style="{ width: pct(data.fired) }"></span>
    </div>

    <div class="cov-filters">
      <label>Show
        <select v-model="show">
          <option value="uncovered">Rules no golden fires</option>
          <option value="covered">Rules the goldens fire</option>
          <option value="all">All rules</option>
        </select>
      </label>
      <label>Area
        <select v-model="area">
          <option value="">All areas</option>
          <option v-for="a in areas" :key="a" :value="a">{{ a }}</option>
        </select>
      </label>
      <label>Search <input v-model="query" type="search" placeholder="id or description" /></label>
      <span class="cov-count">{{ fmt(rows.length) }} rules</span>
    </div>

    <ul class="cov-list">
      <li v-for="r in rows" :key="r.id">
        <div class="cov-head">
          <code>{{ r.id }}</code>
          <span class="cov-goldens">
            <a v-if="r.example" :href="repo + r.example" :title="`${r.comparisons} golden comparisons, ${r.pairs} fixture pairs`">{{ fmt(r.comparisons) }} goldens</a>
            <span v-else class="none">no golden</span>
          </span>
        </div>
        <div class="cov-desc">{{ r.description }}</div>
        <div class="cov-meta"><span :class="'lvl-' + r.level">{{ r.level }}</span> · {{ r.area }} · {{ r.kind }}<template v-if="r.direction !== 'none'"> · {{ r.direction }}</template></div>
      </li>
    </ul>
  </div>
</template>

<style scoped>
.cov-stats { display: flex; flex-wrap: wrap; gap: 8px 28px; margin: 16px 0 10px; }
.cov-stats b { font-size: 1.25em; font-variant-numeric: tabular-nums; }
.cov-stats span { color: var(--vp-c-text-2); }
.cov-bar { height: 8px; border-radius: 4px; background: var(--vp-c-default-soft); overflow: hidden; }
.cov-bar span { display: block; height: 100%; background: var(--vp-c-brand-1); border-radius: 4px 0 0 4px; }
.cov-filters { display: flex; flex-wrap: wrap; align-items: center; gap: 10px 18px; margin: 20px 0 8px; font-size: 14px; }
.cov-filters select, .cov-filters input {
  margin-left: 6px; padding: 3px 8px; border: 1px solid var(--vp-c-divider); border-radius: 6px;
  background: var(--vp-c-bg); color: var(--vp-c-text-1); font-size: 14px;
}
.cov-filters input { width: 13em; max-width: 60vw; }
.cov-count { color: var(--vp-c-text-2); }
.cov-list { list-style: none; padding: 0; margin: 0; border-top: 1px solid var(--vp-c-divider); }
.cov-list li { margin: 0; padding: 10px 0; border-bottom: 1px solid var(--vp-c-divider); }
.cov-head { display: flex; justify-content: space-between; align-items: baseline; gap: 12px; }
.cov-head code { font-size: 13px; overflow-wrap: anywhere; }
.cov-goldens { flex: none; font-size: 13px; font-variant-numeric: tabular-nums; }
.cov-desc { margin-top: 2px; font-size: 14px; line-height: 1.5; }
.cov-meta { font-size: 12px; color: var(--vp-c-text-2); }
.cov .none { color: var(--vp-c-text-3); }
.cov .lvl-error { color: var(--vp-c-danger-1); }
.cov .lvl-warning { color: var(--vp-c-warning-1); }
</style>
