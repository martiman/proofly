<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { message } from 'ant-design-vue'
import { PageHeader, StatePlaceholder, StatusPill, EvidenceLink } from '../../components'
import type { Status } from '../../components/StatusPill.vue'
import { useRunContext } from '../../composables/useRunContext'
import { useResource, usePaged } from '../../api/composables'
import { formatMoney } from '../../api/client'
import { getSafeguardingPosition, listSafeguardingLines, listAdjustments } from '../../api/services'
import type { Schema } from '../../api/services'
import { useAdjustmentCommands, isDecimal } from './adjustmentCommands'
import { describeCommandError, type CommandErrorView } from './commandError'

type Adjustment = Schema<'SafeguardingAdjustment'>
const LINES_LIMIT = 10

const { runId, me } = useRunContext()
const position = useResource(() => runId.value, (id, signal) => getSafeguardingPosition(id, { linesLimit: LINES_LIMIT }, { signal }), { isEmpty: (d) => !d.positions.length })
const data = computed(() => position.data.value)
const adjustments = usePaged((q, signal) => listAdjustments(runId.value!, q, { signal }), [runId], { limit: 10, mode: 'append' })
const canPropose = computed(() => !!me.value?.permissions.includes('safeguarding.adjustment.propose'))

const stateMap: Record<string, [Status, string]> = {
  COVERED: ['pass', 'Covered'], SURPLUS: ['pass', 'Surplus'], SHORTFALL: ['fail', 'Shortfall'],
  QUALIFIED: ['warn', 'Coverage cannot be established'], NO_OBLIGATION: ['neutral', 'No obligation'],
}
const adjMap: Record<string, Status> = { PROPOSED: 'warn', APPROVED: 'pass', REJECTED: 'fail', EXPIRED: 'neutral' }
const readyMap = { COMPLETE: 'pass', QUALIFIED: 'warn', INCOMPLETE: 'fail' } as const
const money = (m: { amount: string; currency: string }) => formatMoney(m.amount, m.currency)
const fmtTs = (s: string) => s.replace('T', ' ').slice(0, 16)
const sliceNote = (shown: number, total: number | undefined) => (total !== undefined && total > shown ? `showing ${shown} of ${total}` : '')
const oblShown = (c: string) => data.value?.obligations.filter((l) => l.currency === c).length ?? 0
const balShown = (c: string) => data.value?.eligibleBalances.filter((l) => l.balance.currency === c).length ?? 0

const oblCols = [
  { title: 'Client account', dataIndex: 'accountId', key: 'accountId' },
  { title: 'Movement type', key: 'movementType' },
  { title: 'Amount', key: 'amount', align: 'right' as const },
]
const balCols = [
  { title: 'External account', key: 'acc' }, { title: 'Balance', key: 'bal', align: 'right' as const },
  { title: 'Timestamp', key: 'ts' }, { title: 'Evidence', key: 'ev' }, { title: 'Inclusion', key: 'inc' }, { title: 'Reason', dataIndex: 'reason', key: 'reason' },
]
const adjCols = [
  { title: 'ID', dataIndex: 'id', key: 'id' }, { title: 'Amount', key: 'amount', align: 'right' as const }, { title: 'Status', key: 'status' },
  { title: 'Owner', key: 'owner' }, { title: 'Reason', dataIndex: 'reason', key: 'reason' }, { title: 'Expires', key: 'exp' }, { title: 'Evidence', key: 'ev' }, { title: 'History', key: 'hist' }, { title: '', key: 'act' },
]

// ---- paged drill-down of all lines of a currency ----
const drill = reactive({ open: false, currency: '', kind: 'obligation' as 'obligation' | 'balance', search: '' })
const drillQuery = ref('')
let t: ReturnType<typeof setTimeout> | undefined
watch(() => drill.search, (v) => { clearTimeout(t); t = setTimeout(() => (drillQuery.value = v.trim()), 300) })
const lines = usePaged(
  async (q, signal) => {
    if (!drill.open || !runId.value) return { items: [] as any[], page: { hasMore: false, nextCursor: null } }
    const p = await listSafeguardingLines(runId.value, drill.currency, { kind: drill.kind, search: drillQuery.value || undefined, ...q }, { signal })
    return { items: drill.kind === 'obligation' ? p.obligationLines : p.balanceLines, page: p.page }
  },
  [() => drill.open, () => drill.currency, () => drill.kind, drillQuery], { limit: 20, includeTotal: true },
)
function openDrill(currency: string, kind: 'obligation' | 'balance') { drill.search = ''; drillQuery.value = ''; drill.currency = currency; drill.kind = kind; drill.open = true }

