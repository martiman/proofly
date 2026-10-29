<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { PageHeader, StatePlaceholder, MoneyAmount, StatusPill, EvidenceLink } from '../../components'
import { listTransactions, getTransaction } from '../../api'
import type { Transaction, TransactionKind, MatchStatus } from '../../api'

const route = useRoute()
const router = useRouter()

const search = ref('')
const kind = ref<TransactionKind | undefined>()
const matchStatus = ref<MatchStatus | undefined>()
const hasException = ref<boolean | undefined>()
const page = ref(1)
const pageSize = 25
const rows = ref<Transaction[]>([])
const total = ref(0)
const state = ref<'loading' | 'ready' | 'error' | 'forbidden'>('loading')
const errMsg = ref('')

const sel = ref<Transaction | null>(null)
const selState = ref<'idle' | 'loading' | 'error'>('idle')
const selErr = ref('')

const kinds: TransactionKind[] = ['BANK_TRANSFER', 'DIRECT_DEBIT', 'CARD', 'BANK_EVIDENCE', 'LEDGER_ENTRY']
const matchStatuses: MatchStatus[] = ['MATCHED', 'UNMATCHED', 'VALUE_MISMATCH', 'NOT_APPLICABLE']
const matchPill = (m: MatchStatus) => (m === 'MATCHED' ? 'pass' : m === 'UNMATCHED' ? 'fail' : m === 'VALUE_MISMATCH' ? 'warn' : 'neutral')
const nice = (s: string) => s.replace(/_/g, ' ').toLowerCase().replace(/^\w/, (c) => c.toUpperCase())
const fail = (e: unknown) => {
  const x = e as { status?: number; message?: string }
  state.value = x.status === 403 ? 'forbidden' : 'error'
  errMsg.value = x.message ?? ''
}

async function load() {
  state.value = 'loading'
  try {
    const r = await listTransactions({ page: page.value, pageSize, search: search.value || undefined, kind: kind.value, matchStatus: matchStatus.value, hasException: hasException.value })
    rows.value = r.items
    total.value = r.total
    state.value = 'ready'
    if (typeof route.query.id !== 'string' && rows.value.length) select(rows.value[0].id)
  } catch (e) { fail(e) }
}
async function open(id: string) {
  selState.value = 'loading'
  try { sel.value = await getTransaction(id); selState.value = 'idle' } catch (e) {
    selState.value = 'error'; selErr.value = (e as Error).message
  }
}
function select(id: string) { router.replace({ query: { ...route.query, id } }) }
function refilter() { page.value = 1; load() }

watch(() => route.query.id, (id) => { if (typeof id === 'string') open(id); else { sel.value = null; selState.value = 'idle' } })
onMounted(() => { load(); if (typeof route.query.id === 'string') open(route.query.id) })

const comparison = computed(() => {
  const t = sel.value
  if (!t) return []
  return [
    ['Normalised ID', t.id], ['Source', t.source], ['Source row', t.sourceRowId], ['Reference', t.reference],
    ['Account', t.accountId ?? 'n/a'], ['Direction', t.direction ?? 'n/a'], ['Timestamp', t.timestamp], ['Source status', t.sourceStatus],
  ]
})
</script>

