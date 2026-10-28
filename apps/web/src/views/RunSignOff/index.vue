<script setup lang="ts">
import { computed, ref } from 'vue'
import { message } from 'ant-design-vue'
import { PageHeader, StatePlaceholder, StatusPill, EvidenceLink } from '../../components'
import { formatMoney } from '../../api/client'
import {
  downloadEvidencePack, generateEvidencePack, getRun, getRunSignOff, listRunOpenMatters, requestRunReprocess,
  returnRun, signOffRun, submitRunForSignOff,
} from '../../api/services'
import type { Schema } from '../../api/services'
import { usePaged, useCommand, useResource } from '../../api/composables'
import { useRunContext } from '../../composables/useRunContext'
import { ctrlStatus, exceptionLink, fmtDate, human, sevStatus, showing } from '../ControlRoom/logic'
import { can, mapCommandError, reasonError, type CommandProblem, type SignOffAction } from './logic'

type Pack = Schema<'RunSignOff'>
const ctx = useRunContext()
const { runId } = ctx
const res = useResource(() => runId.value, (id, signal) => getRunSignOff(id, { openMattersLimit: 20 }, { signal }))
const override = ref<Pack | null>(null) // the response of the last command is the new pack (and its ETag is cached)
const data = computed<Pack | undefined>(() => override.value ?? res.data.value)
async function reload() { override.value = null; problem.value = null; await res.reload(); if (drill.value) await matters.reload() }

const allowed = computed(() => data.value?.allowedActions)
const q = computed(() => (runId.value ? { run: runId.value } : {}))
const signStatus = computed(() => {
  const s = data.value?.run.signOffState
  return s === 'SIGNED_OFF' ? 'pass' : s === 'READY_FOR_REVIEW' || s === 'APPROVAL_PENDING' ? 'info' : s === 'INVALIDATED' || s === 'RETURNED' ? 'fail' : 'warn'
})
const resultRoute = (a: string) =>
  a === 'matching' ? '/matching' : a === 'safeguarding' ? '/safeguarding' : a === 'data_completeness' ? '/sources' : '/exceptions'
const money = (m: Schema<'Money'>) => formatMoney(m.amount, m.currency)

// open matters drill-down (paged)
const drill = ref(false)
const blockingOnly = ref(false)
const matters = usePaged<Schema<'ExceptionSummary'>>(
  (p, signal) => (runId.value && drill.value
    ? listRunOpenMatters(runId.value, { ...p, blocking: blockingOnly.value || undefined }, { signal })
    : Promise.resolve({ items: [], page: { hasMore: false, nextCursor: null } })),
  [runId, drill, blockingOnly], { limit: 25, includeTotal: true })
const matterCols = [
  { title: 'Exception', key: 'id' }, { title: 'Title', key: 'title' }, { title: 'Control', key: 'control' },
  { title: 'Severity', key: 'sev' }, { title: 'Status', key: 'status' }, { title: 'Value', key: 'value', align: 'right' as const },
]

// commands
type Kind = SignOffAction | 'REPROCESS' | 'PACK'
const modal = ref<Kind | null>(null)
const reason = ref('')
const note = ref('')
const attested = ref(false)
const problem = ref<CommandProblem | null>(null)
const submitCmd = useCommand((b: { note?: string }, o) => submitRunForSignOff(runId.value!, b, o))
const returnCmd = useCommand((b: { reason: string }, o) => returnRun(runId.value!, b, o))
const approveCmd = useCommand((b: Parameters<typeof signOffRun>[1], o) => signOffRun(runId.value!, b, o))
const reprocessCmd = useCommand(async (b: { reason: string }, o) => {
  const r = await getRun(runId.value!) // fresh ETag of the run for If-Match
  void r
  return requestRunReprocess(runId.value!, b, o)
})
const packCmd = useCommand((_: null, o) => generateEvidencePack(runId.value!, o))
const busy = computed(() => [submitCmd, returnCmd, approveCmd, reprocessCmd, packCmd].some((c) => c.pending.value))

function open(k: Kind) { modal.value = k; reason.value = ''; note.value = ''; attested.value = false; problem.value = null }
const fieldMsg = computed(() => (problem.value?.kind === 'validation' ? Object.values(problem.value.fields).flat().join(' ') : ''))
const localReasonErr = ref<string | null>(null)
const titles: Record<Kind, string> = {
  SUBMIT: 'Submit for sign-off', RETURN: 'Return to preparer', APPROVE: 'Sign off run', REPROCESS: 'Request reprocess', PACK: 'Generate evidence pack',
}

