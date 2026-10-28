<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { message, Modal } from 'ant-design-vue'
import { StatePlaceholder, StatusPill, EvidenceLink } from '../../components'
import { useRunContext } from '../../composables/useRunContext'
import { useCommand, useResource } from '../../api/composables'
import { formatMoney, isNotFound } from '../../api/client'
import {
  addExceptionNote, assignException, decideResolution, getException, listAssignableUsers, proposeResolution,
  reopenException, requestExceptionEvidence, type Schema,
} from '../../api/services'
import { allows, classifyFailure, decisionBody, proposalIdOf, proposalKindLabel, type CommandFailure } from './logic'

type Detail = Schema<'ExceptionDetail'>
const route = useRoute()
const { runId, me } = useRunContext()
const id = computed(() => String(route.params.id))
const res = useResource(() => id.value, (i, signal) => getException(i, { signal }))
const ex = ref<Detail>()
watch(() => res.data.value, (d) => { if (d) ex.value = d })
watch(id, () => { ex.value = undefined })
const notFound = computed(() => isNotFound(res.error.value))
const q = computed(() => (runId.value ? { run: runId.value } : {}))

const label = (s: string) => s.toLowerCase().replace(/_/g, ' ').replace(/^\w/, (c) => c.toUpperCase())
const when = (s: string) => new Date(s).toLocaleString('en-GB', { dateStyle: 'medium', timeStyle: 'short' })
const money = (m: { amount: string; currency: string } | null | undefined) => (m ? formatMoney(m.amount, m.currency) : '—')
const sevStatus = { CRITICAL: 'fail', HIGH: 'warn', MEDIUM: 'info', LOW: 'neutral' } as const
const stStatus = { OPEN: 'fail', IN_INVESTIGATION: 'info', WAITING_FOR_DATA: 'warn', PENDING_APPROVAL: 'warn', RESOLVED: 'pass', ACCEPTED_DIFFERENCE: 'pass', FALSE_POSITIVE: 'neutral' } as const
const stateNotes: Record<string, string> = {
  WAITING_FOR_DATA: 'Waiting for data: evidence has been requested.', CONFLICTING_EVIDENCE: 'Conflicting evidence between sources.',
  PENDING_EXTERNAL_ACTION: 'Pending external action.', PENDING_APPROVAL: 'Resolution pending approval.', SUPERSEDED_RUN: 'This exception belongs to a superseded run.',
}
const can = (a: Parameters<typeof allows>[1]) => allows(ex.value?.allowedActions, a)
const timeline = computed(() => [...(ex.value?.timeline ?? [])].reverse())
const nodeLabel = (nid: string) => ex.value?.evidenceNodes.find((n) => n.id === nid)?.label ?? nid
const actorName = (a: Detail['timeline'][number]['actor']) => a.name ?? a.id ?? (a.type === 'SYSTEM' ? 'System' : '—')
const evidenceCols = [
  { title: 'Kind', key: 'kind', width: 150 }, { title: 'Record', key: 'label' },
  { title: 'Source record', key: 'src', width: 230 }, { title: 'Amount', key: 'amount', align: 'right' as const, width: 190 },
]

// ---- commands ----
const failure = ref<CommandFailure | null>(null)
const busy = ref(false)
async function exec<T extends Detail>(run: () => Promise<T>, ok: string, after?: () => void): Promise<boolean> {
  failure.value = null; busy.value = true
  try { ex.value = await run(); message.success(ok); after?.(); return true }
  catch (e) { failure.value = classifyFailure(e); return false }
  finally { busy.value = false }
}
const reload = async () => { failure.value = null; await res.reload() }
const fieldMsg = (k: string) => failure.value?.fields[k]?.join(' ')

const noteCmd = useCommand((b: { text: string }, o) => addExceptionNote(id.value, b, o))
const evCmd = useCommand((b: { text: string; requestedFrom?: string }, o) => requestExceptionEvidence(id.value, b, o))
const assignCmd = useCommand((b: { ownerId: string | null; reason?: string }, o) => assignException(id.value, b, o))
const proposeCmd = useCommand((b: Schema<'ResolutionProposalInput'>, o) => proposeResolution(id.value, b, o))
const decideCmd = useCommand((b: { proposalId: string; body: Schema<'DecisionInput'> }, o) => decideResolution(id.value, b.proposalId, b.body, o))
const reopenCmd = useCommand((b: { reason: string }, o) => reopenException(id.value, b, o))