<template>
  <PageHeader title="Transactions" subtitle="Evidence-backed view of transactions across sources and ledger" />

  <div class="card filters">
    <a-input-search v-model:value="search" placeholder="Search ID, reference or account" allow-clear style="width: 300px" @search="refilter" />
    <a-select v-model:value="kind" placeholder="Type" allow-clear style="width: 160px" @change="refilter">
      <a-select-option v-for="k in kinds" :key="k" :value="k">{{ nice(k) }}</a-select-option>
    </a-select>
    <a-select v-model:value="matchStatus" placeholder="Match status" allow-clear style="width: 160px" @change="refilter">
      <a-select-option v-for="m in matchStatuses" :key="m" :value="m">{{ nice(m) }}</a-select-option>
    </a-select>
    <a-select v-model:value="hasException" placeholder="Exception" allow-clear style="width: 160px" @change="refilter">
      <a-select-option :value="true">With exception</a-select-option>
      <a-select-option :value="false">No exception</a-select-option>
    </a-select>
  </div>

  <StatePlaceholder v-if="state === 'loading'" state="loading" />
  <StatePlaceholder v-else-if="state === 'forbidden'" state="forbidden" />
  <StatePlaceholder v-else-if="state === 'error'" state="error" :message="errMsg">
    <template #action><a-button @click="load">Retry</a-button></template>
  </StatePlaceholder>
  <StatePlaceholder v-else-if="!rows.length" state="empty" message="No transactions match the current filters." />
  <div v-else class="row">
    <div class="card results">
      <div class="rh">Results <span>{{ total }}</span></div>
      <div v-for="r in rows" :key="r.id" class="item" :class="{ on: sel?.id === r.id || route.query.id === r.id }" @click="select(r.id)">
        <div class="l1"><b class="mono">{{ r.id }}</b><StatusPill :status="matchPill(r.matchStatus)" :label="nice(r.matchStatus)" /></div>
        <div class="l2">{{ r.reference }}</div>
        <div class="l3"><span>{{ nice(r.kind) }} · {{ r.timestamp.slice(0, 10) }}</span><MoneyAmount :amount="r.amount.amount" :currency="r.amount.currency" /></div>
      </div>
      <a-pagination v-if="total > pageSize" class="pg" size="small" :current="page" :page-size="pageSize" :total="total" :show-size-changer="false" @change="(p: number) => { page = p; load() }" />
    </div>

    <div class="detail">
      <div v-if="selState === 'idle' && !sel" class="card ph">Select a transaction to see its lifecycle, record details and evidence.</div>
      <a-spin v-else-if="selState === 'loading'" />
      <a-result v-else-if="selState === 'error'" status="error" :sub-title="selErr" />
      <template v-else-if="sel">
        <div class="card dh">
          <div>
            <div class="mono big">{{ sel.id }}</div>
            <div class="sub">{{ sel.reference }} · {{ sel.source }}</div>
          </div>
          <div class="dr"><MoneyAmount :amount="sel.amount.amount" :currency="sel.amount.currency" /><StatusPill :status="matchPill(sel.matchStatus)" :label="nice(sel.matchStatus)" /></div>
        </div>
        <div class="card blk">
          <h3>Lifecycle</h3>
          <a-steps v-if="sel.lifecycle.length" direction="horizontal" size="small" :current="sel.lifecycle.length - 1">
            <a-step v-for="(s, i) in sel.lifecycle" :key="i" :title="nice(s)" />
          </a-steps>
          <a-empty v-else description="No lifecycle stages observed; expected stages are missing." />
        </div>
        <div class="card blk">
          <h3>Record details</h3>
          <a-descriptions size="small" :column="2" bordered>
            <a-descriptions-item label="Type">{{ nice(sel.kind) }}</a-descriptions-item>
            <a-descriptions-item label="Accepted">{{ sel.accepted ? 'Yes' : 'No' }}</a-descriptions-item>
            <a-descriptions-item v-for="[l, v] in comparison" :key="l" :label="l">{{ v }}</a-descriptions-item>
            <a-descriptions-item label="Exceptions" :span="2">
              <router-link v-for="e in sel.exceptionIds" :key="e" :to="`/exceptions/${e}`" class="exl">{{ e }}</router-link>
              <span v-if="!sel.exceptionIds.length">None</span>
            </a-descriptions-item>
          </a-descriptions>
        </div>
        <div class="card blk">
          <h3>Evidence and provenance</h3>
          <EvidenceLink :label="`${sel.source} / ${sel.sourceRowId}`" :to="{ path: '/evidence', query: { transactionId: sel.id } }" />
          <div class="acts">
            <router-link :to="{ path: '/matching', query: { transactionId: sel.id } }"><a-button>Inspect match decision</a-button></router-link>
            <router-link v-if="sel.accountId" :to="`/accounts/${sel.accountId}`"><a-button>Open account</a-button></router-link>
            <router-link v-for="e in sel.exceptionIds" :key="e" :to="`/exceptions/${e}`"><a-button type="primary">Open {{ e }}</a-button></router-link>
          </div>
        </div>
      </template>
    </div>
  </div>
</template>

<style scoped>
.filters { display: flex; gap: 8px; flex-wrap: wrap; padding: 12px; margin-bottom: 12px; }
.exl { margin-right: 8px; font-family: var(--font-mono); font-size: 12px; }
h3 { margin: 0 0 10px; font-size: 14px; font-weight: 600; }
.acts { display: flex; gap: 8px; flex-wrap: wrap; margin-top: 12px; }
.row { display: grid; grid-template-columns: 300px 1fr; gap: 16px; align-items: start; }
.results { padding: 0; overflow: hidden; }
.rh { padding: 10px 12px; font-size: 12px; font-weight: 600; text-transform: uppercase; color: var(--ink-3); border-bottom: 1px solid var(--line, #E5E3DC); }
.item { padding: 10px 12px; border-bottom: 1px solid var(--line, #E5E3DC); cursor: pointer; display: flex; flex-direction: column; gap: 4px; }
.item.on { background: #EEEDE8; box-shadow: inset 3px 0 0 var(--accent, #2F5BEA); }
.l1, .l3 { display: flex; justify-content: space-between; align-items: center; gap: 8px; }
.l2 { font-size: 12px; color: var(--ink-2); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.l3 { font-size: 12px; color: var(--ink-3); }
.mono { font-family: var(--font-mono); font-size: 12px; }
.pg { padding: 10px 12px; }
.detail { display: flex; flex-direction: column; gap: 16px; min-width: 0; }
.ph { padding: 32px; text-align: center; color: var(--ink-3); }
.dh { padding: 16px; display: flex; justify-content: space-between; align-items: center; }
.dh .big { font-size: 18px; font-weight: 600; }
.sub { color: var(--ink-2); font-size: 12px; margin-top: 2px; }
.dr { display: flex; gap: 12px; align-items: center; font-size: 20px; font-weight: 600; }
.blk { padding: 16px; }
</style>
