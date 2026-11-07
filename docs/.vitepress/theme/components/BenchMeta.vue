<script setup lang="ts">
import { data } from '../../../data/benchmarks.data'

const repo = 'https://github.com/fluentfoundation/OASDelta'
const date = (iso: string) => new Date(iso).toUTCString().replace(/:\d\d GMT$/, ' UTC')
</script>

<template>
  <table v-if="data.length" class="bench-meta">
    <thead>
      <tr><th></th><th v-for="r in data" :key="r.meta.label">{{ r.meta.label }}</th></tr>
    </thead>
    <tbody>
      <tr><td>OS</td><td v-for="r in data" :key="r.meta.label">{{ r.meta.os }} ({{ r.meta.arch }})</td></tr>
      <tr><td>CPU</td><td v-for="r in data" :key="r.meta.label">{{ r.meta.cpu }}, {{ r.meta.cpu_count }} cores</td></tr>
      <tr><td>Go</td><td v-for="r in data" :key="r.meta.label"><code>{{ r.meta.go_version }}</code>, {{ r.meta.go_binary_mb }} MB binary</td></tr>
      <tr><td>C#</td><td v-for="r in data" :key="r.meta.label"><code>{{ r.meta.cs_version }}</code>, {{ r.meta.cs_binary_mb }} MB binary</td></tr>
      <tr><td>Runs</td><td v-for="r in data" :key="r.meta.label">{{ r.meta.warmup }} warm-up + {{ r.meta.runs }} timed, per case and binary</td></tr>
      <tr v-if="data.some((r) => r.meta.real_world_runs)">
        <td>Real-world</td>
        <td v-for="r in data" :key="r.meta.label">
          <template v-if="r.meta.real_world_runs">
            1 warm-up + {{ r.meta.real_world_runs }} timed, from
            <a :href="`https://github.com/fluentfoundation/OASDelta-TestData/tree/${r.meta.test_data_commit || 'main'}`">OASDelta-TestData<template v-if="r.meta.test_data_commit">{{ ' ' }}<code>{{ r.meta.test_data_commit.slice(0, 7) }}</code></template></a>
          </template>
          <template v-else>not measured</template>
        </td>
      </tr>
      <tr><td>Measured</td><td v-for="r in data" :key="r.meta.label">{{ date(r.meta.date) }}</td></tr>
      <tr>
        <td>Source</td>
        <td v-for="r in data" :key="r.meta.label">
          <a v-if="r.meta.commit" :href="`${repo}/commit/${r.meta.commit}`"><code>{{ r.meta.commit.slice(0, 7) }}</code></a>
          <template v-else>local run</template>
          <template v-if="r.meta.run_url"> · <a :href="r.meta.run_url">workflow run</a></template>
        </td>
      </tr>
    </tbody>
  </table>
</template>

<style scoped>
.bench-meta { font-size: 13px; }
.bench-meta td:first-child { color: var(--vp-c-text-2); white-space: nowrap; }
</style>