const note = ref('')
const addNote = () => note.value.trim() && exec(() => noteCmd.run({ text: note.value.trim() }), 'Note added', () => (note.value = ''))
const evText = ref(''); const evFrom = ref('')
const askEvidence = () => evText.value.trim() && exec(() => evCmd.run({ text: evText.value.trim(), requestedFrom: evFrom.value.trim() || undefined }), 'Evidence requested', () => { evText.value = ''; evFrom.value = '' })

const assignable = useResource(() => res.data.value?.legalEntityId, (le, signal) => listAssignableUsers(le, { signal }).catch(() => []), { isEmpty: () => false })
const ownerOptions = computed(() => {
  const base = (assignable.data.value ?? []).map((u) => ({ value: u.id, label: u.name }))
  if (me.value && !base.some((o) => o.value === me.value!.id)) base.unshift({ value: me.value.id, label: `${me.value.name} (me)` })
  return base
})
const newOwner = ref<string>()
const assign = (ownerId: string | null) => exec(() => assignCmd.run({ ownerId }), ownerId ? 'Owner assigned' : 'Unassigned', () => (newOwner.value = undefined))

const kind = ref<Schema<'ResolutionKind'>>('RESOLVED')
const reason = ref('')
const evidenceRefs = ref<string[]>([])
const refOptions = computed(() => (ex.value?.evidenceNodes ?? []).map((n) => ({ value: n.recordId ?? n.id, label: `${n.recordId ?? n.id} · ${n.label}` })))
function propose() {
  Modal.confirm({
    title: `Propose "${label(kind.value)}"?`, content: `This creates an approval request (policy: ${ex.value?.approvalPolicy}). The original control result is retained.`, okText: 'Propose',
    onOk: () => exec(() => proposeCmd.run({ kind: kind.value, reason: reason.value.trim(), evidenceRefs: evidenceRefs.value }), 'Resolution proposed', () => { reason.value = ''; evidenceRefs.value = [] }),
  })
}
const decideReason = ref('')
function decide(decision: 'APPROVE' | 'REJECT') {
  const p = ex.value?.pendingApproval
  if (!p) return
  Modal.confirm({
    title: decision === 'APPROVE' ? 'Approve this resolution?' : 'Reject this resolution?',
    content: `Proposal v${p.proposalVersion} by ${p.proposer.name}. This is a material decision and is audited.`,
    okText: decision === 'APPROVE' ? 'Approve' : 'Reject', okButtonProps: { danger: decision === 'REJECT' },
    onOk: () => exec(() => decideCmd.run({ proposalId: proposalIdOf(p), body: decisionBody(decision, decideReason.value, p.proposalVersion) }), decision === 'APPROVE' ? 'Resolution approved' : 'Resolution rejected', () => (decideReason.value = '')),
  })
}
const reopenReason = ref('')
const reopen = () => Modal.confirm({
  title: 'Reopen this exception?', okText: 'Reopen',
  onOk: () => exec(() => reopenCmd.run({ reason: reopenReason.value.trim() }), 'Exception reopened', () => (reopenReason.value = '')),
})
</script>

