<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { message } from 'ant-design-vue'
import {
  getException, addExceptionNote, requestEvidence, assignException, proposeResolution, decideResolution, reopenException,
  type ExceptionDetail, type ResolutionKind, type Severity, type ExceptionStatus,
} from '../../api'
import { StatePlaceholder, StatusPill, MoneyAmount, EvidenceLink } from '../../components'

const route = useRoute()
const id = computed(() => String(route.params.id))
const ex = ref<ExceptionDetail | null>(null)
const loading = ref(true)
const err = ref<{ status?: number; message: string } | null>(null)
const busy = ref(false)

async function load() {
  loading.value = true
  err.value = null
  try { ex.value = await getException(id.value) } catch (e: any) { ex.value = null; err.value = { status: e?.status, message: e?.message ?? 'Failed to load' } }
  loading.value = false
}
watch(id, load, { immediate: true })

const sevMap: Record<Severity, 'fail' | 'warn' | 'neutral'> = { CRITICAL: 'fail', HIGH: 'fail', MEDIUM: 'warn', LOW: 'neutral' }
const stMap: Record<ExceptionStatus, 'fail' | 'warn' | 'pass' | 'info' | 'neutral'> = {
  OPEN: 'fail', IN_INVESTIGATION: 'info', WAITING_FOR_DATA: 'warn', PENDING_APPROVAL: 'warn', RESOLVED: 'pass', ACCEPTED_DIFFERENCE: 'pass', FALSE_POSITIVE: 'neutral',
}
const label = (s: string) => s.toLowerCase().replace(/_/g, ' ').replace(/^\w/, (c) => c.toUpperCase())
const when = (s: string) => new Date(s).toLocaleString('en-GB', { dateStyle: 'medium', timeStyle: 'short' })
const closed = computed(() => !!ex.value && ['RESOLVED', 'ACCEPTED_DIFFERENCE', 'FALSE_POSITIVE'].includes(ex.value.status))
const timeline = computed(() => [...(ex.value?.timeline ?? [])].reverse())
const stateNotes: Record<string, string> = {
  WAITING_FOR_DATA: 'Waiting for data: evidence has been requested.',
  CONFLICTING_EVIDENCE: 'Conflicting evidence between sources.',
  PENDING_EXTERNAL_ACTION: 'Pending external action.',
  PENDING_APPROVAL: 'Resolution pending approval.',
  SUPERSEDED_RUN: 'This exception belongs to a superseded run.',
}
const evidenceCols = [
  { title: 'Kind', dataIndex: 'kind', key: 'kind', width: 150 },
  { title: 'Record', dataIndex: 'label', key: 'label' },
  { title: 'Source record', key: 'src', width: 220 },
  { title: 'Amount', key: 'amount', align: 'right' as const, width: 180 },
]
const nodeLabel = (nid: string) => ex.value?.evidenceNodes.find((n) => n.id === nid)?.label ?? nid

async function run(fn: () => Promise<ExceptionDetail>, ok: string) {
  busy.value = true
  try { ex.value = await fn(); message.success(ok) } catch (e: any) { message.error(e?.message ?? 'Action failed') }
  busy.value = false
}

const note = ref('')
const addNote = async () => { if (!note.value.trim()) return; await run(() => addExceptionNote(id.value, note.value.trim()), 'Note added'); note.value = '' }
const evReq = ref('')
const askEvidence = async () => { if (!evReq.value.trim()) return; await run(() => requestEvidence(id.value, evReq.value.trim()), 'Evidence requested'); evReq.value = '' }
const newOwner = ref('')
const assign = async () => { if (!newOwner.value.trim()) return; await run(() => assignException(id.value, newOwner.value.trim()), 'Owner assigned'); newOwner.value = '' }

const kind = ref<ResolutionKind>('RESOLVED')
const reason = ref('')
const evidenceRefs = ref<string[]>([])
const proposedKind = computed<ResolutionKind>(() => {
  const t = [...(ex.value?.timeline ?? [])].reverse().find((e) => e.type === 'RESOLUTION_PROPOSED')?.text ?? ''
  return (['RESOLVED', 'ACCEPTED_DIFFERENCE', 'FALSE_POSITIVE'] as const).find((k) => t.startsWith(k)) ?? 'RESOLVED'
})
const propose = async () => {
  if (!reason.value.trim()) return message.warning('A reason is mandatory')
  if (!evidenceRefs.value.length) return message.warning('Attach at least one evidence record')
  await run(() => proposeResolution(id.value, { kind: kind.value, reason: reason.value.trim(), evidenceRefs: evidenceRefs.value }), 'Resolution proposed')
  reason.value = ''; evidenceRefs.value = []
}
const decide = (approve: boolean) => run(() => decideResolution(id.value, approve, proposedKind.value), approve ? 'Resolution approved' : 'Resolution rejected')
const reopenReason = ref('')
const reopen = async () => { if (!reopenReason.value.trim()) return message.warning('A reason is mandatory'); await run(() => reopenException(id.value, reopenReason.value.trim()), 'Exception reopened'); reopenReason.value = '' }
</script>

