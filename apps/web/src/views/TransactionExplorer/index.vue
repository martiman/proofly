<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { PageHeader, StatePlaceholder, StatusPill, EvidenceLink } from '../../components'
import type { Status } from '../../components/StatusPill.vue'
import { getLineage, getTransaction, getTransactionLineage, listTransactions } from '../../api/services'
import type { Schema } from '../../api/services'
import { usePaged, useResource } from '../../api/composables'
import { formatMoney, isNotFound } from '../../api/client'
import { useRunContext } from '../../composables/useRunContext'

type Transaction = Schema<'Transaction'>
type Money = Schema<'Money'>
type Kind = Schema<'TransactionKind'>
type MatchStatus = Schema<'MatchStatus'>
type Aggregates = NonNullable<Schema<'TransactionPage'>['aggregates']>

const route = useRoute()
const router = useRouter()
const { runId, currencies } = useRunContext()

const q = (k: string) => (typeof route.query[k] === 'string' && route.query[k] ? (route.query[k] as string) : undefined)
const search = ref(q('q') ?? '')
const kind = computed(() => q('kind') as Kind | undefined)
const matchStatus = computed(() => q('match') as MatchStatus | undefined)
const currency = computed(() => q('currency'))
const accountId = computed(() => q('account'))
const hasException = computed(() => (q('exc') === 'true' ? true : q('exc') === 'false' ? false : undefined))
const qText = computed(() => q('q'))
const recordIdQ = computed(() => q('recordId'))
// A source record id (global search) is resolved to its transaction through the record lineage; none found => not-found state.
const recordLookup = useResource(
  () => (runId.value && recordIdQ.value && !q('id') ? `${runId.value}|${recordIdQ.value}` : null),
  (k: string, signal) => { const [r, id] = k.split('|'); return getLineage('RECORD', id!, { runId: r }, { signal }) },
)
const recordTxId = computed(() =>
  recordLookup.data.value?.nodes.find((n) => n.recordId === recordIdQ.value && n.kind !== 'DELIVERY' && n.kind !== 'CONTROL_RESULT')?.id)
const recordNotFound = computed(() => !!recordIdQ.value && !q('id') && (isNotFound(recordLookup.error.value) || (!!recordLookup.data.value && !recordTxId.value)))
const selectedId = computed(() => q('id') ?? recordTxId.value)

const kinds: Kind[] = ['BANK_TRANSFER', 'DIRECT_DEBIT', 'CARD', 'BANK_EVIDENCE', 'LEDGER_ENTRY']
const matchStatuses: MatchStatus[] = ['MATCHED', 'UNMATCHED', 'VALUE_MISMATCH', 'NOT_APPLICABLE', 'PENDING_APPROVAL']
const nice = (s: string) => s.replace(/_/g, ' ').toLowerCase().replace(/^\w/, (c) => c.toUpperCase())
const matchPill = (m: MatchStatus): Status => (m === 'MATCHED' ? 'pass' : m === 'UNMATCHED' ? 'fail' : m === 'VALUE_MISMATCH' ? 'warn' : m === 'PENDING_APPROVAL' ? 'info' : 'neutral')
const fm = (m: Money | null | undefined) => (m ? formatMoney(m.amount, m.currency) : '—')
const dt = (s: string | null | undefined) => (s ? s.replace('T', ' ').slice(0, 19) + ' UTC' : '—')

function setQuery(patch: Record<string, string | undefined>) {
  const next: Record<string, string> = {}
  for (const [k, v] of Object.entries({ ...route.query, ...patch })) if (typeof v === 'string' && v) next[k] = v
  router.replace({ query: next })
}
const select = (id: string) => setQuery({ id })
const doSearch = () => setQuery({ q: search.value.trim() || undefined })

// ---- list (cursor paging, server aggregates shown as given)
const aggregates = ref<Aggregates>([])
const list = usePaged<Transaction>(
  async (p, signal) => {
    const r = await listTransactions(runId.value!, {
      kind: kind.value, matchStatus: matchStatus.value, currency: currency.value as never, accountId: accountId.value,
      hasException: hasException.value, search: qText.value, limit: p.limit, cursor: p.cursor, includeTotal: p.includeTotal,
    }, { signal })
    if (!p.cursor) aggregates.value = r.aggregates ?? []
    return r
  },
  [runId, kind, matchStatus, currency, accountId, hasException, qText],
  { limit: 25, includeTotal: true, mode: 'append' },
)

