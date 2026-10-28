<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { message } from 'ant-design-vue'
import { StatePlaceholder, StatusPill, EvidenceLink, PageHeader } from '../../components'
import type { Status } from '../../components/StatusPill.vue'
import { useRunContext } from '../../composables/useRunContext'
import { useResource, usePaged, useCommand } from '../../api/composables'
import { formatMoney, isApiError } from '../../api/client'
import { getMatchingWorkbench, listMatchGroups, proposeManualMatch, decideManualMatch } from '../../api/services'
import type { Schema } from '../../api/services'
import { buildProposeBody, describeCommandError, type CommandProblem } from './logic'

type Group = Schema<'MatchGroup'>

const route = useRoute()
const router = useRouter()
const { runId, me } = useRunContext()

const focusKey = computed(() => (runId.value ? { runId: runId.value, tx: route.query.transactionId as string | undefined, ex: route.query.exceptionId as string | undefined } : null))
const wb = useResource(() => (focusKey.value ? JSON.stringify(focusKey.value) : null), (k, signal) => {
  const f = JSON.parse(k) as NonNullable<typeof focusKey.value>
  return getMatchingWorkbench(f.runId, { transactionId: f.tx, exceptionId: f.ex }, { signal })
})
const data = computed(() => wb.data.value)
const selectedGroupId = ref<string | null>(null)
watch(data, () => { selectedGroupId.value = null; picked.value = []; problem.value = null })
const group = computed<Group | null>(() => data.value?.groups.find((g) => g.id === selectedGroupId.value) ?? data.value?.groups[0] ?? null)

const statusFilter = ref<Schema<'MatchStatus'> | undefined>()
const groups = usePaged((q, signal) => listMatchGroups(runId.value!, { ...q, status: statusFilter.value }, { signal }), [runId, statusFilter], { limit: 10 })