<template>
  <StatePlaceholder v-if="loading && !ex" state="loading" />
  <StatePlaceholder v-else-if="err?.status === 403" state="forbidden" />
  <StatePlaceholder v-else-if="err?.status === 404" state="empty" :message="`Exception ${id} was not found.`">
    <template #action><router-link to="/exceptions">Back to exceptions</router-link></template>
  </StatePlaceholder>
  <StatePlaceholder v-else-if="err" state="error" :message="err.message">
    <template #action><a-button @click="load">Retry</a-button></template>
  </StatePlaceholder>

  <template v-else-if="ex">
    <div class="crumb"><router-link to="/exceptions">Exceptions</router-link> / <span class="mono">{{ ex.id }}</span></div>
    <section class="card case">
      <div class="trow">
        <div>
          <div class="pills">
            <span class="mono id">{{ ex.id }}</span>
            <StatusPill :status="sevMap[ex.severity]" :label="label(ex.severity)" />
            <StatusPill :status="stMap[ex.status]" :label="label(ex.status)" />
            <StatusPill v-if="ex.blocking" status="fail" label="Blocking" />
          </div>
          <h1>{{ ex.title }}</h1>
        </div>
        <div class="assign">
          <a-input v-model:value="newOwner" placeholder="Assign owner" style="width: 160px" @pressEnter="assign" />
          <a-button type="primary" :loading="busy" @click="assign">Assign owner</a-button>
        </div>
      </div>
      <div class="grid">
        <div><div class="k">Financial value</div><div class="v"><MoneyAmount v-if="ex.value" :amount="ex.value.amount" :currency="ex.value.currency" /><span v-else>No financial value</span></div></div>
        <div><div class="k">Control</div><div class="v">{{ label(ex.control) }} · <span class="mono">{{ ex.reasonCode }}</span></div></div>
        <div><div class="k">Entity · date</div><div class="v">{{ ex.legalEntityId }} · {{ ex.businessDate }}</div></div>
        <div><div class="k">Owner</div><div class="v">{{ ex.owner ?? 'Unassigned' }}</div></div>
        <div><div class="k">Due</div><div class="v">{{ when(ex.dueAt) }}</div></div>
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
            <a-descriptions-item label="Source record"><EvidenceLink :label="`${ex.sourceFile} · ${ex.sourceRowId}`" to="/evidence" /></a-descriptions-item>
          </a-descriptions>
        </section>

        <section class="card pad">
          <h3>Evidence graph</h3>
          <StatePlaceholder v-if="!ex.evidenceNodes.length" state="empty" message="No linked evidence." />
          <a-table v-else :columns="evidenceCols" :data-source="ex.evidenceNodes" row-key="id" size="small" :pagination="false">
            <template #bodyCell="{ column, record }">
              <template v-if="column.key === 'kind'">{{ label(record.kind) }}</template>
              <template v-else-if="column.key === 'src'"><EvidenceLink :label="`${record.source} · ${record.sourceRowId}`" to="/evidence" /></template>
              <template v-else-if="column.key === 'amount'"><MoneyAmount v-if="record.amount" :amount="record.amount.amount" :currency="record.amount.currency" /><span v-else>–</span></template>
            </template>
          </a-table>
          <ul v-if="ex.evidenceEdges.length" class="edges">
            <li v-for="(e, i) in ex.evidenceEdges" :key="i">{{ nodeLabel(e.from) }} <b>{{ e.relation }}</b> {{ nodeLabel(e.to) }}</li>
          </ul>
        </section>

        <section class="card pad">
          <h3>Calculation and rule trace</h3>
          <a-descriptions :column="2" size="small" bordered>
            <a-descriptions-item label="Control">{{ label(ex.trace.control) }}</a-descriptions-item>
            <a-descriptions-item label="Rule"><span class="mono">{{ ex.trace.ruleId }} {{ ex.trace.ruleVersion }}</span></a-descriptions-item>
            <a-descriptions-item v-for="i in ex.trace.inputs" :key="i.name" :label="i.name"><span class="mono">{{ i.value }}</span></a-descriptions-item>
            <a-descriptions-item label="Tolerance" :span="2">{{ ex.trace.tolerance }}</a-descriptions-item>
            <a-descriptions-item label="Calculation" :span="2"><span class="mono">{{ ex.trace.calculation }}</span></a-descriptions-item>
            <a-descriptions-item label="Result" :span="2"><b>{{ ex.trace.result }}</b></a-descriptions-item>
          </a-descriptions>
        </section>

        <section class="card pad">
          <h3>Investigation timeline <span class="hint">append-only</span></h3>
          <a-timeline>
            <a-timeline-item v-for="t in timeline" :key="t.id">
              <div class="t-head"><StatusPill status="neutral" :label="label(t.type)" /> <span class="hint">{{ t.actor }} · {{ when(t.at) }}</span></div>
              <div>{{ t.text }}</div>
            </a-timeline-item>
          </a-timeline>
          <a-textarea v-model:value="note" :rows="2" placeholder="Add an investigation note or hypothesis" />
          <a-button class="mt" :loading="busy" @click="addNote">Add note</a-button>
        </section>
      </div>

      <div class="col side">
        <section class="card pad">
          <h3>Resolution</h3>
          <template v-if="ex.status === 'PENDING_APPROVAL'">
            <p>A resolution is awaiting approval. Policy: {{ ex.approvalPolicy }}</p>
            <a-space>
              <a-button type="primary" :loading="busy" @click="decide(true)">Approve</a-button>
              <a-button danger :loading="busy" @click="decide(false)">Reject</a-button>
            </a-space>
          </template>
          <template v-else-if="closed">
            <p>Closed as <b>{{ label(ex.status) }}</b>. The original control result is retained.</p>
            <a-input v-model:value="reopenReason" placeholder="Reason for reopening (required)" />
            <a-button class="mt" :loading="busy" @click="reopen">Reopen</a-button>
          </template>
          <template v-else>
            <a-radio-group v-model:value="kind" class="radios">
              <a-radio value="RESOLVED">Resolved</a-radio>
              <a-radio value="ACCEPTED_DIFFERENCE">Accepted difference</a-radio>
              <a-radio value="FALSE_POSITIVE">False positive</a-radio>
            </a-radio-group>
            <a-textarea v-model:value="reason" :rows="3" placeholder="Reason (mandatory)" />
            <a-select v-model:value="evidenceRefs" mode="multiple" class="mt full" placeholder="Evidence (mandatory)"
              :options="ex.evidenceNodes.map((n) => ({ value: n.id, label: `${n.id} · ${n.label}` }))" />
            <p class="hint mt">Approval policy: {{ ex.approvalPolicy }}</p>
            <a-button type="primary" :loading="busy" @click="propose">Propose resolution</a-button>
          </template>
        </section>

        <section class="card pad">
          <h3>Request evidence</h3>
          <a-textarea v-model:value="evReq" :rows="2" placeholder="What evidence is needed, and from whom?" />
          <a-button class="mt" :loading="busy" @click="askEvidence">Request evidence</a-button>
        </section>

        <section class="card pad">
          <h3>Ownership</h3>
          <a-descriptions :column="1" size="small">
            <a-descriptions-item label="Owner">{{ ex.owner ?? 'Unassigned' }}</a-descriptions-item>
            <a-descriptions-item label="Due">{{ when(ex.dueAt) }}</a-descriptions-item>
            <a-descriptions-item label="Approval">{{ ex.approvalPolicy }}</a-descriptions-item>
          </a-descriptions>
        </section>

        <section class="card pad">
          <h3>Linked records</h3>
          <div v-for="t in ex.relatedTransactionIds" :key="t"><EvidenceLink :label="t" :to="{ path: '/transactions', query: { id: t } }" /></div>
          <div v-for="a in ex.relatedAccountIds" :key="a"><EvidenceLink :label="a" :to="`/accounts/${a}`" /></div>
          <div><EvidenceLink label="Matching workbench" :to="{ path: '/matching', query: { exceptionId: ex.id } }" /></div>
          <div v-if="!ex.relatedTransactionIds.length && !ex.relatedAccountIds.length" class="hint">No linked transactions or accounts.</div>
        </section>
      </div>
    </div>
  </template>
