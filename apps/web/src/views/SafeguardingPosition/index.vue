<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { message } from 'ant-design-vue'
import { PageHeader, StatePlaceholder, StatusPill, MoneyAmount, EvidenceLink } from '../../components'
import type { Status } from '../../components/StatusPill.vue'
import { getSafeguardingPosition, proposeAdjustment } from '../../api'
import type { SafeguardingPosition, SafeguardingState, AdjustmentStatus, Currency } from '../../api'

const data = ref<SafeguardingPosition | null>(null)
const view = ref<'loading' | 'ready' | 'error' | 'forbidden'>('loading')
const errMsg = ref('')

async function load() {
  view.value = 'loading'
  try {
    data.value = await getSafeguardingPosition()
    view.value = 'ready'
  } catch (e: any) {
    errMsg.value = e?.message ?? ''
    view.value = e?.status === 403 ? 'forbidden' : 'error'
  }
}
onMounted(load)

const stateMap: Record<SafeguardingState, [Status, string]> = {
  COVERED: ['pass', 'Covered'], SURPLUS: ['pass', 'Surplus'], SHORTFALL: ['fail', 'Shortfall'],
  QUALIFIED: ['warn', 'Coverage cannot be established'], NO_OBLIGATION: ['neutral', 'No obligation'],
}
const adjMap: Record<AdjustmentStatus, Status> = { PROPOSED: 'warn', APPROVED: 'pass', REJECTED: 'fail', EXPIRED: 'neutral' }
const readyMap = { COMPLETE: 'pass', QUALIFIED: 'warn', INCOMPLETE: 'fail' } as const

const oblCols = [
  { title: 'Client account', dataIndex: 'accountId', key: 'accountId' },
  { title: 'Movement type', dataIndex: 'movementType', key: 'movementType' },
  { title: 'Amount', key: 'amount', align: 'right' as const },
]
const balCols = [
  { title: 'External account', key: 'acc' },
  { title: 'Balance', key: 'bal', align: 'right' as const },
  { title: 'Timestamp', key: 'ts' },
  { title: 'Evidence', key: 'ev' },
  { title: 'Inclusion', key: 'inc' },
  { title: 'Reason', dataIndex: 'reason', key: 'reason' },
]
const adjCols = [
  { title: 'ID', dataIndex: 'id', key: 'id' },
  { title: 'Amount', key: 'amount', align: 'right' as const },
  { title: 'Status', key: 'status' },
  { title: 'Owner', dataIndex: 'owner', key: 'owner' },
  { title: 'Reason', dataIndex: 'reason', key: 'reason' },
  { title: 'Evidence', key: 'ev' },
  { title: 'History', key: 'hist' },
]

const modal = reactive({ open: false, currency: 'EUR' as Currency, amount: '', reason: '', evidence: '', busy: false })
const currencies = computed(() => data.value?.positions.map((p) => p.currency) ?? [])
function openModal(c?: Currency) {
  modal.currency = c ?? currencies.value[0] ?? 'EUR'
  modal.amount = ''; modal.reason = ''; modal.evidence = ''
  modal.open = true
}
async function submit() {
  if (!/^-?\d+(\.\d+)?$/.test(modal.amount) || !modal.reason.trim()) {
    message.warning('Enter a decimal amount and a reason.')
    return
  }
  modal.busy = true
  try {
    await proposeAdjustment({
      currency: modal.currency, amount: { amount: modal.amount, currency: modal.currency },
      reason: modal.reason, evidenceRefs: modal.evidence.split(',').map((s) => s.trim()).filter(Boolean),
    })
    modal.open = false
    message.success('Adjustment proposed; approval pending.')
    await load()
  } catch (e: any) {
    message.error(e?.message ?? 'Failed to propose adjustment')
  } finally {
    modal.busy = false
  }
}
const fmtTs = (s: string) => s.replace('T', ' ').slice(0, 16)
</script>

