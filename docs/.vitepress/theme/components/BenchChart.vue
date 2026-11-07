<script setup lang="ts">
import { computed, ref } from 'vue'
import { data, type BenchCase } from '../../../data/benchmarks.data'
import { fmtMs, fmtMb, relTime, relMem, inputs } from '../stats'

type Metric = 'time' | 'memory'
const platform = ref(data[0]?.meta.label ?? '')
const metric = ref<Metric>('time')
const report = computed(() => data.find((r) => r.meta.label === platform.value) ?? data[0])
const panels = computed(() => (report.value ? inputs(report.value) : []))

const value = (c: BenchCase, side: 'go' | 'cs') => (metric.value === 'time' ? c[side].median_ms : c[side].peak_mb)
const fmt = (v: number) => (metric.value === 'time' ? fmtMs(v) : fmtMb(v))
const rel = (c: BenchCase) => (metric.value === 'time' ? relTime(c.speedup) : relMem(c.memory_ratio))
const better = (c: BenchCase) => (metric.value === 'time' ? c.speedup : c.memory_ratio) >= 1

/** A clean axis: 0 … max in 1/2/2.5/5 × 10^n steps, at most 5 intervals. */
function axis(cases: BenchCase[]) {
  const max = Math.max(...cases.flatMap((c) => [value(c, 'go'), value(c, 'cs')]))
  const raw = max / 4
  const mag = 10 ** Math.floor(Math.log10(raw))
  const step = [1, 2, 2.5, 5, 10].map((m) => m * mag).find((s) => s >= raw)!
  const top = Math.ceil(max / step) * step
  const ticks = Array.from({ length: Math.round(top / step) + 1 }, (_, i) => i * step)
  return { top, ticks }
}
const tickLabel = (v: number) =>
  metric.value === 'time' ? (v >= 1000 ? `${v / 1000} s` : `${v} ms`) : `${v} MB`

const tip = ref<{ panel: string; c: BenchCase; top: number } | null>(null)
function show(panel: string, c: BenchCase, e: Event) {
  const row = e.currentTarget as HTMLElement
  tip.value = { panel, c, top: row.offsetTop + row.offsetHeight + 4 }
}
const hide = () => (tip.value = null)
</script>

