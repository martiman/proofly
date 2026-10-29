<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { PageHeader, StatePlaceholder, MoneyAmount, StatusPill, EvidenceLink } from '../../components'
import { getAccountLedger, listAccounts } from '../../api'
import type { AccountLedgerView, Account, ControlState } from '../../api'

const route = useRoute()
const router = useRouter()
const data = ref<AccountLedgerView | null>(null)
const accounts = ref<Account[]>([])
const loading = ref(true)
const error = ref<{ status?: number; message: string } | null>(null)
const dirFilter = ref<'ALL' | 'DEBIT' | 'CREDIT'>('ALL')
const onlyIssues = ref(false)

const accountId = computed(() => String(route.params.id ?? ''))

async function load() {
  loading.value = true
  error.value = null
  try {
    if (!accounts.value.length) accounts.value = await listAccounts()
    if (!accountId.value && accounts.value.length) { router.replace(`/accounts/${accounts.value[0].id}`); return }
    data.value = accountId.value ? await getAccountLedger(accountId.value) : null
  } catch (e: any) {
    error.value = { status: e?.status, message: e?.message ?? 'Failed to load account' }
    data.value = null
  } finally { loading.value = false }
}
watch(accountId, load, { immediate: true })

const stateMap: Record<ControlState, { label: string; status: 'pass' | 'warn' | 'fail' | 'neutral' | 'info' }> = {
  PASSED: { label: 'Agreed', status: 'pass' },
  REVIEW_REQUIRED: { label: 'Difference - review required', status: 'warn' },
  FAILED: { label: 'Material difference', status: 'fail' },
  BLOCKED: { label: 'Incomplete evidence', status: 'fail' },
  QUALIFIED: { label: 'Qualified', status: 'info' },
}

const entries = computed(() => (data.value?.entries ?? []).filter((e) =>
  (dirFilter.value === 'ALL' || e.direction === dirFilter.value) && (!onlyIssues.value || e.exceptionIds.length)))

const entryCols = [
  { title: 'Value date', dataIndex: 'valueDate', key: 'valueDate', width: 110 },
  { title: 'Reference', dataIndex: 'reference', key: 'reference' },
  { title: 'Type', dataIndex: 'entryType', key: 'entryType', width: 130 },
  { title: 'Direction', dataIndex: 'direction', key: 'direction', width: 90 },
  { title: 'Amount', key: 'amount', align: 'right' as const, width: 160 },
  { title: 'Support', key: 'support', width: 160 },
]
const txCols = [
  { title: 'Transaction', key: 'id' },
  { title: 'Reference', dataIndex: 'reference', key: 'reference' },
  { title: 'Match', dataIndex: 'matchStatus', key: 'matchStatus', width: 130 },
  { title: 'Amount', key: 'amount', align: 'right' as const, width: 160 },
]
const bridgeEmphasis = (l: string) => /closing/i.test(l)
</script>

