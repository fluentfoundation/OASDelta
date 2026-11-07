import DefaultTheme from 'vitepress/theme'
import type { Theme } from 'vitepress'
import BenchHeadline from './components/BenchHeadline.vue'
import BenchChart from './components/BenchChart.vue'
import BenchTable from './components/BenchTable.vue'
import BenchMeta from './components/BenchMeta.vue'
import './style.css'

export default {
  extends: DefaultTheme,
  enhanceApp({ app }) {
    app.component('BenchHeadline', BenchHeadline)
    app.component('BenchChart', BenchChart)
    app.component('BenchTable', BenchTable)
    app.component('BenchMeta', BenchMeta)
  },
} satisfies Theme