<template>
  <PageHeader title="Safeguarding Position" subtitle="Client-money obligation against eligible safeguarding balances">
    <template #actions>
      <a-button type="primary" :disabled="view !== 'ready'" @click="openModal()">Propose adjustment</a-button>
      <router-link to="/sign-off"><a-button :disabled="view !== 'ready'">Proceed to sign-off</a-button></router-link>
    </template>
  </PageHeader>

  <StatePlaceholder v-if="view === 'loading'" state="loading" />
  <StatePlaceholder v-else-if="view === 'forbidden'" state="forbidden" />
  <StatePlaceholder v-else-if="view === 'error'" state="error" :message="errMsg || undefined">
    <template #action><a-button @click="load">Retry</a-button></template>
  </StatePlaceholder>
  <StatePlaceholder v-else-if="!data || !data.positions.length" state="empty" message="No safeguarding position for this run." />

  <template v-else>
    <div class="card block hdr">
      <span><b>Entity</b> {{ data.legalEntityId }}</span>
      <span><b>Business date</b> {{ data.businessDate }}</span>
      <span><b>Run</b> {{ data.runId }}</span>
      <span><b>Evidence</b> <StatusPill :status="readyMap[data.evidenceReadiness]" :label="data.evidenceReadiness" /></span>
      <span><b>Approval</b> <StatusPill status="info" :label="data.approvalState.replace(/_/g, ' ')" /></span>
    </div>

    <div class="cards">
      <div v-for="p in data.positions" :key="p.currency" class="card pos" :class="{ bad: p.state === 'SHORTFALL' }">
        <div class="ph2"><b>{{ p.currency }}</b><StatusPill :status="stateMap[p.state as SafeguardingState][0]" :label="stateMap[p.state as SafeguardingState][1]" /></div>
        <div class="big"><MoneyAmount :amount="p.result.amount" :currency="p.result.currency" /></div>
        <div class="sub3">
          <span><i>Obligation</i><MoneyAmount :amount="p.obligation.amount" :currency="p.obligation.currency" /></span>
          <span><i>Eligible</i><MoneyAmount :amount="p.eligibleBalance.amount" :currency="p.eligibleBalance.currency" /></span>
          <span><i>Adjustments</i><MoneyAmount :amount="p.approvedAdjustments.amount" :currency="p.approvedAdjustments.currency" /></span>
        </div>
        <div v-if="p.qualifications.length || p.exceptionIds.length" class="q">
          <span v-for="q in p.qualifications" :key="q">{{ q }}</span>
          <router-link v-for="id in p.exceptionIds" :key="id" :to="`/exceptions/${id}`" class="exc">{{ id }}</router-link>
        </div>
      </div>
    </div>

    <div class="grid">
      <div class="card block">
        <h3>Obligation breakdown</h3>
        <a-table :columns="oblCols" :data-source="data.obligations" :row-key="(r: any) => r.accountId + r.movementType" :pagination="{ pageSize: 8 }" size="small">
          <template #bodyCell="{ column, record }">
            <MoneyAmount v-if="column.key === 'amount'" :amount="record.amount.amount" :currency="record.amount.currency" />
            <router-link v-else-if="column.key === 'accountId'" :to="`/accounts/${record.accountId}`">{{ record.accountId }}</router-link>
          </template>
        </a-table>
      </div>
      <div class="stack">
      <div class="card block">
        <h3>Eligible-balance breakdown</h3>
        <a-table :columns="balCols" :scroll="{ x: 'max-content' }" :data-source="data.eligibleBalances" row-key="sourceRowId" :pagination="false" size="small">
          <template #bodyCell="{ column, record }">
            <span v-if="column.key === 'acc'" class="mono">{{ record.externalAccountId }}</span>
            <MoneyAmount v-else-if="column.key === 'bal'" :amount="record.balance.amount" :currency="record.balance.currency" />
            <span v-else-if="column.key === 'ts'">{{ fmtTs(record.balanceTimestamp) }}</span>
            <EvidenceLink v-else-if="column.key === 'ev'" :label="record.evidenceReference ?? record.sourceRowId" :to="`/evidence?ref=${record.sourceRowId}`" />
            <StatusPill v-else-if="column.key === 'inc'" :status="record.included ? 'pass' : 'warn'" :label="record.included ? 'Included' : 'Excluded'" />
          </template>
        </a-table>
      </div>

    <div class="card block">
      <h3>Adjustments</h3>
      <a-table :columns="adjCols" :scroll="{ x: 'max-content' }" :data-source="data.adjustments" row-key="id" :pagination="false" size="small">
        <template #emptyText>No adjustments proposed.</template>
        <template #bodyCell="{ column, record }">
          <MoneyAmount v-if="column.key === 'amount'" :amount="record.amount.amount" :currency="record.amount.currency" />
          <StatusPill v-else-if="column.key === 'status'" :status="adjMap[record.status as AdjustmentStatus]" :label="record.status" />
          <span v-else-if="column.key === 'ev'"><EvidenceLink v-for="e in record.evidenceRefs" :key="e" :label="e" :to="`/evidence?ref=${e}`" /></span>
          <span v-else-if="column.key === 'hist'"><div v-for="h in record.history" :key="h.id">{{ fmtTs(h.at) }} {{ h.actor }}: {{ h.text }}</div></span>
        </template>
      </a-table>
    </div>

    <div class="card block">
      <h3>Change vs previous business date</h3>
      <a-empty v-if="!data.changeAnalysis.length" description="No previous position to compare." />
      <ul v-else class="drivers">
        <li v-for="d in data.changeAnalysis" :key="d.currency + d.label">
          {{ d.label }} ({{ d.currency }}): <MoneyAmount :amount="d.change.amount" :currency="d.change.currency" />
        </li>
      </ul>
    </div>
      </div>
    </div>
  </template>

  <a-modal v-model:open="modal.open" title="Propose adjustment" ok-text="Propose" :confirm-loading="modal.busy" @ok="submit">
    <a-form layout="vertical">
      <a-form-item label="Currency">
        <a-select v-model:value="modal.currency" :options="currencies.map((c) => ({ value: c, label: c }))" />
      </a-form-item>
      <a-form-item label="Amount (decimal, signed)"><a-input v-model:value="modal.amount" /></a-form-item>
      <a-form-item label="Reason"><a-textarea v-model:value="modal.reason" :rows="3" /></a-form-item>
      <a-form-item label="Evidence references (comma separated)"><a-input v-model:value="modal.evidence" /></a-form-item>
    </a-form>
  </a-modal>