// ---- commands ----
const cmd = useAdjustmentCommands(() => runId.value)
const prop = reactive({ open: false, currency: '', amount: '', reason: '', evidence: [] as string[], expiresOn: '', err: null as CommandErrorView | null, local: {} as Record<string, string> })
const currencies = computed(() => data.value?.positions.map((p) => p.currency) ?? [])
function openProp(c?: string) { Object.assign(prop, { open: true, currency: c ?? currencies.value[0] ?? '', amount: '', reason: '', evidence: [], expiresOn: '', err: null, local: {} }); cmd.propose.reset() }
async function refresh() { await Promise.all([position.reload(), adjustments.reload()]) }
async function submitProp() {
  const local: Record<string, string> = {}
  if (!isDecimal(prop.amount)) local.amount = 'Enter a signed decimal amount, e.g. -250.00.'
  if (!prop.reason.trim()) local.reason = 'A reason is required.'
  if (!prop.evidence.length) local.evidenceRefs = 'At least one evidence reference is required.'
  prop.local = local; prop.err = null
  if (Object.keys(local).length) return
  try {
    await cmd.propose.run({ currency: prop.currency, amount: prop.amount.trim(), reason: prop.reason.trim(), evidenceRefs: prop.evidence, expiresOn: prop.expiresOn || null })
    prop.open = false
    message.success('Adjustment proposed; approval pending.')
    await refresh()
  } catch (e) { prop.err = describeCommandError(e) }
}
const fe = (k: string) => prop.local[k] ?? prop.err?.fields[k]?.join(' ')

const dec = reactive({ open: false, adj: null as Adjustment | null, decision: 'APPROVE' as 'APPROVE' | 'REJECT', reason: '', err: null as CommandErrorView | null })
function openDec(a: Adjustment, d: 'APPROVE' | 'REJECT') { Object.assign(dec, { open: true, adj: a, decision: d, reason: '', err: null }); cmd.decide.reset() }
async function submitDec() {
  if (!dec.reason.trim()) { dec.err = { kind: 'validation', message: 'A reason is required.', fields: { reason: ['A reason is required.'] } }; return }
  try {
    await cmd.decide.run({ adjustment: dec.adj!, decision: dec.decision, reason: dec.reason.trim() })
    dec.open = false
    message.success(dec.decision === 'APPROVE' ? 'Adjustment approved. Reprocess the run to include it in the result.' : 'Adjustment rejected.')
    await refresh()
  } catch (e) { dec.err = describeCommandError(e) }
}
async function reloadDec() { dec.open = false; await refresh() }
</script>