<template>
  <StatePlaceholder v-if="notFound" state="empty" :message="`Exception ${id} was not found or is outside your scope.`">
    <template #action><router-link :to="{ path: '/exceptions', query: q }">Back to exceptions</router-link></template>
  </StatePlaceholder>
  <StatePlaceholder v-else-if="!ex && res.placeholder.value" :state="res.placeholder.value === 'empty' ? 'error' : res.placeholder.value" :message="res.message.value">
    <template #action><a-button @click="res.reload()">Retry</a-button></template>
  </StatePlaceholder>

  <template v-else-if="ex">
    <div class="crumb"><router-link :to="{ path: '/exceptions', query: q }">Exceptions</router-link> / <span class="mono">{{ ex.id }}</span></div>

    <a-alert v-if="res.error.value && !notFound" type="warning" show-icon class="mb" message="Could not refresh this exception." :description="res.message.value">
      <template #action><a-button size="small" @click="res.reload()">Retry</a-button></template>
    </a-alert>
    <a-alert v-if="failure" :type="failure.kind === 'stale' ? 'warning' : 'error'" show-icon class="mb"
      :message="failure.kind === 'stale' ? 'This exception changed since you opened it' : failure.kind === 'sod' ? 'Separation of duties' : 'Action failed'"
      :description="failure.message" closable @close="failure = null">
      <template v-if="failure.kind === 'stale'" #action><a-button size="small" type="primary" @click="reload">Reload</a-button></template>
    </a-alert>

    <section class="card case">
      <div class="trow">
        <div>
          <div class="pills">
            <span class="mono id">{{ ex.id }}</span>
            <StatusPill :status="sevStatus[ex.severity]" :label="label(ex.severity)" />
            <StatusPill :status="stStatus[ex.status]" :label="label(ex.status)" />
            <StatusPill v-if="ex.blocking" status="fail" label="Blocking" />
          </div>
          <h1>{{ ex.title }}</h1>
        </div>
        <div v-if="can('ASSIGN')" class="assign">
          <a-select v-model:value="newOwner" placeholder="Assign owner" :options="ownerOptions" style="width: 190px" />
          <a-button type="primary" :disabled="!newOwner" :loading="busy" @click="assign(newOwner!)">Assign</a-button>
          <a-button v-if="ex.owner" :loading="busy" @click="assign(null)">Unassign</a-button>
        </div>
      </div>
      <div class="grid">
        <div><div class="k">Financial value</div><div class="v val">{{ ex.value ? money(ex.value) : 'No financial value' }}</div></div>
        <div><div class="k">Control</div><div class="v">{{ label(ex.control) }} · <span class="mono">{{ ex.reasonCode }}</span></div></div>
        <div><div class="k">Entity · date</div><div class="v">{{ ex.legalEntityId }} · {{ ex.businessDate }}</div></div>
        <div><div class="k">Owner</div><div class="v">{{ ex.owner?.name ?? 'Unassigned' }}</div></div>
        <div><div class="k">Due · age</div><div class="v">{{ when(ex.dueAt) }} · {{ ex.ageDays ?? '—' }}d</div></div>
        <div><div class="k">Rule version</div><div class="v mono">{{ ex.trace.ruleId }} {{ ex.trace.ruleVersion }}</div></div>
      </div>
      <a-alert v-for="s in ex.states" :key="s" type="warning" show-icon :message="stateNotes[s] ?? label(s)" class="state" />
    </section>

    <div class="cols">
      <div class="col">
        <section class="card pad">
          <h3>Case summary</h3>
          <a-descriptions :column="1" size="small" bordered>
            <a-descriptions-item label="What happened">{{ ex.description }}</a-descriptions-item>
            <a-descriptions-item label="Expected effect">{{ ex.expectedEffect }}</a-descriptions-item>
            <a-descriptions-item label="Investigation">{{ ex.summary }}</a-descriptions-item>
            <a-descriptions-item label="Source file"><span class="mono">{{ ex.sourceFile ?? '—' }}<template v-if="ex.sourceRowId"> · {{ ex.sourceRowId }}</template></span></a-descriptions-item>
          </a-descriptions>
        </section>

        <section class="card pad">
          <h3>Evidence graph</h3>
          <StatePlaceholder v-if="!ex.evidenceNodes.length" state="empty" message="No linked evidence." />
          <a-table v-else :columns="evidenceCols" :data-source="ex.evidenceNodes" row-key="id" size="small" :pagination="false">
            <template #bodyCell="{ column, record }">
              <template v-if="column.key === 'kind'">{{ label(record.kind) }}</template>
              <template v-else-if="column.key === 'label'">{{ record.label }}</template>
              <template v-else-if="column.key === 'src'">
                <EvidenceLink v-if="record.recordId" :label="record.recordId" :to="{ path: '/evidence', query: { ref: record.recordId, ...q } }" />
                <span v-else class="mono small">{{ record.source }}<template v-if="record.sourceRowId"> · {{ record.sourceRowId }}</template></span>
              </template>
              <template v-else-if="column.key === 'amount'"><span class="tab">{{ money(record.amount) }}</span></template>
            </template>
          </a-table>
          <ul v-if="ex.evidenceEdges.length" class="edges">
            <li v-for="(e, i) in ex.evidenceEdges" :key="i">{{ nodeLabel(e.from) }} <b>{{ label(e.relation) }}</b> {{ nodeLabel(e.to) }}</li>
          </ul>
        </section>

        <section class="card pad">
          <h3>Calculation and rule trace</h3>
          <a-descriptions :column="2" size="small" bordered>
            <a-descriptions-item label="Control">{{ label(ex.trace.control) }}</a-descriptions-item>
            <a-descriptions-item label="Rule"><span class="mono">{{ ex.trace.ruleId }} {{ ex.trace.ruleVersion }}</span></a-descriptions-item>
            <a-descriptions-item label="Tolerance" :span="2">{{ ex.trace.tolerance ?? '—' }}</a-descriptions-item>
            <a-descriptions-item label="Calculation" :span="2"><span class="mono">{{ ex.trace.calculation }}</span></a-descriptions-item>
            <a-descriptions-item label="Result" :span="2"><b>{{ ex.trace.result }}</b></a-descriptions-item>
            <a-descriptions-item :label="`Inputs (${ex.trace.inputs.length})`" :span="2">
              <div class="inputs"><span v-for="(i, n) in ex.trace.inputs.slice(0, 12)" :key="n" class="mono small">{{ i.name }}: {{ i.value }}</span></div>
              <div v-if="ex.trace.inputs.length > 12" class="hint">showing 12 of {{ ex.trace.inputs.length }} inputs</div>
            </a-descriptions-item>
          </a-descriptions>
        </section>

        <section class="card pad">
          <h3>Investigation timeline <span class="hint">append-only</span></h3>
          <a-timeline>
            <a-timeline-item v-for="t in timeline" :key="t.id">
              <div class="t-head"><StatusPill status="neutral" :label="label(t.type)" /> <span class="hint">{{ actorName(t.actor) }} · {{ when(t.at) }}</span></div>
              <div>{{ t.text }}</div>
            </a-timeline-item>
          </a-timeline>
          <template v-if="can('ADD_NOTE')">
            <a-textarea v-model:value="note" :rows="2" maxlength="4000" placeholder="Add an investigation note or hypothesis" />
            <a-button class="mt" :loading="busy" :disabled="!note.trim()" @click="addNote">Add note</a-button>
          </template>
        </section>
      </div>

      <div class="col side">
        <section class="card pad">
          <h3>Resolution</h3>
          <template v-if="ex.pendingApproval && ex.pendingApproval.status === 'PENDING'">
            <p>Proposal v{{ ex.pendingApproval.proposalVersion }} by {{ ex.pendingApproval.proposer.name }} awaits {{ label(ex.pendingApproval.requiredRole) }} approval. Policy: {{ ex.approvalPolicy }}.</p>
            <div class="small" data-test="proposal-detail">
              <div v-if="proposalKindLabel(ex.pendingApproval.kind)">Proposed resolution: <b>{{ proposalKindLabel(ex.pendingApproval.kind) }}</b></div>
              <div v-if="ex.pendingApproval.rationale">Reason: {{ ex.pendingApproval.rationale }}</div>
              <div v-if="ex.pendingApproval.evidenceRefs?.length">Evidence: <span v-for="r in ex.pendingApproval.evidenceRefs" :key="r" class="mono">{{ r }} </span></div>
            </div>
            <template v-if="can('APPROVE_RESOLUTION') || can('REJECT_RESOLUTION')">
              <a-textarea v-model:value="decideReason" :rows="2" placeholder="Decision reason (mandatory)" />
              <div v-if="fieldMsg('reason')" class="ferr">{{ fieldMsg('reason') }}</div>
              <a-space class="mt">
                <a-button v-if="can('APPROVE_RESOLUTION')" type="primary" :loading="busy" :disabled="decideReason.trim().length < 3" @click="decide('APPROVE')">Approve</a-button>
                <a-button v-if="can('REJECT_RESOLUTION')" danger :loading="busy" :disabled="decideReason.trim().length < 3" @click="decide('REJECT')">Reject</a-button>
              </a-space>
            </template>
            <p v-else class="hint">You cannot decide this proposal{{ ex.pendingApproval.canDecide === false ? ' (the server marks it as not decidable by you)' : '' }}.</p>
          </template>
          <template v-else-if="can('REOPEN')">
            <p>Closed as <b>{{ label(ex.status) }}</b>. The original control result is retained.</p>
            <a-input v-model:value="reopenReason" placeholder="Reason for reopening (required)" />
            <div v-if="fieldMsg('reason')" class="ferr">{{ fieldMsg('reason') }}</div>
            <a-button class="mt" :loading="busy" :disabled="reopenReason.trim().length < 3" @click="reopen">Reopen</a-button>
          </template>
          <template v-else-if="can('PROPOSE_RESOLUTION')">
            <a-radio-group v-model:value="kind" class="radios">
              <a-radio value="RESOLVED">Resolved</a-radio>
              <a-radio value="ACCEPTED_DIFFERENCE">Accepted difference</a-radio>
              <a-radio value="FALSE_POSITIVE">False positive</a-radio>
            </a-radio-group>
            <a-textarea v-model:value="reason" :rows="3" placeholder="Reason (mandatory, 3+ characters)" />
            <div v-if="fieldMsg('reason')" class="ferr">{{ fieldMsg('reason') }}</div>
            <a-select v-model:value="evidenceRefs" mode="multiple" class="mt full" placeholder="Evidence (at least one)" :options="refOptions" />
            <div v-if="fieldMsg('evidenceRefs')" class="ferr">{{ fieldMsg('evidenceRefs') }}</div>
            <p class="hint mt">Approval policy: {{ ex.approvalPolicy }}</p>
            <a-button type="primary" :loading="busy" :disabled="reason.trim().length < 3 || !evidenceRefs.length" @click="propose">Propose resolution</a-button>
          </template>
          <p v-else class="hint">No resolution action is available to you for this exception.</p>
        </section>

        <section v-if="can('REQUEST_EVIDENCE')" class="card pad">
          <h3>Request evidence</h3>
          <a-textarea v-model:value="evText" :rows="2" placeholder="What evidence is needed?" />
          <a-input v-model:value="evFrom" class="mt" placeholder="Requested from (optional)" />
          <a-button class="mt" :loading="busy" :disabled="!evText.trim()" @click="askEvidence">Request evidence</a-button>
        </section>

        <section class="card pad">
          <h3>Approvals</h3>
          <div v-if="!ex.approvals?.length" class="hint">No approvals yet.</div>
          <div v-for="a in ex.approvals" :key="a.id" class="appr">
            <StatusPill :status="a.status === 'APPROVED' ? 'pass' : a.status === 'PENDING' ? 'warn' : 'neutral'" :label="label(a.status)" />
            <span class="hint">v{{ a.proposalVersion }}<template v-if="proposalKindLabel(a.kind)"> · {{ proposalKindLabel(a.kind) }}</template> · {{ a.proposer.name }}<template v-if="a.approver"> → {{ a.approver.name }}</template></span>
            <div v-if="a.rationale" class="small">{{ a.rationale }}</div>
          </div>
        </section>

        <section class="card pad">
          <h3>Linked records</h3>
          <div v-for="t in ex.relatedTransactionIds" :key="t"><EvidenceLink :label="t" :to="{ path: '/transactions', query: { id: t, ...q } }" /></div>
          <div v-for="a in ex.relatedAccountIds" :key="a"><EvidenceLink :label="a" :to="{ path: `/accounts/${a}`, query: q }" /></div>
          <div><EvidenceLink label="Matching workbench" :to="{ path: '/matching', query: { exceptionId: ex.id, ...q } }" /></div>
          <div v-if="!ex.relatedTransactionIds.length && !ex.relatedAccountIds.length" class="hint">No linked transactions or accounts.</div>
        </section>
      </div>
    </div>
  </template>