const statusMap: Record<string, { label: string; s: Status }> = {
  MATCHED: { label: 'Matched automatically', s: 'pass' }, UNMATCHED: { label: 'Unmatched', s: 'fail' },
  VALUE_MISMATCH: { label: 'Value mismatch', s: 'warn' }, NOT_APPLICABLE: { label: 'Not applicable', s: 'neutral' },
  PENDING_APPROVAL: { label: 'Pending approval', s: 'info' },
}
const st = (s: string) => statusMap[s] ?? { label: s.replace(/_/g, ' ').toLowerCase(), s: 'neutral' as Status }
const money = (m: Schema<'Money'> | null | undefined) => (m ? formatMoney(m.amount, m.currency) : '—')
const fmt = (iso: string | null) => (iso ? new Date(iso).toLocaleString('en-GB', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' }) : '—')
const rate = computed(() => { const r = data.value?.stats.autoMatchRate; return r == null ? '—' : `${(Number(r) * 100).toFixed(0)}%` })

function focus(c: { transactionId: string }) { void router.push({ path: '/matching', query: { ...route.query, transactionId: c.transactionId, exceptionId: undefined } }) }
function openGroup(g: Group) { const t = [...g.left, ...g.right][0]; if (t) focus(t) }

// ---- propose
const picked = ref<string[]>([])
const reason = ref('')
const problem = ref<CommandProblem | null>(null)
const pickedCandidates = computed(() => (data.value?.unmatched ?? []).filter((c) => picked.value.includes(c.transactionId)))
const canPropose = computed(() => !!me.value?.permissions.includes('matching.propose'))
const propose = useCommand((body: ReturnType<typeof buildProposeBody>, o) => proposeManualMatch(runId.value!, body, o))
async function submitPropose() {
  problem.value = null
  const body = buildProposeBody(pickedCandidates.value, reason.value)
  if (!body.reason || (!body.leftTransactionIds.length && !body.rightTransactionIds.length)) {
    problem.value = { kind: 'validation', title: 'Select at least one record and give a reason', fields: {} }; return
  }
  try {
    const g = await propose.run(body)
    message.success(`Proposed ${g.id}; a second person must approve it`)
    reason.value = ''; picked.value = []
    await Promise.all([wb.reload(), groups.reload()])
  } catch (e) { problem.value = describeCommandError(e) }
}

// ---- decide
const decideReason = ref('')
const decideOpen = ref<'APPROVE' | 'REJECT' | null>(null)
const decide = useCommand((a: { g: Group; decision: 'APPROVE' | 'REJECT'; reason: string }, o) =>
  decideManualMatch(runId.value!, a.g.id, { decision: a.decision, reason: a.reason, proposalVersion: a.g.approval?.proposalVersion }, { ...o, ifMatch: a.g.etag }))
const canApprove = computed(() => !!group.value?.approval?.canDecide && !!group.value.allowedActions?.includes('APPROVE_MANUAL_MATCH'))
const canReject = computed(() => !!group.value?.approval?.canDecide && !!group.value.allowedActions?.includes('REJECT_MANUAL_MATCH'))
async function submitDecision() {
  const g = group.value, d = decideOpen.value
  if (!g || !d) return
  problem.value = null
  if (!decideReason.value.trim()) { problem.value = { kind: 'validation', title: 'A reason is required', fields: {} }; return }
  try {
    await decide.run({ g, decision: d, reason: decideReason.value.trim() })
    message.success(d === 'APPROVE' ? 'Manual match approved' : 'Manual match rejected')
    decideOpen.value = null; decideReason.value = ''
    await Promise.all([wb.reload(), groups.reload()])
  } catch (e) { problem.value = describeCommandError(e); decideOpen.value = null }
}
async function reloadAfterConflict() { problem.value = null; await Promise.all([wb.reload(), groups.reload()]) }

const errStatus = computed(() => (isApiError(wb.error.value) ? wb.error.value.status : 0))
</script>

<template>
  <PageHeader title="Matching workbench" eyebrow="Matching · Bank ↔ instruction and ledger" subtitle="Precision workspace for records the automatic rules could not match safely." />

  <StatePlaceholder v-if="wb.state.value === 'loading'" state="loading" />
  <StatePlaceholder v-else-if="wb.state.value === 'forbidden'" state="forbidden" :message="wb.message.value" />
  <StatePlaceholder v-else-if="errStatus === 404" state="empty" message="The transaction or exception was not found in this run.">
    <template #action><a-button @click="router.push('/matching')">Open default workbench</a-button></template>
  </StatePlaceholder>
  <StatePlaceholder v-else-if="wb.state.value === 'error'" state="error" :message="wb.message.value"><template #action><a-button @click="wb.reload">Retry</a-button></template></StatePlaceholder>
  <template v-else-if="data">
    <div class="card strip">
      <div class="cell"><div class="lbl">Auto-match rate</div><div class="n">{{ rate }}</div><div class="sub">automatically matched groups</div></div>
      <div class="cell"><div class="lbl">Unmatched</div><div class="n failc">{{ data.stats.unmatchedCount }}</div><div class="sub">records</div></div>
      <div class="cell">
        <div class="lbl">Unmatched value</div>
        <div v-if="!data.stats.unmatchedByCurrency.length" class="n">—</div>
        <div v-for="t in data.stats.unmatchedByCurrency" :key="t.currency" class="n failc sm">{{ formatMoney(t.value.amount, t.value.currency) }} <span class="sub">{{ t.count }} records</span></div>
      </div>
      <div class="cell"><div class="lbl">Open groups</div><div class="n warnc">{{ data.stats.openGroups }}</div><div class="sub">not matched</div></div>
    </div>

    <a-alert v-if="problem" :type="problem.kind === 'validation' ? 'warning' : 'error'" show-icon closable class="gap" :message="problem.title" @close="problem = null">
      <template #description>
        <div v-if="problem.detail">{{ problem.detail }}</div>
        <div v-for="(m, k) in problem.fields" :key="k">{{ k }}: {{ m.join('; ') }}</div>
        <a-button v-if="problem.kind === 'stale'" size="small" style="margin-top: 6px" @click="reloadAfterConflict">Reload</a-button>
      </template>
    </a-alert>

    <div class="wrow">
      <div class="card pop">
        <div class="ch"><span class="lbl">Unmatched population</span><span class="aside">{{ data.unmatched.length }} of {{ data.stats.unmatchedCount }}</span></div>
        <a-empty v-if="!data.unmatched.length" description="No unmatched records." />
        <div v-for="c in data.unmatched" :key="c.transactionId" class="item" :class="{ sel: c.transactionId === data.focusTransactionId }">
          <a-checkbox v-if="canPropose" :checked="picked.includes(c.transactionId)" @change="(e: any) => (picked = e.target.checked ? [...picked, c.transactionId] : picked.filter((x) => x !== c.transactionId))" />
          <a class="mono" @click="focus(c)">{{ c.transactionId }}</a>
          <span class="sub">{{ c.side === 'RIGHT' ? 'evidence' : 'operational' }}</span>
          <span class="amt">{{ money(c.amount) }}</span>
        </div>
        <div v-if="data.unmatched.length < data.stats.unmatchedCount" class="more">Showing {{ data.unmatched.length }} of {{ data.stats.unmatchedCount }} unmatched records.</div>
      </div>

      <div class="stack">
        <StatePlaceholder v-if="!group" state="empty" :message="data.focusTransactionId ? `${data.focusTransactionId} is not part of any match group.` : 'No match group to show for this run.'" />
        <div v-else class="card">
          <div class="ch">
            <span class="lbl">Side by side</span><span class="mono did">{{ group.id }}</span>
            <StatusPill :status="st(group.status).s" :label="st(group.status).label" />
            <StatusPill v-if="group.manual" status="info" label="Manual" />
            <a-select v-if="data.groups.length > 1" v-model:value="selectedGroupId" size="small" class="aside" style="width: 180px" placeholder="Groups for this record">
              <a-select-option v-for="g in data.groups" :key="g.id" :value="g.id">{{ g.id }}</a-select-option>
            </a-select>
          </div>
          <div class="two">
            <div v-for="(side, i) in [group.left, group.right]" :key="i" class="side">
              <div class="lbl">{{ i === 0 ? 'Instruction / operational' : 'Bank / ledger evidence' }}</div>
              <a-empty v-if="!side.length" :description="i === 0 ? 'No records.' : 'No bank evidence found.'" />
              <div v-for="c in side" :key="c.label + c.transactionId" class="rec">
                <EvidenceLink :label="c.transactionId" :to="{ path: '/transactions', query: { ...route.query, id: c.transactionId } }" /> <span class="muted">{{ c.label }}</span>
                <div class="amt">{{ money(c.amount) }}</div>
                <small class="muted">{{ c.reference ?? '—' }} · {{ fmt(c.timestamp) }}<template v-if="c.score"> · score {{ c.score }}</template></small>
              </div>
            </div>
          </div>
          <div class="kvs top">
            <div class="kv"><span>Difference</span><b>{{ group.difference ? formatMoney(group.difference.amount, group.difference.currency) : 'Not comparable' }}</b></div>
          </div>
        </div>

        <div class="card">
          <div class="ch"><span class="lbl">Match groups</span>
            <a-select v-model:value="statusFilter" allow-clear size="small" class="aside" style="width: 180px" placeholder="All statuses"
              :options="Object.entries(statusMap).map(([v, m]) => ({ value: v, label: m.label }))" />
          </div>
          <StatePlaceholder v-if="groups.placeholder.value" :state="groups.placeholder.value" :message="groups.placeholder.value === 'empty' ? 'No match groups.' : undefined"><template #action><a-button @click="groups.reload">Retry</a-button></template></StatePlaceholder>
          <a-table v-else size="small" :pagination="false" row-key="id" :data-source="groups.items.value" :loading="groups.loading.value" :custom-row="(g: Group) => ({ onClick: () => openGroup(g), style: 'cursor:pointer' })"
            :columns="[{ title: 'Group', key: 'id' }, { title: 'Status', key: 'status' }, { title: 'Cardinality', dataIndex: 'cardinality', key: 'c' }, { title: 'Difference', key: 'd', align: 'right' }]">
            <template #bodyCell="{ column, record }">
              <span v-if="column.key === 'id'" class="mono">{{ record.id }}</span>
              <StatusPill v-else-if="column.key === 'status'" :status="st(record.status).s" :label="st(record.status).label" />
              <span v-else-if="column.key === 'd'">{{ money(record.difference) }}</span>
            </template>
          </a-table>
          <div class="pager"><a-button size="small" :disabled="!groups.hasPrev.value" @click="groups.prev">Previous</a-button><a-button size="small" :disabled="!groups.hasNext.value" @click="groups.next">Next</a-button></div>
        </div>
      </div>

      <div class="stack">
        <div v-if="group" class="card">
          <div class="ch"><span class="lbl">Match explanation</span></div>
          <div class="kvs top">
            <div class="kv"><span>Rule version</span><b>{{ group.ruleVersion ?? '—' }}</b></div>
            <div class="kv"><span>Tolerance</span><b>{{ group.tolerance ?? '—' }}</b></div>
            <div class="kv"><span>Cardinality</span><b>{{ group.cardinality ?? '—' }}</b></div>
            <div class="kv"><span>Rationale</span><b>{{ group.rationale ?? '—' }}</b></div>
            <div class="kv"><span>Exceptions</span><b>
              <template v-if="group.exceptionIds.length"><EvidenceLink v-for="e in group.exceptionIds" :key="e" :label="e" :to="`/exceptions/${e}`" /> </template><template v-else>None</template></b></div>
            <template v-if="group.approval">
              <div class="kv"><span>Approval</span><b>{{ group.approval.status }} · needs {{ group.approval.requiredRole }}</b></div>
              <div class="kv"><span>Proposer</span><b>{{ group.approval.proposer.name }}</b></div>
              <div v-if="group.approval.rationale" class="kv"><span>Reason</span><b>{{ group.approval.rationale }}</b></div>
            </template>
          </div>
        </div>

        <div v-if="group && group.approval?.status === 'PENDING'" class="card">
          <div class="ch"><span class="lbl">Decision</span></div>
          <div class="kvs top">
            <p v-if="!canApprove && !canReject" class="muted">You cannot decide this proposal (the proposer, or a user without the approval permission).</p>
            <template v-else>
              <label class="lbl">Reason (required)</label>
              <a-textarea v-model:value="decideReason" :rows="3" placeholder="Why do you approve or reject this match?" />
              <div class="btns">
                <a-button v-if="canApprove" type="primary" class="dark" :loading="decide.pending.value" @click="decideOpen = 'APPROVE'">Approve match</a-button>
                <a-button v-if="canReject" danger :loading="decide.pending.value" @click="decideOpen = 'REJECT'">Reject match</a-button>
              </div>
            </template>
          </div>
        </div>

        <div class="card">
          <div class="ch"><span class="lbl">Governed action</span></div>
          <div class="kvs top">
            <template v-if="canPropose">
              <label class="lbl">Reason (required)</label>
              <a-textarea v-model:value="reason" :rows="3" placeholder="Explain why the selected records belong together" />
              <p class="muted">{{ picked.length }} record(s) selected in the unmatched population. Every manual match needs a second-person approver; the proposer cannot approve.</p>
              <div class="btns">
                <a-button type="primary" class="dark" :loading="propose.pending.value" :disabled="!picked.length" @click="submitPropose">Propose manual match</a-button>
                <a-button v-if="group?.exceptionIds[0]" @click="router.push({ path: `/exceptions/${group.exceptionIds[0]}`, query: route.query })">Open exception {{ group.exceptionIds[0] }}</a-button>
              </div>
            </template>
            <p v-else class="muted">Your role cannot propose manual matches.</p>
          </div>
        </div>
      </div>
    </div>

    <a-modal :open="!!decideOpen" :title="decideOpen === 'APPROVE' ? 'Approve manual match?' : 'Reject manual match?'" :confirm-loading="decide.pending.value" :ok-text="decideOpen === 'APPROVE' ? 'Approve' : 'Reject'" @ok="submitDecision" @cancel="decideOpen = null">
      <p>{{ group?.id }} will be {{ decideOpen === 'APPROVE' ? 'approved' : 'rejected' }}. This is recorded in the audit trail with your reason.</p>
      <p class="muted">{{ decideReason || 'No reason entered yet.' }}</p>
    </a-modal>
  </template>
</template>

<style scoped>
.lbl { font-size: 10px; font-weight: 600; letter-spacing: 0.06em; text-transform: uppercase; color: var(--ink-3); }
.dark { background: var(--ink); }
.card { overflow: hidden; }
.gap { margin-bottom: 12px; }
.strip { display: flex; margin-bottom: 16px; }
.cell { flex: 1; padding: 14px 20px; display: flex; flex-direction: column; gap: 4px; border-right: 1px solid var(--border); }
.cell:last-child { border-right: 0; }
.n { font-size: 26px; font-weight: 500; line-height: 1.3; }
.n.sm { font-size: 18px; }
.sub, .muted, small { font-size: 11px; color: var(--ink-3); }
p.muted { margin: 0; }
.warnc { color: var(--warn); } .failc { color: var(--fail); }
.wrow { display: grid; grid-template-columns: 260px minmax(0, 1fr) 340px; gap: 16px; align-items: start; }
.stack { display: flex; flex-direction: column; gap: 16px; }
.ch { display: flex; align-items: center; gap: 10px; padding: 14px 20px; }
.aside { margin-left: auto; font-size: 12px; color: var(--ink-3); }
.did { color: var(--accent); font-weight: 600; }
.item { display: flex; align-items: center; gap: 8px; padding: 10px 20px; border-top: 1px solid var(--border); font-size: 12px; }
.item.sel { background: var(--accent-soft); }
.item .amt { margin-left: auto; }
.item a { cursor: pointer; }
.more { padding: 10px 20px; font-size: 11px; color: var(--ink-3); border-top: 1px solid var(--border); }
.mono { font-family: var(--font-mono); font-size: 12px; }
.amt { font-variant-numeric: tabular-nums; white-space: nowrap; }
.two { display: grid; grid-template-columns: 1fr 1fr; border-top: 1px solid var(--border); }
.side { padding: 14px 20px; }
.side + .side { border-left: 1px solid var(--border); }
.rec { padding: 8px 0; font-size: 12px; }
.kvs { padding: 0 20px 14px; display: flex; flex-direction: column; gap: 8px; }
.kvs.top { border-top: 1px solid var(--border); padding-top: 14px; }
.kv { display: flex; gap: 12px; font-size: 12px; }
.kv span { width: 90px; flex: none; color: var(--ink-3); }
.kv b { font-weight: 500; }
.btns { display: flex; flex-direction: column; align-items: stretch; gap: 8px; margin-top: 4px; }
.pager { display: flex; gap: 8px; justify-content: flex-end; padding: 10px 20px; }
</style>