</template>

<style scoped>
.crumb { color: var(--ink-3); margin-bottom: 12px; font-size: 12px; }
.crumb a { color: var(--accent); }
.pad { padding: 16px; margin-bottom: 16px; }
.case { padding: 20px 24px; margin-bottom: 16px; }
.trow { display: flex; justify-content: space-between; align-items: flex-start; gap: 24px; margin-bottom: 16px; }
h1 { margin: 8px 0 0; font-size: 22px; font-weight: 600; line-height: 1.35; }
.assign { display: flex; gap: 8px; }
h3 { font-size: 10px; font-weight: 600; text-transform: uppercase; letter-spacing: .08em; color: var(--ink-3); margin: 0 0 12px; }
.pills { display: flex; gap: 8px; align-items: center; }
.id { font-weight: 600; font-size: 12px; }
.grid { display: grid; grid-template-columns: repeat(5, 1fr); gap: 16px; }
.k { color: var(--ink-3); font-size: 10px; font-weight: 600; letter-spacing: .06em; text-transform: uppercase; margin-bottom: 4px; }
.v { color: var(--ink); font-size: 12px; }
.state { margin-top: 12px; }
.cols { display: grid; grid-template-columns: minmax(0, 1fr) 360px; gap: 16px; align-items: start; }
.hint { color: var(--ink-3); font-size: 12px; text-transform: none; letter-spacing: 0; font-weight: 400; }
.edges { margin: 12px 0 0; padding-left: 18px; color: var(--ink-2); }
.t-head { display: flex; gap: 8px; align-items: center; margin-bottom: 2px; }
.mt { margin-top: 8px; }
.full { width: 100%; }
.radios { display: flex; flex-direction: column; gap: 4px; margin-bottom: 8px; }
</style>