</template>

<style scoped>
.crumb { color: var(--ink-3); margin-bottom: 12px; font-size: 12px; }
.crumb a { color: var(--accent); }
.mb { margin-bottom: 12px; }
.pad { padding: 16px; margin-bottom: 16px; }
.case { padding: 20px 24px; margin-bottom: 16px; }
.trow { display: flex; justify-content: space-between; align-items: flex-start; gap: 24px; margin-bottom: 16px; }
h1 { margin: 8px 0 0; font-size: 20px; font-weight: 600; line-height: 1.35; }
.assign { display: flex; gap: 8px; flex-shrink: 0; }
h3 { font-size: 10px; font-weight: 600; text-transform: uppercase; letter-spacing: .08em; color: var(--ink-3); margin: 0 0 12px; }
.pills { display: flex; gap: 8px; align-items: center; }
.id { font-weight: 600; font-size: 12px; }
.grid { display: grid; grid-template-columns: repeat(6, 1fr); gap: 16px; }
.k { color: var(--ink-3); font-size: 10px; font-weight: 600; letter-spacing: .06em; text-transform: uppercase; margin-bottom: 4px; }
.v { color: var(--ink); font-size: 12px; }
.val { font-weight: 600; font-variant-numeric: tabular-nums; }
.state { margin-top: 12px; }
.cols { display: grid; grid-template-columns: minmax(0, 1fr) 360px; gap: 16px; align-items: start; }
.hint { color: var(--ink-3); font-size: 12px; text-transform: none; letter-spacing: 0; font-weight: 400; }
.small { font-size: 11px; }
.tab { font-variant-numeric: tabular-nums; white-space: nowrap; }
.edges { margin: 12px 0 0; padding-left: 18px; color: var(--ink-2); }
.inputs { display: flex; flex-wrap: wrap; gap: 4px 12px; }
.t-head { display: flex; gap: 8px; align-items: center; margin-bottom: 2px; }
.mt { margin-top: 8px; }
.full { width: 100%; }
.ferr { color: var(--fail); font-size: 12px; margin-top: 4px; }
.radios { display: flex; flex-direction: column; gap: 4px; margin-bottom: 8px; }
.appr { margin-bottom: 8px; display: flex; flex-direction: column; gap: 2px; align-items: flex-start; }
</style>