async function confirm() {
  const k = modal.value!
  localReasonErr.value = null
  if ((k === 'RETURN' || k === 'APPROVE' || k === 'REPROCESS') && (localReasonErr.value = reasonError(reason.value))) return
  if (k === 'APPROVE' && !attested.value) { localReasonErr.value = 'You must confirm the attestation.'; return }
  problem.value = null
  try {
    if (k === 'SUBMIT') override.value = await submitCmd.run({ note: note.value.trim() || undefined })
    else if (k === 'RETURN') override.value = await returnCmd.run({ reason: reason.value.trim() })
    else if (k === 'APPROVE') override.value = await approveCmd.run({ attestation: true, attestationTextHash: data.value?.attestationTextHash, reason: reason.value.trim() })
    else if (k === 'REPROCESS') {
      const r = await reprocessCmd.run({ reason: reason.value.trim() })
      message.success(`Reprocess accepted: new run ${r.id}`)
      ctx.selectRun(r.id)
      await ctx.reload()
    } else { const p = await packCmd.run(null); message.success(`Evidence pack ${p.id} ${p.status.toLowerCase().replace('_', ' ')}`); await reload() }
    modal.value = null
    if (k !== 'REPROCESS' && k !== 'PACK') message.success(`${titles[k]}: done`)
  } catch (e) {
    problem.value = mapCommandError(e)
  }
}
async function download() {
  try {
    const { blob, filename } = await downloadEvidencePack(runId.value!)
    const a = document.createElement('a')
    a.href = URL.createObjectURL(blob); a.download = filename ?? 'evidence-pack.zip'; a.click(); URL.revokeObjectURL(a.href)
  } catch (e) { message.error(mapCommandError(e).message) }
}
</script>

