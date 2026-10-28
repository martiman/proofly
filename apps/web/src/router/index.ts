import { createRouter, createWebHistory } from 'vue-router'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', redirect: '/control-room' },
    { path: '/control-room', name: 'ControlRoom', component: () => import('../views/ControlRoom/index.vue'), meta: { title: 'Daily Control Room' } },
    { path: '/exceptions', name: 'ExceptionsQueue', component: () => import('../views/ExceptionsQueue/index.vue'), meta: { title: 'Exceptions' } },
    { path: '/exceptions/:id', name: 'ExceptionDetail', component: () => import('../views/ExceptionDetail/index.vue'), meta: { title: 'Exception Detail' } },
    { path: '/transactions', name: 'TransactionExplorer', component: () => import('../views/TransactionExplorer/index.vue'), meta: { title: 'Transactions' } },
    { path: '/safeguarding', name: 'SafeguardingPosition', component: () => import('../views/SafeguardingPosition/index.vue'), meta: { title: 'Safeguarding' } },
    { path: '/sign-off', name: 'RunSignOff', component: () => import('../views/RunSignOff/index.vue'), meta: { title: 'Sign-off' } },
    { path: '/sources', name: 'Sources', component: () => import('../views/Sources/index.vue'), meta: { title: 'Sources' } },
    { path: '/settings', name: 'ConfigurationAccess', component: () => import('../views/ConfigurationAccess/index.vue'), meta: { title: 'Configuration and Access' } },
    { path: '/matching', name: 'MatchingWorkbench', component: () => import('../views/MatchingWorkbench/index.vue'), meta: { title: 'Matching Workbench' } },
    { path: '/accounts/:id', name: 'AccountLedger', component: () => import('../views/AccountLedger/index.vue'), meta: { title: 'Account and Ledger' } },
    { path: '/evidence', name: 'EvidenceAudit', component: () => import('../views/EvidenceAudit/index.vue'), meta: { title: 'Evidence and Audit Explorer' } },
  ],
})

// The selected run (`?run=`) follows the user across screens: links that omit it inherit it from the previous route.
router.beforeEach((to, from) => {
  if (!to.query.run && typeof from.query.run === 'string') return { ...to, query: { ...to.query, run: from.query.run }, replace: true }
  return true
})

export default router