// ---- selected transaction
const tx = useResource(
  () => (runId.value && selectedId.value ? `${runId.value}|${selectedId.value}` : null),
  (k: string, signal) => { const [r, id] = k.split('|'); return getTransaction(r!, id!, { signal }) },
)
const lineage = useResource(
  () => (runId.value && selectedId.value ? `${runId.value}|${selectedId.value}` : null),
  (k: string, signal) => { const [r, id] = k.split('|'); return getTransactionLineage(r!, id!, { signal }) },
)
const notFound = computed(() => isNotFound(tx.error.value))
const t = computed(() => tx.data.value)
const sourceNodes = computed(() => (lineage.data.value?.nodes ?? []).filter((n) => n.kind !== 'DELIVERY'))
const evidenceTo = (recordId: string) => ({ path: '/evidence', query: { ...(runId.value ? { run: runId.value } : {}), recordId } })

const details = computed<[string, string][]>(() => {
  const x = t.value
  if (!x) return []
  return [
    ['Type', nice(x.kind)], ['Source', x.source], ['Source row', x.sourceRowId], ['Reference', x.reference],
    ['Direction', x.direction ? nice(x.direction) : '—'], ['Timestamp', dt(x.timestamp)], ['Source status', x.sourceStatus],
    ['Delivery', x.deliveryId ?? '—'], ['Match group', x.matchGroupId ?? '—'], ['Accepted', x.accepted ? 'Yes' : 'No'],
  ]
})
const nodeCols = [
  { title: 'Record', key: 'id', width: 130 }, { title: 'Kind', dataIndex: 'kind', key: 'kind', width: 160 },
  { title: 'Source', dataIndex: 'source', key: 'source' }, { title: 'Source row', key: 'row' },
  { title: 'Amount', key: 'amount', align: 'right' as const },
]
</script>