</template>

<style scoped>
.block { padding: 16px; margin-bottom: 16px; }
h3 { margin: 0 0 12px; font-size: 14px; font-weight: 600; }
.hdr { display: flex; gap: 28px; flex-wrap: wrap; align-items: center; }
.hdr b { color: var(--ink-3); font-weight: 500; margin-right: 6px; }
.grid { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 16px; align-items: start; }
.stack { display: flex; flex-direction: column; min-width: 0; }
.cards { display: grid; grid-template-columns: repeat(3, 1fr); gap: 16px; margin-bottom: 16px; }
.pos { padding: 16px; }
.pos.bad { border-color: var(--fail, #B3261E); }
.ph2 { display: flex; justify-content: space-between; align-items: center; margin-bottom: 8px; }
.big { font-size: 24px; font-weight: 600; margin-bottom: 12px; }
.sub3 { display: flex; gap: 16px; font-size: 12px; }
.sub3 span { display: flex; flex-direction: column; }
.sub3 i { font-style: normal; color: var(--ink-3); }
.q { margin-top: 10px; font-size: 12px; color: var(--ink-2); display: flex; flex-direction: column; gap: 2px; }
.mono { font-family: var(--font-mono); font-size: 12px; }
.exc { margin-right: 8px; }
.drivers { margin: 0; padding-left: 18px; }
</style>