<template>
  <PageHeader title="Safeguarding Position" subtitle="Client-money obligation against eligible safeguarding balances">
    <template #actions>
      <a-button v-if="canPropose" type="primary" :disabled="!data" @click="openProp()">Propose adjustment</a-button>
      <router-link to="/sign-off"><a-button :disabled="!data">Proceed to sign-off</a-button></router-link>
    </template>
  </PageHeader>

  <StatePlaceholder v-if="position.placeholder.value" :state="position.placeholder.value" :message="position.placeholder.value === 'empty' ? 'No safeguarding position for this run.' : position.message.value">
    <template #action><a-button v-if="position.placeholder.value === 'error'" @click="position.reload()">Retry</a-button></template>
  </StatePlaceholder>

  <template v-else-if="data">
    <a-alert v-if="data.stale" type="warning" show-icon style="margin-bottom: 16px" message="This position is stale: an adjustment was approved or expired after the run was processed. Reprocess the run to include it." />
    <div class="card block hdr">
      <span><b>Entity</b> {{ data.legalEntityId }}</span>
      <span><b>Business date</b> {{ data.businessDate }}</span>
      <span><b>Run</b> {{ data.runId }}</span>
      <span><b>Evidence</b> <StatusPill :status="readyMap[data.evidenceReadiness]" :label="data.evidenceReadiness" /></span>
      <span><b>Approval</b> <StatusPill status="info" :label="data.approvalState.replace(/_/g, ' ')" /></span>
    </div>

    <div class="cards">
      <div v-for="p in data.positions" :key="p.currency" class="card pos" :class="{ bad: p.state === 'SHORTFALL' }">
        <div class="ph2"><b>{{ p.currency }}</b><StatusPill :status="(stateMap[p.state] ?? ['neutral', p.state])[0]" :label="(stateMap[p.state] ?? ['neutral', p.state])[1]" /></div>
        <div class="big">
          <template v-if="p.result">{{ money(p.result) }}</template>
          <a-tooltip v-else title="Coverage cannot be established, so no difference is computed."><span>—</span></a-tooltip>
          <a-tag v-if="p.stale" color="orange" style="margin-left: 8px; vertical-align: middle">stale</a-tag>
        </div>
        <div v-if="!p.result" class="expl">Coverage cannot be established; the difference is not computed.</div>
        <div class="sub3">
          <span><i>Obligation</i>{{ money(p.obligation) }}</span>
          <span><i>Eligible</i>{{ money(p.eligibleBalance) }}</span>
          <span><i>Approved adjustments</i>{{ money(p.approvedAdjustments) }}</span>
        </div>
        <div v-if="p.previousResult" class="sub3 prev"><span><i>Previous result</i>{{ money(p.previousResult) }}</span></div>
        <div v-if="p.qualifications.length || p.exceptionIds.length" class="q">
          <span v-for="q in p.qualifications" :key="q">{{ q }}</span>
          <span><router-link v-for="id in p.exceptionIds" :key="id" :to="`/exceptions/${id}`" class="exc">{{ id }}</router-link></span>
        </div>
      </div>
    </div>

    <div class="grid">
      <div class="card block">
        <h3>Obligation breakdown</h3>
        <div v-for="p in data.positions" :key="p.currency" class="sect">
          <div class="sh"><b>{{ p.currency }}</b>
            <span class="dim">{{ sliceNote(oblShown(p.currency), p.obligationLinesTotal) }}</span>
            <a-button size="small" type="link" @click="openDrill(p.currency, 'obligation')">View all lines</a-button></div>
        </div>
        <a-table :columns="oblCols" :data-source="data.obligations" :row-key="(r: any) => r.accountId + r.currency + (r.movementType ?? '')" :pagination="false" size="small">
          <template #bodyCell="{ column, record }">
            <template v-if="column.key === 'amount'">{{ money(record.amount) }}</template>
            <router-link v-else-if="column.key === 'accountId'" :to="`/accounts/${record.accountId}`">{{ record.accountId }}</router-link>
            <span v-else-if="column.key === 'movementType'">{{ record.movementType ?? '—' }}</span>
          </template>
        </a-table>
      </div>
      <div class="stack">
        <div class="card block">
          <h3>Eligible-balance breakdown</h3>
          <div v-for="p in data.positions" :key="p.currency" class="sect">
            <div class="sh"><b>{{ p.currency }}</b>
              <span class="dim">{{ sliceNote(balShown(p.currency), p.balanceLinesTotal) }}</span>
              <a-button size="small" type="link" @click="openDrill(p.currency, 'balance')">View all lines</a-button></div>
          </div>
          <a-table :columns="balCols" :scroll="{ x: 'max-content' }" :data-source="data.eligibleBalances" row-key="sourceRowId" :pagination="false" size="small">
            <template #bodyCell="{ column, record }">
              <span v-if="column.key === 'acc'" class="mono">{{ record.externalAccountId }}</span>
              <template v-else-if="column.key === 'bal'">{{ money(record.balance) }}</template>
              <span v-else-if="column.key === 'ts'">{{ fmtTs(record.balanceTimestamp) }}</span>
              <template v-else-if="column.key === 'ev'"><EvidenceLink v-if="record.evidenceReference || record.recordId" :label="record.evidenceReference ?? record.sourceRowId" :to="`/evidence?ref=${encodeURIComponent(record.recordId ?? record.sourceRowId)}`" /><span v-else>—</span></template>
              <StatusPill v-else-if="column.key === 'inc'" :status="record.included ? 'pass' : 'warn'" :label="record.included ? 'Included' : 'Excluded'" />
            </template>
          </a-table>
        </div>

        <div class="card block">
          <h3>Adjustments</h3>
          <StatePlaceholder v-if="adjustments.state.value === 'error' || adjustments.state.value === 'forbidden'" :state="adjustments.state.value">
            <template #action><a-button @click="adjustments.reload()">Retry</a-button></template>
          </StatePlaceholder>
          <a-table v-else :columns="adjCols" :scroll="{ x: 'max-content' }" :data-source="adjustments.items.value" row-key="id" :loading="adjustments.loading.value" :pagination="false" size="small">
            <template #emptyText>No adjustments proposed.</template>
            <template #bodyCell="{ column, record }">
              <template v-if="column.key === 'amount'">{{ money(record.amount) }}</template>
              <StatusPill v-else-if="column.key === 'status'" :status="adjMap[record.status] ?? 'neutral'" :label="record.status" />
              <span v-else-if="column.key === 'owner'">{{ record.owner.name }}</span>
              <span v-else-if="column.key === 'exp'">{{ record.expiresOn ?? '—' }}</span>
              <span v-else-if="column.key === 'ev'"><EvidenceLink v-for="e in record.evidenceRefs" :key="e" :label="e" :to="`/evidence?ref=${encodeURIComponent(e)}`" /></span>
              <span v-else-if="column.key === 'hist'"><div v-for="h in record.history" :key="h.id">{{ fmtTs(h.at) }} {{ h.actor.name ?? h.actor.type }}: {{ h.text }}</div></span>
              <span v-else-if="column.key === 'act'">
                <template v-if="record.status === 'PROPOSED' && record.approval?.canDecide">
                  <a-button size="small" type="primary" @click="openDec(record, 'APPROVE')">Approve</a-button>
                  <a-button size="small" danger style="margin-left: 4px" @click="openDec(record, 'REJECT')">Reject</a-button>
                </template>
              </span>
            </template>
          </a-table>
          <div v-if="adjustments.hasNext.value" class="more"><a-button size="small" :loading="adjustments.loading.value" @click="adjustments.loadMore()">Load more</a-button></div>
        </div>

        <div class="card block">
          <h3>Change vs previous business date</h3>
          <a-empty v-if="!data.changeAnalysis.length" description="No previous position to compare." />
          <ul v-else class="drivers"><li v-for="d in data.changeAnalysis" :key="d.currency + d.label">{{ d.label }} ({{ d.currency }}): {{ money(d.change) }}</li></ul>
        </div>
      </div>
    </div>
  </template>

  <a-drawer v-model:open="drill.open" :title="`${drill.kind === 'obligation' ? 'Obligation' : 'Eligible balance'} lines · ${drill.currency}`" width="720">
    <a-input-search v-model:value="drill.search" :placeholder="drill.kind === 'obligation' ? 'Search account id' : 'Search external account id'" allow-clear style="margin-bottom: 12px" />
    <StatePlaceholder v-if="lines.placeholder.value" :state="lines.placeholder.value" :message="lines.placeholder.value === 'empty' ? 'No lines match.' : undefined">
      <template #action><a-button v-if="lines.placeholder.value === 'error'" @click="lines.reload()">Retry</a-button></template>
    </StatePlaceholder>
    <a-table v-else :columns="drill.kind === 'obligation' ? oblCols : balCols" :data-source="lines.items.value as any[]" :row-key="(r: any) => r.sourceRowId ?? r.accountId + (r.movementType ?? '') + r.amount?.amount" :pagination="false" size="small" :scroll="{ x: 'max-content' }">
      <template #bodyCell="{ column, record }">
        <template v-if="column.key === 'amount'">{{ money(record.amount) }}</template>
        <router-link v-else-if="column.key === 'accountId'" :to="`/accounts/${record.accountId}`">{{ record.accountId }}</router-link>
        <span v-else-if="column.key === 'movementType'">{{ record.movementType ?? '—' }}</span>
        <span v-else-if="column.key === 'acc'" class="mono">{{ record.externalAccountId }}</span>
        <template v-else-if="column.key === 'bal'">{{ money(record.balance) }}</template>
        <span v-else-if="column.key === 'ts'">{{ fmtTs(record.balanceTimestamp) }}</span>
        <template v-else-if="column.key === 'ev'"><EvidenceLink v-if="record.evidenceReference || record.recordId" :label="record.evidenceReference ?? record.sourceRowId" :to="`/evidence?ref=${encodeURIComponent(record.recordId ?? record.sourceRowId)}`" /><span v-else>—</span></template>
        <StatusPill v-else-if="column.key === 'inc'" :status="record.included ? 'pass' : 'warn'" :label="record.included ? 'Included' : 'Excluded'" />
      </template>
    </a-table>
    <div class="pg">
      <span class="dim"><template v-if="lines.total.value !== undefined">{{ lines.total.value }} lines</template></span>
      <a-button size="small" :disabled="!lines.hasPrev.value || lines.loading.value" @click="lines.prev()">Previous</a-button>
      <a-button size="small" :disabled="!lines.hasNext.value || lines.loading.value" @click="lines.next()">Next</a-button>
    </div>
  </a-drawer>

  <a-modal v-model:open="prop.open" title="Propose adjustment" ok-text="Propose" :confirm-loading="cmd.propose.pending.value" :mask-closable="false" @ok="submitProp">
    <a-alert v-if="prop.err" :type="prop.err.kind === 'stale' ? 'warning' : 'error'" show-icon :message="prop.err.message" style="margin-bottom: 12px" />
    <a-form layout="vertical">
      <a-form-item label="Currency"><a-select v-model:value="prop.currency" :options="currencies.map((c) => ({ value: c, label: c }))" /></a-form-item>
      <a-form-item label="Amount (signed decimal)" :validate-status="fe('amount') ? 'error' : undefined" :help="fe('amount')"><a-input v-model:value="prop.amount" placeholder="-250.00" /></a-form-item>
      <a-form-item label="Reason" :validate-status="fe('reason') ? 'error' : undefined" :help="fe('reason')"><a-textarea v-model:value="prop.reason" :rows="3" /></a-form-item>
      <a-form-item label="Evidence references (at least one)" :validate-status="fe('evidenceRefs') ? 'error' : undefined" :help="fe('evidenceRefs')"><a-select v-model:value="prop.evidence" mode="tags" :token-separators="[',', ' ']" placeholder="Type a reference and press Enter" /></a-form-item>
      <a-form-item label="Expires on (optional)" :validate-status="fe('expiresOn') ? 'error' : undefined" :help="fe('expiresOn')"><a-input v-model:value="prop.expiresOn" type="date" /></a-form-item>
    </a-form>
  </a-modal>

  <a-modal v-model:open="dec.open" :title="dec.decision === 'APPROVE' ? 'Approve adjustment' : 'Reject adjustment'" :ok-text="dec.decision === 'APPROVE' ? 'Approve' : 'Reject'" :ok-button-props="{ danger: dec.decision === 'REJECT' }" :confirm-loading="cmd.decide.pending.value" :mask-closable="false" @ok="submitDec">
    <a-alert v-if="dec.err" :type="dec.err.kind === 'stale' ? 'warning' : 'error'" show-icon :message="dec.err.message" style="margin-bottom: 12px">
      <template v-if="dec.err.kind === 'stale'" #action><a-button size="small" @click="reloadDec">Reload</a-button></template>
    </a-alert>
    <p v-if="dec.adj">{{ dec.adj.id }}: {{ money(dec.adj.amount) }}. {{ dec.adj.reason }}</p>
    <a-form layout="vertical"><a-form-item label="Reason (required)" :validate-status="dec.err?.fields.reason ? 'error' : undefined" :help="dec.err?.fields.reason?.join(' ')"><a-textarea v-model:value="dec.reason" :rows="3" /></a-form-item></a-form>
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
.big { font-size: 24px; font-weight: 600; margin-bottom: 8px; }
.expl { font-size: 12px; color: var(--warn); margin-bottom: 10px; }
.sub3 { display: flex; gap: 16px; font-size: 12px; flex-wrap: wrap; }
.sub3.prev { margin-top: 8px; }
.sub3 span { display: flex; flex-direction: column; }
.sub3 i { font-style: normal; color: var(--ink-3); }
.q { margin-top: 10px; font-size: 12px; color: var(--ink-2); display: flex; flex-direction: column; gap: 2px; }
.mono { font-family: var(--font-mono); font-size: 12px; }
.exc { margin-right: 8px; }
.drivers { margin: 0; padding-left: 18px; }
.sh { display: flex; align-items: center; gap: 8px; font-size: 12px; }
.dim { color: var(--ink-3); font-size: 12px; }
.more, .pg { display: flex; justify-content: flex-end; gap: 8px; margin-top: 8px; align-items: center; }
.pg .dim { margin-right: auto; }
</style>