<template>
  <PageHeader title="Transaction explorer" subtitle="Evidence-backed view of transactions across sources and the ledger" />

  <div class="card filters">
    <a-input-search v-model:value="search" placeholder="Search ID, reference or account" allow-clear style="width: 280px" @search="doSearch" />
    <a-select :value="kind" placeholder="Type" allow-clear style="width: 150px" @change="(v: string | undefined) => setQuery({ kind: v, id: undefined })">
      <a-select-option v-for="k in kinds" :key="k" :value="k">{{ nice(k) }}</a-select-option>
    </a-select>
    <a-select :value="matchStatus" placeholder="Match status" allow-clear style="width: 170px" @change="(v: string | undefined) => setQuery({ match: v })">
      <a-select-option v-for="m in matchStatuses" :key="m" :value="m">{{ nice(m) }}</a-select-option>
    </a-select>
    <a-select :value="currency" placeholder="Currency" allow-clear style="width: 110px" @change="(v: string | undefined) => setQuery({ currency: v })">
      <a-select-option v-for="c in currencies" :key="c" :value="c">{{ c }}</a-select-option>
    </a-select>
    <a-select :value="q('exc')" placeholder="Exception" allow-clear style="width: 150px" @change="(v: string | undefined) => setQuery({ exc: v })">
      <a-select-option value="true">With exception</a-select-option>
      <a-select-option value="false">No exception</a-select-option>
    </a-select>
    <a-tag v-if="accountId" closable @close="setQuery({ account: undefined })">Account {{ accountId }}</a-tag>
  </div>

  <div v-if="aggregates.length" class="card agg" data-test="aggregates">
    <div v-for="a in aggregates" :key="a.currency" class="aggc">
      <div class="lbl">{{ a.currency }} · {{ a.count }} transactions</div>
      <div class="sub">Debits {{ fm(a.debitTotal) }}</div>
      <div class="sub">Credits {{ fm(a.creditTotal) }}</div>
    </div>
    <div class="aggn">Totals over the full filtered set, calculated by the server.</div>
  </div>

  <StatePlaceholder v-if="!runId || list.placeholder.value === 'loading'" state="loading" />
  <StatePlaceholder v-else-if="list.placeholder.value === 'forbidden'" state="forbidden" />
  <StatePlaceholder v-else-if="list.placeholder.value === 'error'" state="error" message="Transactions could not be loaded.">
    <template #action><a-button @click="list.reload()">Retry</a-button></template>
  </StatePlaceholder>
  <StatePlaceholder v-else-if="list.placeholder.value === 'empty'" state="empty" message="No transactions match the current filters." />
  <div v-else class="row">
    <div class="card results">
      <div class="rh">Results <span>{{ list.items.value.length }}<template v-if="list.total.value != null"> of {{ list.total.value }}</template></span></div>
      <div v-for="r in list.items.value" :key="r.id" class="item" :class="{ on: selectedId === r.id }" @click="select(r.id)">
        <div class="l1"><b class="mono">{{ r.id }}</b><StatusPill :status="matchPill(r.matchStatus)" :label="nice(r.matchStatus)" /></div>
        <div class="l2">{{ r.reference }}</div>
        <div class="l3"><span>{{ nice(r.kind) }} · {{ r.timestamp.slice(0, 10) }}</span><span class="mono amt">{{ fm(r.amount) }}</span></div>
      </div>
      <div v-if="list.hasNext.value" class="more"><a-button size="small" :loading="list.loading.value" @click="list.loadMore()">Load more</a-button></div>
    </div>

    <div class="detail">
      <div v-if="!selectedId && !recordNotFound" class="card pick">Select a transaction to see its lifecycle, record details and evidence.</div>
      <StatePlaceholder v-else-if="recordNotFound" state="empty" :message="`No transaction was found for source record ${recordIdQ}.`" />
      <StatePlaceholder v-else-if="tx.placeholder.value === 'loading'" state="loading" />
      <StatePlaceholder v-else-if="notFound" state="empty" :message="`Transaction ${selectedId} was not found in this run.`" />
      <StatePlaceholder v-else-if="tx.placeholder.value === 'forbidden'" state="forbidden" />
      <StatePlaceholder v-else-if="tx.placeholder.value" state="error" :message="tx.message.value">
        <template #action><a-button @click="tx.reload()">Retry</a-button></template>
      </StatePlaceholder>
      <template v-else-if="t">
        <div class="card dh">
          <div>
            <div class="mono big">{{ t.id }}</div>
            <div class="sub">{{ t.reference }} · {{ t.source }}</div>
          </div>
          <div class="dr"><span class="mono">{{ fm(t.amount) }}</span><StatusPill :status="matchPill(t.matchStatus)" :label="nice(t.matchStatus)" /></div>
        </div>
        <div class="card blk">
          <h3>Lifecycle</h3>
          <a-steps v-if="t.lifecycle.length" size="small" :current="t.lifecycle.length - 1">
            <a-step v-for="(s, i) in t.lifecycle" :key="i" :title="nice(s)" />
          </a-steps>
          <a-empty v-else description="No lifecycle stages observed; expected stages are missing." />
        </div>
        <div class="card blk">
          <h3>Record details</h3>
          <dl class="kv">
            <div class="kvi"><dt>Account</dt><dd><router-link v-if="t.accountId && t.kind !== 'BANK_EVIDENCE'" :to="{ path: `/accounts/${t.accountId}`, query: { ...(runId ? { run: runId } : {}), currency: t.amount.currency } }">{{ t.accountId }}</router-link><template v-else>{{ t.accountId ?? '—' }}</template></dd></div>
            <div class="kvi"><dt>Amount</dt><dd>{{ fm(t.amount) }}</dd></div>
            <div v-for="[l, v] in details" :key="l" class="kvi"><dt>{{ l }}</dt><dd>{{ v }}</dd></div>
            <div class="kvi wide"><dt>Exceptions</dt><dd>
              <router-link v-for="e in t.exceptionIds" :key="e" :to="`/exceptions/${e}`" class="exl">{{ e }}</router-link>
              <span v-if="!t.exceptionIds.length">None</span>
            </dd></div>
          </dl>
        </div>
        <div class="card blk">
          <h3>Source records and provenance</h3>
          <StatePlaceholder v-if="lineage.placeholder.value === 'loading'" state="loading" />
          <StatePlaceholder v-else-if="lineage.placeholder.value === 'forbidden'" state="forbidden" />
          <StatePlaceholder v-else-if="lineage.placeholder.value === 'error'" state="error" :message="lineage.message.value"><template #action><a-button @click="lineage.reload()">Retry</a-button></template></StatePlaceholder>
          <template v-else-if="lineage.data.value">
            <a-table :columns="nodeCols" :data-source="sourceNodes" row-key="id" size="small" :pagination="false">
              <template #bodyCell="{ column, record }">
                <template v-if="column.key === 'id'"><EvidenceLink v-if="record.recordId" :label="record.id" :to="evidenceTo(record.recordId)" /><span v-else class="mono">{{ record.id }}</span></template>
                <span v-else-if="column.key === 'row'" class="mono">{{ record.sourceRowId ?? '—' }}</span>
                <span v-else-if="column.key === 'amount'" class="mono">{{ fm(record.amount) }}</span>
                <template v-else-if="column.key === 'kind'">{{ nice(record.kind) }}</template>
              </template>
              <template #emptyText>No linked source records.</template>
            </a-table>
            <div v-if="lineage.data.value.deliveries?.length" class="dels">
              <div v-for="d in lineage.data.value.deliveries" :key="d.deliveryId" class="del mono">
                {{ d.deliveryId }} · row {{ d.rowNumber ?? '—' }} · fingerprint {{ d.fingerprint }}
              </div>
            </div>
          </template>
          <div class="acts">
            <EvidenceLink v-if="t.recordId" :label="`Open source record ${t.recordId}`" :to="evidenceTo(t.recordId)" />
            <router-link :to="{ path: '/matching', query: { ...(runId ? { run: runId } : {}), transactionId: t.id } }"><a-button>Inspect match decision</a-button></router-link>
            <router-link v-for="e in t.exceptionIds" :key="e" :to="`/exceptions/${e}`"><a-button type="primary">Open {{ e }}</a-button></router-link>
          </div>
        </div>
      </template>
    </div>
  </div>