<template>
  <PageHeader title="Run sign-off" subtitle="An accountable decision point. Every control links to its result and cannot be marked passed by hand.">
    <template #actions>
      <a-button v-if="data" :disabled="busy" @click="open('REPROCESS')">Request reprocess</a-button>
      <a-button v-if="data" :disabled="busy" @click="open('PACK')">Generate evidence pack</a-button>
      <a-button v-if="data?.evidencePackId" :disabled="busy" @click="download">Download pack</a-button>
      <a-button v-if="can(allowed, 'SUBMIT')" :disabled="busy" @click="open('SUBMIT')">Submit for sign-off</a-button>
      <a-button v-if="can(allowed, 'RETURN')" :disabled="busy" @click="open('RETURN')">Return to preparer</a-button>
      <a-button v-if="can(allowed, 'APPROVE')" type="primary" :disabled="busy" @click="open('APPROVE')">Sign off</a-button>
    </template>
  </PageHeader>

  <StatePlaceholder v-if="!runId && !ctx.loading.value" state="empty" message="No run available." />
  <StatePlaceholder v-else-if="res.placeholder.value && !data" :state="res.placeholder.value" :message="res.message.value">
    <template #action><a-button @click="reload">Retry</a-button></template>
  </StatePlaceholder>
  <template v-else-if="data">
    <a-alert v-if="problem && !modal" :type="problem.kind === 'stale' ? 'warning' : 'error'" show-icon class="gap" :message="problem.message">
      <template v-if="problem.kind === 'stale'" #action><a-button size="small" @click="reload">Reload</a-button></template>
    </a-alert>

    <div class="banner" :class="{ ok: !data.blockers.length }">
      <div class="bt">
        <b>{{ data.blockers.length ? 'Preparation incomplete' : 'Preparation complete' }}</b>
        <span v-if="data.blockers.length">{{ data.blockers.length }} blocker(s). Sign-off unlocks when each is resolved or covered by an approved qualification.</span>
      </div>
      <StatusPill :status="signStatus" :label="human(data.run.signOffState)" />
      <div class="kv"><i>Legal entity</i>{{ data.run.legalEntityId }}</div>
      <div class="kv"><i>Business date</i>{{ data.run.businessDate }}</div>
      <div class="kv"><i>Run</i>v{{ data.run.version }} · {{ data.run.state }}</div>
      <div class="kv"><i>Preparer</i>{{ data.run.preparer?.name ?? '—' }}</div>
      <div class="kv"><i>Required approver</i>{{ data.run.requiredApprover?.name ?? '—' }}</div>
      <div v-if="data.run.signedOffBy" class="kv"><i>Signed off</i>{{ data.run.signedOffBy.name }} · {{ fmtDate(data.run.signedOffAt) }}</div>
    </div>

    <div v-if="data.blockers.length" class="card block" data-test="blockers">
      <h3>Blockers ({{ data.blockers.length }})</h3>
      <ul class="plain"><li v-for="b in data.blockers" :key="b">{{ b }}</li></ul>
    </div>

    <div class="cols">
      <div class="left">
        <div class="card block">
          <h3>Mandatory control checklist</h3>
          <a-table :data-source="data.checklist" row-key="id" :pagination="false" size="small">
            <a-table-column title="Control" data-index="label" />
            <a-table-column title="State" key="state">
              <template #default="{ record }"><StatusPill :status="ctrlStatus(record.state)" :label="human(record.state)" /></template>
            </a-table-column>
            <a-table-column title="Result" key="note">
              <template #default="{ record }">{{ record.note }}<span v-if="record.exceptionCount"> · {{ showing(record.exceptionIds.length, record.exceptionCount) }} exceptions</span></template>
            </a-table-column>
            <a-table-column title="" key="link">
              <template #default="{ record }"><EvidenceLink label="Open result" :to="{ path: resultRoute(record.control), query: q }" /></template>
            </a-table-column>
          </a-table>
        </div>

        <div class="card block" data-test="open-matters">
          <div class="row">
            <h3>Open matters</h3>
            <span class="muted">{{ showing(data.openMatters.length, data.openMattersTotal) }}</span>
            <a-button v-if="(data.openMattersTotal ?? 0) > data.openMatters.length" size="small" @click="drill = !drill">{{ drill ? 'Hide full list' : 'Browse all' }}</a-button>
          </div>
          <div v-if="data.openMattersByCategory?.length" class="cats">
            <a-tag v-for="c in data.openMattersByCategory" :key="c.control">{{ human(c.control) }}: {{ c.total }}<template v-if="c.blocking"> ({{ c.blocking }} blocking)</template></a-tag>
          </div>
          <template v-if="!drill">
            <a-table :data-source="data.openMatters" :columns="matterCols" row-key="id" :pagination="false" size="small" :locale="{ emptyText: 'No unresolved matters.' }">
              <template #bodyCell="{ column, record }">
                <EvidenceLink v-if="column.key === 'id'" :label="record.id" :to="exceptionLink(record.id, runId)" />
                <template v-else-if="column.key === 'title'">{{ record.title }} <a-tag v-if="record.blocking" color="red">Blocking</a-tag></template>
                <template v-else-if="column.key === 'control'">{{ human(record.control) }}</template>
                <StatusPill v-else-if="column.key === 'sev'" :status="sevStatus(record.severity)" :label="human(record.severity)" />
                <template v-else-if="column.key === 'status'">{{ human(record.status) }}</template>
                <span v-else-if="column.key === 'value'" class="tabular">{{ record.value ? money(record.value) : '—' }}</span>
              </template>
            </a-table>
          </template>
          <template v-else>
            <a-checkbox v-model:checked="blockingOnly" class="gap">Blocking only</a-checkbox>
            <StatePlaceholder v-if="matters.placeholder.value" :state="matters.placeholder.value">
              <template #action><a-button @click="matters.reload">Retry</a-button></template>
            </StatePlaceholder>
            <a-table v-else :data-source="matters.items.value" :columns="matterCols" row-key="id" :pagination="false" size="small" :loading="matters.loading.value">
              <template #bodyCell="{ column, record }">
                <EvidenceLink v-if="column.key === 'id'" :label="record.id" :to="exceptionLink(record.id, runId)" />
                <template v-else-if="column.key === 'title'">{{ record.title }} <a-tag v-if="record.blocking" color="red">Blocking</a-tag></template>
                <template v-else-if="column.key === 'control'">{{ human(record.control) }}</template>
                <StatusPill v-else-if="column.key === 'sev'" :status="sevStatus(record.severity)" :label="human(record.severity)" />
                <template v-else-if="column.key === 'status'">{{ human(record.status) }}</template>
                <span v-else-if="column.key === 'value'" class="tabular">{{ record.value ? money(record.value) : '—' }}</span>
              </template>
            </a-table>
            <div class="pager">
              <span class="muted">Page {{ matters.pageIndex.value + 1 }}<template v-if="matters.total.value != null"> · {{ matters.total.value }} total</template></span>
              <a-button size="small" :disabled="!matters.hasPrev.value" @click="matters.prev">Previous</a-button>
              <a-button size="small" :disabled="!matters.hasNext.value" @click="matters.next">Next</a-button>
            </div>
          </template>
        </div>
      </div>

      <div class="right">
        <div class="card block">
          <h3>Changes since preparation</h3>
          <ul v-if="data.changesSincePreparation.length"><li v-for="c in data.changesSincePreparation" :key="c">{{ c }}</li></ul>
          <span v-else class="muted">No changes.</span>
          <template v-if="data.qualifications?.length"><h3 class="mt">Qualifications</h3><ul><li v-for="c in data.qualifications" :key="c">{{ c }}</li></ul></template>
        </div>

        <div class="card block" data-test="attestation">
          <h3>Attestation</h3>
          <p>{{ data.attestationText }}</p>
          <div class="muted">SHA-256 <code class="hash">{{ data.attestationTextHash ?? '—' }}</code></div>
          <a-descriptions :column="1" size="small" class="mt">
            <a-descriptions-item label="Evidence pack">{{ data.evidencePackId ?? 'Not generated' }}</a-descriptions-item>
            <a-descriptions-item v-if="data.approval" label="Approval">{{ data.approval.status }} · {{ data.approval.proposer.name }}</a-descriptions-item>
          </a-descriptions>
          <a-alert v-if="!can(allowed, 'APPROVE')" type="info" show-icon class="gap" message="Sign-off is not available to you for this run in its current state." />
        </div>

        <div v-if="data.history?.length" class="card block">
          <h3>History</h3>
          <ul class="plain"><li v-for="h in data.history" :key="h.id"><span class="muted">{{ fmtDate(h.at) }} · {{ h.actor.name }}</span><br />{{ h.text }}</li></ul>
        </div>
      </div>
    </div>
  </template>

  <a-modal :open="!!modal" :title="modal ? titles[modal] : ''" ok-text="Confirm" :confirm-loading="busy" :ok-button-props="{ disabled: busy }"
    @ok="confirm" @cancel="modal = null">
    <a-alert v-if="problem" :type="problem.kind === 'stale' ? 'warning' : 'error'" show-icon class="gap" :message="problem.message">
      <template v-if="problem.kind === 'stale'" #action><a-button size="small" @click="modal = null; reload()">Reload</a-button></template>
    </a-alert>
    <p v-if="modal === 'APPROVE'" class="muted">{{ data?.attestationText }}</p>
    <p v-if="modal === 'PACK'">Generates (or regenerates) the evidence pack for this run. This is recorded in the audit trail.</p>
    <p v-if="modal === 'REPROCESS'">Creates a new run version that supersedes this one.</p>
    <a-textarea v-if="modal === 'SUBMIT'" v-model:value="note" :rows="2" placeholder="Note (optional)" />
    <template v-if="modal === 'RETURN' || modal === 'APPROVE' || modal === 'REPROCESS'">
      <a-textarea v-model:value="reason" :rows="3" placeholder="Reason (required, recorded in the audit event)" />
      <div v-if="localReasonErr || fieldMsg" class="err">{{ localReasonErr || fieldMsg }}</div>
    </template>
    <a-checkbox v-if="modal === 'APPROVE'" v-model:checked="attested" class="gap">I attest to the statement above</a-checkbox>
  </a-modal>
