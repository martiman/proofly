<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { PageHeader, StatePlaceholder, StatusPill, EvidenceLink } from '../../components'
import type { Status } from '../../components/StatusPill.vue'
import { getAccountLedger, listAccounts } from '../../api/services'
import type { Schema } from '../../api/services'
import { useResource } from '../../api/composables'
import { formatMoney, isNotFound, isValidation } from '../../api/client'
import { useRunContext } from '../../composables/useRunContext'

type Money = Schema<'Money'>
type LedgerEntry = Schema<'LedgerEntry'>
const route = useRoute()
const router = useRouter()
const { runId } = useRunContext()
const accountId = computed(() => String(route.params.id ?? ''))
const currency = computed(() => (typeof route.query.currency === 'string' && route.query.currency ? route.query.currency : undefined))
const dirFilter = ref<'ALL' | 'DEBIT' | 'CREDIT'>('ALL')
const onlyIssues = ref(false)
const fm = (m: Money | null | undefined) => (m ? formatMoney(m.amount, m.currency) : '—')

// accounts in this run with the same id (one row per currency) feed the currency switcher
const sameId = useResource(() => (runId.value && accountId.value ? `${runId.value}|${accountId.value}` : null),
  async (k: string, signal) => { const [r, id] = k.split('|'); return (await listAccounts(r!, { search: id, limit: 50 }, { signal })).items.filter((a) => a.id === id) })
const currencies = computed(() => sameId.data.value?.map((a) => a.currency) ?? [])

const ledger = useResource(() => (runId.value && accountId.value ? `${runId.value}|${accountId.value}|${currency.value ?? ''}` : null),
  (k: string, signal) => { const [r, id, c] = k.split('|'); return getAccountLedger(r!, id!, { currency: (c || undefined) as never, limit: 50 }, { signal }) })
const data = computed(() => ledger.data.value)

// further ledger pages are appended; the server's cursor is followed, nothing is derived locally
const extra = ref<LedgerEntry[]>([])
const cursor = ref<string | null>(null)
const more = ref(false)
const moreErr = ref('')
watch(data, (d) => { extra.value = []; cursor.value = d?.entriesPage?.nextCursor ?? null }, { immediate: true })
async function loadMore() {
  if (!cursor.value || !runId.value) return
  more.value = true; moreErr.value = ''
  try {
    const r = await getAccountLedger(runId.value, accountId.value, { currency: currency.value as never, limit: 50, cursor: cursor.value })
    extra.value = [...extra.value, ...r.entries]; cursor.value = r.entriesPage?.nextCursor ?? null
  } catch (e) { moreErr.value = (e as Error).message } finally { more.value = false }
}
const needsCurrency = computed(() => isValidation(ledger.error.value))
const notFound = computed(() => isNotFound(ledger.error.value))
function setCurrency(c: string) { router.replace({ query: { ...route.query, currency: c } }) }

const stateMap: Record<Schema<'ControlState'>, { label: string; status: Status }> = {
  PASSED: { label: 'Agreed', status: 'pass' },
  REVIEW_REQUIRED: { label: 'Difference - review required', status: 'warn' },
  FAILED: { label: 'Material difference', status: 'fail' },
  BLOCKED: { label: 'Incomplete evidence', status: 'fail' },
  QUALIFIED: { label: 'Qualified', status: 'info' },
}
const entries = computed(() => [...(data.value?.entries ?? []), ...extra.value].filter((e) =>
  (dirFilter.value === 'ALL' || e.direction === dirFilter.value) && (!onlyIssues.value || e.exceptionIds.length)))