<template>
  <div class="bench-viz" v-if="report">
    <div class="controls" role="toolbar" aria-label="Chart options">
      <div class="seg" role="radiogroup" aria-label="Platform">
        <button v-for="r in data" :key="r.meta.label" role="radio" :aria-checked="platform === r.meta.label"
          :class="{ on: platform === r.meta.label }" @click="platform = r.meta.label">{{ r.meta.label }}</button>
      </div>
      <div class="seg" role="radiogroup" aria-label="Metric">
        <button role="radio" :aria-checked="metric === 'time'" :class="{ on: metric === 'time' }" @click="metric = 'time'">Wall time</button>
        <button role="radio" :aria-checked="metric === 'memory'" :class="{ on: metric === 'memory' }" @click="metric = 'memory'">Peak memory</button>
      </div>
      <div class="legend" aria-hidden="false">
        <span><i class="sw cs"></i>oasdelta (C#, NativeAOT)</span>
        <span><i class="sw go"></i>oasdiff (Go)</span>
      </div>
    </div>
    <p class="caption">
      {{ metric === 'time' ? 'Median wall time per invocation, process start-up included. Shorter is better.' : 'Peak resident memory (Linux) / peak working set (Windows). Shorter is better.' }}
      Hover or focus a row for details.
    </p>

    <div class="panels">
      <figure v-for="p in panels" :key="p.key" class="panel" @mouseleave="hide">
        <figcaption>{{ p.title }}</figcaption>
        <div class="plot">
          <template v-for="ax in [axis(p.cases)]" :key="'ax'">
            <div class="rows">
              <div class="grid" aria-hidden="true">
                <span v-for="t in ax.ticks" :key="t" class="gl" :style="{ left: (t / ax.top) * 100 + '%' }"></span>
              </div>
              <div v-for="c in p.cases" :key="c.command" class="row" tabindex="0"
                :aria-label="`${c.command}: oasdelta ${fmt(value(c, 'cs'))}, oasdiff ${fmt(value(c, 'go'))}, ${rel(c)}`"
                @mouseenter="show(p.key, c, $event)" @focus="show(p.key, c, $event)" @blur="hide">
                <code class="cmd">{{ c.command }}</code>
                <div class="bars">
                  <span class="bar cs" :style="{ width: (value(c, 'cs') / ax.top) * 100 + '%' }"></span>
                  <span class="bar go" :style="{ width: (value(c, 'go') / ax.top) * 100 + '%' }"></span>
                </div>
                <span class="rel" :class="{ strong: better(c) }">{{ rel(c) }}</span>
              </div>
            </div>
            <div class="ticks" aria-hidden="true">
              <span v-for="t in ax.ticks" :key="t" :style="{ left: (t / ax.top) * 100 + '%' }">{{ tickLabel(t) }}</span>
            </div>
          </template>
          <div v-if="tip && tip.panel === p.key" class="tip" :style="{ top: tip.top + 'px' }" role="status">
            <div class="tip-h"><code>{{ tip.c.command }}</code> · {{ rel(tip.c) }}</div>
            <div class="tip-r"><i class="sw cs"></i>oasdelta <b>{{ fmtMs(tip.c.cs.median_ms) }}</b>
              <span>± {{ fmtMs(tip.c.cs.stddev_ms) }} · {{ fmtMb(tip.c.cs.peak_mb) }}</span></div>
            <div class="tip-r"><i class="sw go"></i>oasdiff <b>{{ fmtMs(tip.c.go.median_ms) }}</b>
              <span>± {{ fmtMs(tip.c.go.stddev_ms) }} · {{ fmtMb(tip.c.go.peak_mb) }}</span></div>
            <div class="tip-f">{{ tip.c.identical ? 'Output identical' : 'Output differs' }}</div>
          </div>
        </div>
      </figure>
    </div>
  </div>
  <p v-else class="empty">No benchmark results yet.</p>
</template>

<style scoped>
.bench-viz {
  --series-cs: #2a78d6;
  --series-go: #eb6834;
  --grid: var(--vp-c-divider);
  margin: 16px 0 32px;
}
.dark .bench-viz {
  --series-cs: #3987e5;
  --series-go: #d95926;
}
.controls { display: flex; flex-wrap: wrap; gap: 12px 16px; align-items: center; }
.seg { display: inline-flex; background: var(--vp-c-bg-soft); border-radius: 8px; padding: 3px; }
.seg button {
  font-size: 13px; padding: 4px 12px; border-radius: 6px; color: var(--vp-c-text-2); cursor: pointer;
}
.seg button.on { background: var(--vp-c-bg); color: var(--vp-c-text-1); box-shadow: 0 1px 2px rgb(0 0 0 / 0.12); }
.legend { display: flex; gap: 16px; font-size: 13px; color: var(--vp-c-text-2); margin-left: auto; }
.legend span { display: inline-flex; align-items: center; gap: 6px; }
.sw { display: inline-block; width: 10px; height: 10px; border-radius: 2px; }
.sw.cs { background: var(--series-cs); }
.sw.go { background: var(--series-go); }
.caption { font-size: 13px; color: var(--vp-c-text-2); margin: 10px 0 4px; }

.panels { display: grid; gap: 24px; margin-top: 12px; }
.panel { margin: 0; }
figcaption { font-size: 14px; font-weight: 600; color: var(--vp-c-text-1); margin-bottom: 8px; }
.plot { position: relative; }
.rows { position: relative; }
/* the label column and the result column sit outside the plot area; the grid covers only the bars */
.row, .ticks, .grid { --label: 150px; --rel: 96px; }
.grid { position: absolute; top: 0; bottom: 0; left: var(--label); right: var(--rel); pointer-events: none; }
.gl { position: absolute; top: 0; bottom: 0; width: 1px; background: var(--grid); }
.row {
  position: relative; display: grid; grid-template-columns: var(--label) 1fr var(--rel); align-items: center;
  padding: 7px 0; border-radius: 6px; outline: none; cursor: default;
}
.row:hover, .row:focus-visible { background: color-mix(in srgb, var(--vp-c-bg-soft) 70%, transparent); }
.row:focus-visible { box-shadow: 0 0 0 2px var(--vp-c-brand-1); }
.cmd { font-size: 12px; background: none !important; padding: 0 8px 0 0 !important; color: var(--vp-c-text-2); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.bars { display: grid; gap: 2px; }
.bar { display: block; height: 10px; border-radius: 0 4px 4px 0; min-width: 2px; }
.bar.cs { background: var(--series-cs); }
.bar.go { background: var(--series-go); }
.rel { font-size: 12px; color: var(--vp-c-text-2); text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
.rel.strong { color: var(--vp-c-text-1); font-weight: 600; }
.ticks { position: relative; height: 18px; margin: 4px var(--rel) 0 var(--label); }
.ticks span { position: absolute; transform: translateX(-50%); font-size: 11px; color: var(--vp-c-text-3); white-space: nowrap; font-variant-numeric: tabular-nums; }
.ticks span:first-child { transform: none; }
.ticks span:last-child { transform: translateX(-100%); }

.tip {
  position: absolute; left: 150px; z-index: 10; min-width: 240px; max-width: calc(100% - 150px);
  background: var(--vp-c-bg-elv); border: 1px solid var(--vp-c-divider); border-radius: 8px;
  box-shadow: var(--vp-shadow-3); padding: 10px 12px; font-size: 13px; pointer-events: none;
}
.tip-h { color: var(--vp-c-text-1); font-weight: 600; margin-bottom: 6px; }
.tip-r { display: flex; align-items: center; gap: 6px; color: var(--vp-c-text-1); font-variant-numeric: tabular-nums; }
.tip-r span { color: var(--vp-c-text-2); }
.tip-f { color: var(--vp-c-text-2); margin-top: 6px; font-size: 12px; }
.empty { color: var(--vp-c-text-2); }

@media (max-width: 640px) {
  .row, .ticks, .grid { --label: 104px; --rel: 76px; }
  .legend { margin-left: 0; }
  .tip { left: 0; max-width: 100%; }
}
</style>