</template>

<style scoped>
.block { padding: 16px; margin-bottom: 16px; }
h3 { margin: 0 0 12px; font-size: 14px; font-weight: 600; }
h3.mt, .mt { margin-top: 12px; }
.banner { display: flex; flex-wrap: wrap; gap: 20px; align-items: center; background: #FBEFD9; border: 1px solid #EBD3A4; border-radius: 8px; padding: 14px 16px; margin-bottom: 16px; }
.banner.ok { background: #E3F2E8; border-color: #B7DCC2; }
.bt { flex: 1; min-width: 240px; display: flex; flex-direction: column; gap: 2px; font-size: 13px; }
.bt b { font-size: 15px; }
.kv { display: flex; flex-direction: column; font-size: 13px; font-weight: 500; }
.kv i { font-style: normal; font-size: 11px; color: var(--ink-3); text-transform: uppercase; }
.cols { display: grid; grid-template-columns: 2fr 1fr; gap: 16px; align-items: start; }
.left, .right { min-width: 0; }
.gap { margin: 8px 0; display: block; }
.row { display: flex; align-items: center; gap: 12px; margin-bottom: 8px; }
.row h3 { margin: 0; }
.row .muted { flex: 1; }
.muted { color: var(--ink-3); font-size: 12px; }
.cats { margin-bottom: 8px; }
.plain { margin: 0; padding-left: 18px; font-size: 13px; }
.pager { display: flex; justify-content: flex-end; align-items: center; gap: 8px; margin-top: 8px; }
.hash { word-break: break-all; font-size: 11px; }
.err { color: var(--fail); font-size: 12px; margin-top: 4px; }
</style>