const evTo = (recordId: string) => ({ path: '/evidence', query: { ...(runId.value ? { run: runId.value } : {}), recordId } })
const entryCols = [
  { title: 'Value date', dataIndex: 'valueDate', key: 'valueDate', width: 110 },
  { title: 'Reference', dataIndex: 'reference', key: 'reference' },
  { title: 'Type', dataIndex: 'entryType', key: 'entryType', width: 120 },
  { title: 'Direction', dataIndex: 'direction', key: 'direction', width: 90 },
  { title: 'Amount', key: 'amount', align: 'right' as const, width: 170 },
  { title: 'Support', key: 'support', width: 170 },
]
const txCols = [
  { title: 'Transaction', key: 'id' }, { title: 'Reference', dataIndex: 'reference', key: 'reference' },
  { title: 'Match', dataIndex: 'matchStatus', key: 'matchStatus', width: 120 }, { title: 'Amount', key: 'amount', align: 'right' as const, width: 150 },
]
const bridgeEmphasis = (l: string) => /closing/i.test(l)
</script>

<template>
  <PageHeader title="Account and Ledger" :subtitle="data ? `${data.account.id} · ${data.account.currency} · ${data.account.legalEntityId}` : accountId">
    <template #actions>
      <a-radio-group v-if="currencies.length > 1" :value="currency ?? data?.account.currency" button-style="solid" @change="(e: any) => setCurrency(e.target.value)">
        <a-radio-button v-for="c in currencies" :key="c" :value="c">{{ c }}</a-radio-button>
      </a-radio-group>
    </template>
  </PageHeader>

  <StatePlaceholder v-if="!runId || ledger.placeholder.value === 'loading'" state="loading" />
  <StatePlaceholder v-else-if="ledger.placeholder.value === 'forbidden'" state="forbidden" />
  <StatePlaceholder v-else-if="needsCurrency" state="empty" message="This account holds several currencies in this run. Choose a currency to view its ledger.">
    <template #action>
      <a-space v-if="currencies.length"><a-button v-for="c in currencies" :key="c" type="primary" @click="setCurrency(c)">{{ c }}</a-button></a-space>
      <span v-else>No currency options could be loaded.</span>
    </template>
  </StatePlaceholder>
  <StatePlaceholder v-else-if="notFound" state="empty" :message="`Account ${accountId} was not found in this run.`" />
  <StatePlaceholder v-else-if="ledger.placeholder.value" state="error" :message="ledger.message.value">
    <template #action><a-button @click="ledger.reload()">Retry</a-button></template>
  </StatePlaceholder>

  <template v-else-if="data">
    <div class="card strip">
      <div class="cell"><div class="lbl">INDEPENDENT BALANCE</div><div class="big">{{ fm(data.account.independentClosingBalance) }}</div><div class="sub">calculated by Proofly</div></div>
      <div class="cell"><div class="lbl">RECORDED LEDGER BALANCE</div><div class="big">{{ fm(data.account.recordedClosingBalance) }}</div><div class="sub">primary ledger</div></div>
      <div class="cell"><div class="lbl">DIFFERENCE</div><div class="big">{{ fm(data.account.difference) }}</div><div class="sub">independent minus recorded; positive = independent figure is higher</div></div>
      <div class="cell"><div class="lbl">STATE</div><StatusPill :status="stateMap[data.account.state].status" :label="stateMap[data.account.state].label" /><div class="sub">Opening {{ fm(data.account.openingBalance) }}</div></div>
    </div>

    <a-row :gutter="16">
      <a-col :span="16">
        <a-card size="small" title="BALANCE BRIDGE" style="margin-bottom: 16px">
          <a-descriptions :column="1" size="small" :colon="false" class="bridge">
            <a-descriptions-item v-for="l in data.rollForward" :key="l.label" :label="l.label">
              <b v-if="bridgeEmphasis(l.label)">{{ fm(l.amount) }}</b><template v-else>{{ fm(l.amount) }}</template>
            </a-descriptions-item>
          </a-descriptions>
        </a-card>

        <a-card size="small" title="MOVEMENTS">
          <template #extra>
            <a-radio-group v-model:value="dirFilter" size="small" style="margin-right: 12px">
              <a-radio-button value="ALL">All</a-radio-button><a-radio-button value="DEBIT">Debit</a-radio-button><a-radio-button value="CREDIT">Credit</a-radio-button>
            </a-radio-group>
            <a-checkbox v-model:checked="onlyIssues">With exceptions</a-checkbox>
          </template>
          <a-table :columns="entryCols" :data-source="entries" row-key="id" size="small" :pagination="false">
            <template #bodyCell="{ column, record }">
              <span v-if="column.key === 'amount'" class="tabular">{{ fm(record.amount) }}</span>
              <template v-else-if="column.key === 'reference'">
                <EvidenceLink v-if="record.recordId" :label="record.reference" :to="evTo(record.recordId)" /><template v-else>{{ record.reference }}</template>
              </template>
              <template v-else-if="column.key === 'support'">
                <template v-if="record.exceptionIds.length"><EvidenceLink v-for="x in record.exceptionIds" :key="x" :label="x" :to="`/exceptions/${x}`" style="margin-right: 6px" /></template>
                <StatusPill v-else status="pass" label="No exception" />
              </template>
            </template>
            <template #emptyText><StatePlaceholder state="empty" message="No movements match the filter." /></template>
          </a-table>
          <div v-if="cursor" class="more"><a-button size="small" :loading="more" @click="loadMore">Load more entries</a-button><span v-if="moreErr" class="err">{{ moreErr }}</span></div>
        </a-card>
      </a-col>

      <a-col :span="8">
        <a-card size="small" title="DIFFERENCE EXPLANATION" style="margin-bottom: 16px">
          <StatePlaceholder v-if="!data.exceptions.length" state="empty" message="No exceptions contribute to this account." />
          <a-list v-else size="small" :data-source="data.exceptions">
            <template #renderItem="{ item }">
              <a-list-item>
                <div>
                  <EvidenceLink :label="item.id" :to="`/exceptions/${item.id}`" /> {{ item.title }}
                  <div><StatusPill :status="item.severity === 'CRITICAL' || item.severity === 'HIGH' ? 'fail' : 'warn'" :label="item.severity" /> <span v-if="item.value" class="tabular">{{ fm(item.value) }}</span></div>
                </div>
              </a-list-item>
            </template>
          </a-list>
        </a-card>
        <a-card size="small" title="RELATED TRANSACTIONS">
          <a-table :columns="txCols" :data-source="data.relatedTransactions" row-key="id" size="small" :pagination="{ pageSize: 8 }">
            <template #bodyCell="{ column, record }">
              <EvidenceLink v-if="column.key === 'id'" :label="record.id" :to="{ path: '/transactions', query: { ...(runId ? { run: runId } : {}), id: record.id } }" />
              <span v-else-if="column.key === 'amount'" class="tabular">{{ fm(record.amount) }}</span>
              <template v-else-if="column.key === 'matchStatus'">{{ String(record.matchStatus).replace(/_/g, ' ').toLowerCase() }}</template>
            </template>
          </a-table>
        </a-card>
      </a-col>
    </a-row>
  </template>
