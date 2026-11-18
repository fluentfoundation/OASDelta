import DefaultTheme from 'vitepress/theme'
import type { Theme } from 'vitepress'
import BenchHeadline from './components/BenchHeadline.vue'
import BenchChart from './components/BenchChart.vue'
import BenchTable from './components/BenchTable.vue'
import BenchMeta from './components/BenchMeta.vue'
import RuleCoverage from './components/RuleCoverage.vue'
import OracleLatest from './components/OracleLatest.vue'
import OracleHistory from './components/OracleHistory.vue'
import './style.css'

export default {
  extends: DefaultTheme,
  enhanceApp({ app }) {
    app.component('BenchHeadline', BenchHeadline)
    app.component('BenchChart', BenchChart)
    app.component('BenchTable', BenchTable)
    app.component('BenchMeta', BenchMeta)
    app.component('RuleCoverage', RuleCoverage)
    app.component('OracleLatest', OracleLatest)
    app.component('OracleHistory', OracleHistory)
  },
} satisfies Theme