<template>
  <PageHeader title="Account and Ledger" :subtitle="data ? `${data.account.id} · ${data.account.currency} · ${data.account.legalEntityId}` : undefined">
    <template #actions>
      <a-select v-if="accounts.length" :value="accountId" style="width: 220px" @change="(v: string) => router.push(`/accounts/${v}`)">
        <a-select-option v-for="a in accounts" :key="a.id" :value="a.id">{{ a.id }} ({{ a.currency }})</a-select-option>
      </a-select>
      <a-button disabled>Export calculation trace</a-button>
    </template>
  </PageHeader>

  <StatePlaceholder v-if="loading" state="loading" />
  <StatePlaceholder v-else-if="error?.status === 403" state="forbidden" />
  <StatePlaceholder v-else-if="error" state="error" :message="error.message">
    <template #action><a-button @click="load">Retry</a-button></template>
  </StatePlaceholder>
  <StatePlaceholder v-else-if="!data" state="empty" message="No account selected." />

  <template v-else>
    <div class="card strip">
      <div class="cell"><div class="lbl">INDEPENDENT BALANCE</div><div class="big"><MoneyAmount :amount="data.account.independentClosingBalance.amount" :currency="data.account.currency" :show-currency="false" /></div><div class="sub">{{ data.account.currency }} · calculated by Proofly</div></div>
      <div class="cell"><div class="lbl">RECORDED LEDGER BALANCE</div><div class="big"><MoneyAmount :amount="data.account.recordedClosingBalance.amount" :currency="data.account.currency" :show-currency="false" /></div><div class="sub">{{ data.account.currency }} · primary ledger</div></div>
      <div class="cell"><div class="lbl">DIFFERENCE</div><div class="big"><MoneyAmount :amount="data.account.difference.amount" :currency="data.account.currency" :show-currency="false" /></div><div class="sub">{{ data.account.currency }}</div></div>
      <div class="cell"><div class="lbl">STATE</div><StatusPill :status="stateMap[data.account.state].status" :label="stateMap[data.account.state].label" /><div class="sub">Client money account · {{ data.account.legalEntityId }}</div></div>
    </div>

    <a-row :gutter="16">
      <a-col :span="16">
        <a-card size="small" title="BALANCE BRIDGE" style="margin-bottom: 16px">
          <a-descriptions :column="1" size="small" :colon="false" class="bridge">
            <a-descriptions-item v-for="l in data.rollForward" :key="l.label" :label="l.label">
              <b v-if="bridgeEmphasis(l.label)"><MoneyAmount :amount="l.amount.amount" :currency="l.amount.currency" /></b>
              <MoneyAmount v-else :amount="l.amount.amount" :currency="l.amount.currency" />
            </a-descriptions-item>
            <a-descriptions-item label="Difference">
              <MoneyAmount :amount="data.account.difference.amount" :currency="data.account.difference.currency" />
            </a-descriptions-item>
          </a-descriptions>
        </a-card>

        <a-card size="small" title="MOVEMENTS">
          <template #extra>
            <a-radio-group v-model:value="dirFilter" size="small" style="margin-right: 12px">
              <a-radio-button value="ALL">All</a-radio-button>
              <a-radio-button value="DEBIT">Debit</a-radio-button>
              <a-radio-button value="CREDIT">Credit</a-radio-button>
            </a-radio-group>
            <a-checkbox v-model:checked="onlyIssues">With exceptions</a-checkbox>
          </template>
          <a-table :columns="entryCols" :data-source="entries" row-key="id" size="small" :pagination="{ pageSize: 10 }">
            <template #bodyCell="{ column, record }">
              <MoneyAmount v-if="column.key === 'amount'" :amount="record.amount.amount" :currency="record.amount.currency" />
              <template v-else-if="column.key === 'support'">
                <template v-if="record.exceptionIds.length">
                  <EvidenceLink v-for="x in record.exceptionIds" :key="x" :label="x" :to="`/exceptions/${x}`" style="margin-right: 6px" />
                </template>
                <StatusPill v-else status="pass" label="Supported" />
              </template>
            </template>
            <template #emptyText><StatePlaceholder state="empty" message="No movements match the filter." /></template>
          </a-table>
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
                  <div><StatusPill :status="item.severity === 'CRITICAL' || item.severity === 'HIGH' ? 'fail' : 'warn'" :label="item.severity" />
                    <MoneyAmount v-if="item.value" :amount="item.value.amount" :currency="item.value.currency" style="margin-left: 8px" /></div>
                </div>
              </a-list-item>
            </template>
          </a-list>
        </a-card>
        <a-card size="small" title="RELATED TRANSACTIONS">
          <a-table :columns="txCols" :data-source="data.relatedTransactions" row-key="id" size="small" :pagination="{ pageSize: 8 }">
            <template #bodyCell="{ column, record }">
              <EvidenceLink v-if="column.key === 'id'" :label="record.id" :to="`/transactions?id=${record.id}`" />
              <MoneyAmount v-else-if="column.key === 'amount'" :amount="record.amount.amount" :currency="record.amount.currency" />
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