</template>

<style scoped>
.strip { display: flex; margin-bottom: 16px; }
.cell { flex: 1; padding: 14px 20px; display: flex; flex-direction: column; gap: 4px; border-right: 1px solid var(--border); }
.cell:last-child { border-right: 0; }
.lbl { font: 600 10px var(--font-mono); color: var(--ink-3); letter-spacing: 0.4px; }
.big { font-size: 26px; font-weight: 500; line-height: 34px; }
.big :deep(.money) { font-size: 26px; }
.sub { font-size: 11px; color: var(--ink-3); }
:deep(.ant-card-head) { min-height: 40px; }
:deep(.ant-card-head-title) { font: 600 10px var(--font-mono); color: var(--ink-3); letter-spacing: 0.4px; }
:deep(.ant-table-thead > tr > th) { background: var(--surface-sunk); font: 600 10px var(--font-mono); color: var(--ink-3); text-transform: uppercase; }
:deep(.bridge .ant-descriptions-item-label) { width: 70%; color: var(--ink); }
:deep(.bridge .ant-descriptions-row:nth-last-child(1) > th), :deep(.bridge .ant-descriptions-row:nth-last-child(1) > td) { background: #FBE7E4; color: var(--fail); font-weight: 600; }
</style>
<style scoped>
.more { margin-top: 8px; display: flex; gap: 8px; align-items: center }
.err { color: var(--fail) }
</style>