</template>

<style scoped>
.filters { display: flex; gap: 8px; flex-wrap: wrap; padding: 12px; margin-bottom: 12px; }
.agg { display: flex; gap: 24px; padding: 12px 16px; margin-bottom: 12px; flex-wrap: wrap; align-items: flex-start; }
.aggc { display: flex; flex-direction: column; gap: 2px; min-width: 200px; }
.aggn { margin-left: auto; font-size: 11px; color: var(--ink-3); align-self: center; }
.lbl { font: 600 11px var(--font-mono); color: var(--ink-3); }
.exl { margin-right: 8px; font-family: var(--font-mono); font-size: 12px; }
h3 { margin: 0 0 10px; font-size: 14px; font-weight: 600; }
.acts { display: flex; gap: 8px; flex-wrap: wrap; margin-top: 12px; align-items: center; }
.row { display: grid; grid-template-columns: 320px 1fr; gap: 16px; align-items: start; }
.results { padding: 0; overflow: hidden; }
.rh { padding: 10px 12px; font-size: 12px; font-weight: 600; text-transform: uppercase; color: var(--ink-3); border-bottom: 1px solid var(--border); }
.item { padding: 10px 12px; border-bottom: 1px solid var(--border); cursor: pointer; display: flex; flex-direction: column; gap: 4px; }
.item.on { background: var(--surface-sunk); box-shadow: inset 3px 0 0 var(--accent); }
.l1, .l3 { display: flex; justify-content: space-between; align-items: center; gap: 8px; }
.l2 { font-size: 12px; color: var(--ink-2); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.l3 { font-size: 12px; color: var(--ink-3); }
.l1 :deep(.pill), .dr :deep(.pill) { white-space: nowrap; }
.kv { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); margin: 0; border: 1px solid var(--border); border-radius: 6px; overflow: hidden; }
.kvi { display: grid; grid-template-columns: 120px 1fr; border-bottom: 1px solid var(--border); }
.kvi.wide { grid-column: 1 / -1; border-bottom: 0; }
.kv dt { background: var(--surface-sunk); color: var(--ink-3); font-size: 12px; padding: 8px 12px; }
.kv dd { margin: 0; padding: 8px 12px; font-size: 13px; overflow-wrap: anywhere; min-width: 0; }
.detail :deep(a) { white-space: nowrap; }
.mono { font-family: var(--font-mono); font-size: 12px; }
.amt { color: var(--ink); white-space: nowrap; }
.more { padding: 10px 12px; text-align: center; }
.detail { display: flex; flex-direction: column; gap: 16px; min-width: 0; }
.pick { padding: 32px; text-align: center; color: var(--ink-3); }
.dh { padding: 16px; display: flex; justify-content: space-between; align-items: center; }
.dh .big { font-size: 18px; font-weight: 600; }
.sub { color: var(--ink-2); font-size: 12px; margin-top: 2px; }
.dr { display: flex; gap: 12px; align-items: center; font-size: 18px; font-weight: 600; }
.blk { padding: 16px; overflow-x: auto; }
.dels { margin-top: 8px; color: var(--ink-3); display: flex; flex-direction: column; gap: 2px; }
</style>
