import { createRouter, createWebHistory } from 'vue-router'

export default createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', redirect: '/control-room' },
    // Lite design: same API layer, simpler screens (proofly.pen "Lite — *" frames)
    { path: '/lite', name: 'LiteToday', component: () => import('../views/lite/Today.vue'), meta: { title: 'Today', layout: 'lite' } },
    { path: '/lite/exceptions', name: 'LiteExceptions', component: () => import('../views/lite/Exceptions.vue'), meta: { title: 'Exceptions', layout: 'lite' } },
    { path: '/lite/exceptions/:id', name: 'LiteException', component: () => import('../views/lite/Exception.vue'), meta: { title: 'Exception', layout: 'lite' } },
    { path: '/lite/safeguarding', name: 'LiteSafeguarding', component: () => import('../views/lite/Safeguarding.vue'), meta: { title: 'Safeguarding', layout: 'lite' } },
    { path: '/lite/sources', name: 'LiteSources', component: () => import('../views/lite/Sources.vue'), meta: { title: 'Sources', layout: 'lite' } },
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
