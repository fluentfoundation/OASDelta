<script setup lang="ts">
import { data } from '../../../data/benchmarks.data'
import { fmtMs, fmtMb, fmtX, outputLabel } from '../stats'
</script>

<template>
  <div v-for="r in data" :key="r.meta.label" class="bench-table">
    <h3 :id="`results-${r.meta.label.toLowerCase()}`">{{ r.meta.label }}</h3>
    <table>
      <thead>
        <tr>
          <th>Input</th>
          <th>Command</th>
          <th class="n">oasdiff (Go)</th>
          <th class="n">oasdelta (C#)</th>
          <th class="n">Speed-up</th>
          <th class="n">Go peak</th>
          <th class="n">C# peak</th>
          <th>Output</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="c in r.results" :key="c.input + c.command">
          <td>{{ c.input }}</td>
          <td><code>{{ c.command }}</code></td>
          <td class="n">{{ fmtMs(c.go.median_ms) }} <small>± {{ fmtMs(c.go.stddev_ms) }}</small></td>
          <td class="n">{{ fmtMs(c.cs.median_ms) }} <small>± {{ fmtMs(c.cs.stddev_ms) }}</small></td>
          <td class="n"><b>{{ fmtX(c.speedup) }}</b></td>
          <td class="n">{{ fmtMb(c.go.peak_mb) }}</td>
          <td class="n">{{ fmtMb(c.cs.peak_mb) }}</td>
          <td>
            {{ outputLabel(c) }}
            <small v-if="c.output === 'reordered' && c.order_runs">(distinct in {{ c.order_runs }} runs: oasdiff {{ c.go_orders }}, oasdelta {{ c.cs_orders }})</small>
          </td>
        </tr>
      </tbody>
    </table>
  </div>
</template>

<style scoped>
.bench-table table { font-size: 13px; display: table; width: 100%; }
.bench-table :is(th, td).n { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
.bench-table small { color: var(--vp-c-text-3); }
.bench-table th { white-space: nowrap; }
@media (max-width: 640px) { .bench-table table { display: block; } }
</style>
