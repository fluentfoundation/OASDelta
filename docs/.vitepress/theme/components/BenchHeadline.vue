<script setup lang="ts">
import { computed } from 'vue'
import { data } from '../../../data/benchmarks.data'
import { summarize, fmtX } from '../stats'

const reports = computed(() => data.map((r) => ({ meta: r.meta, s: summarize(r) })))
</script>

<template>
  <div class="bench-headline">
    <p v-if="!reports.length" class="empty">
      Benchmark results appear here after the first run of the <code>Benchmarks &amp; site</code> workflow.
    </p>
    <section v-for="r in reports" :key="r.meta.label" class="platform">
      <h3>{{ r.meta.label }} <span class="sub">{{ r.meta.cpu_count }} cores, {{ r.meta.run_url ? 'GitHub-hosted runner' : 'local run' }}</span></h3>
      <div class="tiles">
        <div v-if="r.s.realCases" class="tile">
          <div class="label">Speed-up, real-world APIs</div>
          <div class="value">{{ fmtX(r.s.speedupReal) }}</div>
          <div class="note">geometric mean, GitHub, Stripe and Kubernetes releases</div>
        </div>
        <div v-else class="tile">
          <div class="label">Speed-up, large specs</div>
          <div class="value">{{ fmtX(r.s.speedupLarge) }}</div>
          <div class="note">geometric mean, ~1–1.5 MB YAML and JSON</div>
        </div>
        <div class="tile">
          <div class="label">Speed-up, all cases</div>
          <div class="value">{{ fmtX(r.s.speedup) }}</div>
          <div class="note">range {{ fmtX(r.s.minSpeedup) }} – {{ fmtX(r.s.maxSpeedup) }}</div>
        </div>
        <div class="tile">
          <div class="label">Cases where oasdelta is faster</div>
          <div class="value">{{ r.s.faster }} / {{ r.s.cases }}</div>
          <div class="note">median wall time, start-up included</div>
        </div>
        <div class="tile">
          <div class="label">Identical output</div>
          <div class="value">{{ r.s.identical }} / {{ r.s.cases }}</div>
          <div class="note">stdout and exit code, byte for byte</div>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.bench-headline { margin: 24px 0; }
.platform + .platform { margin-top: 24px; }
h3 { margin: 0 0 12px; font-size: 16px; border: 0; padding: 0; }
.sub { font-weight: 400; color: var(--vp-c-text-2); font-size: 14px; margin-left: 6px; }
.tiles { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; }
@media (min-width: 960px) { :global(.VPHome) .tiles { grid-template-columns: repeat(4, minmax(0, 1fr)); } }
.tile { background: var(--vp-c-bg-soft); border-radius: 12px; padding: 16px; }
.label { font-size: 13px; color: var(--vp-c-text-2); line-height: 1.3; }
.tile { min-width: 0; }
.value { font-size: 30px; font-weight: 600; color: var(--vp-c-text-1); line-height: 1.2; margin: 6px 0 4px; font-variant-numeric: tabular-nums; }
.note { font-size: 12px; color: var(--vp-c-text-3); line-height: 1.35; }
.empty { color: var(--vp-c-text-2); }
</style>
