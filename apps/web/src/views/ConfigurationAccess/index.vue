<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { message } from 'ant-design-vue'
import { decideConfigChange, getSettings, listApprovals, listConfigChanges, proposeConfigChange } from '../../api/services'
import type { Schema } from '../../api/services'
import { useAsync, useCommand } from '../../api/composables'
import { fieldErrors, isApiError, isSeparationOfDuties, isStale, isValidation } from '../../api/client'
import { PageHeader, StatePlaceholder, StatusPill } from '../../components'
import type { Status } from '../../components/StatusPill.vue'
import { validateProposal } from './proposeValidation'
import { useRunContext } from '../../composables/useRunContext'
import { reloadApprovals } from '../../composables/useShellStatus'

type Change = Schema<'ConfigChange'>
type Kind = Schema<'ConfigTargetKind'>

const { can } = useRunContext()
const tab = ref('users')
const settings = useAsync((s) => getSettings({}, { signal: s }))
const pending = useAsync((s) => listConfigChanges({ status: 'PENDING_APPROVAL', limit: 50 }, { signal: s }))
const history = useAsync((s) => listConfigChanges({ limit: 50 }, { signal: s }))
const inbox = useAsync((s) => listApprovals({ limit: 50 }, { signal: s }))
async function reloadAll() {
  await Promise.all([settings.reload(), pending.reload(), history.reload(), inbox.reload(), reloadApprovals()])
}

const label = (s: string) => s.charAt(0) + s.slice(1).toLowerCase().replace(/_/g, ' ')
const sev = (s: string): Status => (s === 'CRITICAL' ? 'fail' : s === 'HIGH' ? 'warn' : 'neutral')
const statusPill = (s: string): Status => (s === 'ACTIVE' || s === 'APPROVED' ? 'pass' : s === 'REJECTED' ? 'fail' : s === 'PENDING_APPROVAL' ? 'warn' : 'neutral')
const when = (iso?: string | null) => (iso ? iso.replace('T', ' ').slice(0, 16) + ' UTC' : '—')
const canPropose = computed(() => can('settings.draft') || can('access.manage'))
const permFor = (k: Kind) => (k === 'USER_ACCESS' ? 'access.manage' : 'settings.approve')

// ---- propose ----
const propose = reactive({ open: false, targetKind: 'RULE' as Kind, targetId: '', after: '{}', effectiveFrom: '', reason: '' })
const proposeErrors = ref<Record<string, string[]>>({})
const proposeCmd = useCommand((b: Schema<'ConfigChangeInput'>, o) => proposeConfigChange(b, o))
const afterError = ref('')
function openPropose(kind: Kind, id: string, current: object) {
  Object.assign(propose, { open: true, targetKind: kind, targetId: id, after: JSON.stringify(current, null, 2), effectiveFrom: '', reason: '' })
  proposeErrors.value = {}; afterError.value = ''; proposeCmd.reset()
}
const rowCurrent = (r: object) => { const { pendingChangeId: _p, ...rest } = r as Record<string, unknown>; return rest }
const errOf = (key: string) => [...(proposeErrors.value[key] ?? []), ...Object.entries(proposeErrors.value).filter(([k]) => k.startsWith(key + '/')).flatMap(([k, v]) => v.map((m) => `${k.slice(key.length + 1)}: ${m}`))]
async function submitPropose() {
  afterError.value = ''; proposeErrors.value = {}
  let after: Record<string, unknown>
  try {
    const parsed: unknown = JSON.parse(propose.after)
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) throw new Error('must be a JSON object')
    after = parsed as Record<string, unknown>
  } catch (e) { afterError.value = `Proposed object is not valid JSON: ${(e as Error).message}`; return }
  const local = validateProposal(propose)
  if (Object.keys(local).length) { proposeErrors.value = local; return }
  try {
    await proposeCmd.run({ targetKind: propose.targetKind, targetId: propose.targetId.trim(), after, effectiveFrom: propose.effectiveFrom.trim(), reason: propose.reason.trim() })
    propose.open = false
    message.success('Change proposed; it now awaits a second person')
    await reloadAll()
  } catch (e) {
    if (isValidation(e)) proposeErrors.value = fieldErrors(e)
  }
}
const proposeProblem = computed(() => {
  const e = proposeCmd.error.value
  return isApiError(e) && !isValidation(e) || (isValidation(e) && !Object.keys(proposeErrors.value).length) ? (e as Error).message : ''
})

// ---- decide ----
const decide = reactive({ open: false, change: null as Change | null, decision: 'APPROVE' as 'APPROVE' | 'REJECT', reason: '' })
const decideCmd = useCommand((a: { change: Change; decision: 'APPROVE' | 'REJECT'; reason: string }, o) =>
  decideConfigChange(a.change.id, { decision: a.decision, reason: a.reason, proposalVersion: a.change.approval?.proposalVersion }, { ...o, ifMatch: a.change.etag }))
function openDecide(change: Change, decision: 'APPROVE' | 'REJECT') {
  Object.assign(decide, { open: true, change, decision, reason: '' }); decideCmd.reset()
}
async function submitDecide() {
  if (!decide.change || !decide.reason.trim()) return
  try {
    await decideCmd.run({ change: decide.change, decision: decide.decision, reason: decide.reason.trim() })
    decide.open = false
    message.success(decide.decision === 'APPROVE' ? 'Change approved' : 'Change rejected')
    await reloadAll()
  } catch { /* shown in the dialog */ }
}
const decideErr = computed(() => decideCmd.error.value)
const stale = computed(() => isStale(decideErr.value))
async function reloadAfterStale() { decide.open = false; await reloadAll() }

const diffText = (v: unknown) => (v === undefined ? '—' : typeof v === 'string' ? v : JSON.stringify(v))
const sourceCols = [
  { title: 'Source', dataIndex: 'sourceName', key: 'sourceName' }, { title: 'Schema version', dataIndex: 'schemaVersion' },
  { title: 'Expected by (local)', dataIndex: 'expectedByLocalTime' }, { title: 'Mandatory', key: 'mandatory' }, { title: '', key: 'act', width: 120 },
]
const ruleCols = [
  { title: 'Rule', dataIndex: 'ruleId' }, { title: 'Control', key: 'control' }, { title: 'Version', dataIndex: 'version' },
  { title: 'Description', dataIndex: 'description' }, { title: 'Tolerance', dataIndex: 'tolerance' }, { title: 'Severity', key: 'severity' },
  { title: 'Effective from', dataIndex: 'effectiveFrom' }, { title: '', key: 'act', width: 120 },
]
const userCols = [
  { title: 'Name', dataIndex: 'name' }, { title: 'Email', dataIndex: 'email' }, { title: 'Roles', key: 'roles' },
  { title: 'Legal-entity scope', key: 'scope' }, { title: '', key: 'act', width: 120 },
]
const historyCols = [
  { title: 'Change', dataIndex: 'id' }, { title: 'Target', key: 'target' }, { title: 'Status', key: 'status' },
  { title: 'Proposed by', key: 'proposer' }, { title: 'Proposed', key: 'at' }, { title: 'Effective from', dataIndex: 'effectiveFrom' },
  { title: 'Ruleset', key: 'ruleset' }, { title: 'Reason', dataIndex: 'reason' },
]
const inboxCols = [
  { title: 'Type', key: 'type' }, { title: 'Object', dataIndex: 'objectId' }, { title: 'Proposed by', key: 'proposer' },
  { title: 'Required role', key: 'role' }, { title: 'Proposed', key: 'at' }, { title: 'You may decide', key: 'can' },
]
</script>

<template>
  <PageHeader eyebrow="Configuration and Access" title="Configuration & access"
    subtitle="Controlled administration of sources, control parameters, users and approval responsibilities.">
    <template #actions>
      <a-button v-if="canPropose" type="primary" data-test="propose" @click="openPropose('RULE', '', {})">Propose change</a-button>
    </template>
  </PageHeader>

  <StatePlaceholder v-if="settings.placeholder.value" :state="settings.placeholder.value" :message="settings.message.value">
    <template #action><a-button @click="settings.reload">Retry</a-button></template>
  </StatePlaceholder>

  <div v-else-if="settings.data.value" class="cfg">
    <section v-if="pending.data.value?.items.length" class="pend">
      <h3>Pending changes ({{ settings.data.value.pendingChanges }})</h3>
      <div class="cards">
        <div v-for="c in pending.data.value.items" :key="c.id" class="card chg" data-test="pending-card">
          <div class="top"><span class="mono">{{ c.id }}</span><StatusPill :label="label(c.status)" status="warn" /></div>
          <div class="ttl">{{ label(c.targetKind) }} · <span class="mono">{{ c.targetId }}</span></div>
          <ul class="diff"><li v-for="d in c.diff ?? []" :key="d.path"><span class="mono">{{ d.path }}</span>: {{ diffText(d.from) }} &rarr; <b>{{ diffText(d.to) }}</b></li></ul>
          <div class="meta">Proposed by {{ c.proposer.name }} · effective {{ c.effectiveFrom }}<br />{{ c.reason }}</div>
          <div class="btns">
            <a-tooltip :title="c.approval?.canDecide ? '' : `Needs ${permFor(c.targetKind)} and a different approver than the proposer`">
              <span>
                <a-button size="small" type="primary" :disabled="!c.approval?.canDecide" @click="openDecide(c, 'APPROVE')">Approve</a-button>
                <a-button size="small" danger :disabled="!c.approval?.canDecide" style="margin-left: 8px" @click="openDecide(c, 'REJECT')">Reject</a-button>
              </span>
            </a-tooltip>
          </div>
        </div>
      </div>
    </section>

    <a-tabs v-model:activeKey="tab">
      <a-tab-pane key="sources" tab="Sources">
        <a-table class="card" :columns="sourceCols" :data-source="settings.data.value.sources" row-key="sourceName" :pagination="false" size="small">
          <template #emptyText>No sources are configured for this legal entity.</template>
          <template #bodyCell="{ column, record }">
            <StatusPill v-if="column.key === 'mandatory'" :label="record.mandatory ? 'Mandatory' : 'Optional'" :status="record.mandatory ? 'info' : 'neutral'" />
            <template v-else-if="column.key === 'act'">
              <StatusPill v-if="record.pendingChangeId" label="Change pending" status="warn" />
              <a-button v-else-if="canPropose" size="small" type="link" @click="openPropose('SOURCE', record.sourceName, rowCurrent(record))">Propose</a-button>
            </template>
          </template>
        </a-table>
      </a-tab-pane>
      <a-tab-pane key="rules" tab="Control parameters">
        <a-table class="card" :columns="ruleCols" :data-source="settings.data.value.rules" row-key="ruleId" :pagination="false" size="small">
          <template #emptyText>No control parameters are configured for this legal entity.</template>
          <template #bodyCell="{ column, record }">
            <template v-if="column.key === 'control'">{{ label(record.control) }}</template>
            <StatusPill v-else-if="column.key === 'severity'" :label="label(record.severity)" :status="sev(record.severity)" />
            <template v-else-if="column.key === 'act'">
              <StatusPill v-if="record.pendingChangeId" label="Change pending" status="warn" />
              <a-button v-else-if="canPropose" size="small" type="link" @click="openPropose('RULE', record.ruleId, rowCurrent(record))">Propose</a-button>
            </template>
          </template>
        </a-table>
        <a-descriptions title="Policies" bordered size="small" :column="1" class="card" style="margin-top: 16px">
          <a-descriptions-item label="Freshness">{{ settings.data.value.freshnessPolicy || 'Not configured' }}</a-descriptions-item>
           <a-descriptions-item v-if="!settings.data.value.approvalPolicies.length" label="Approval policies">None configured</a-descriptions-item>
          <a-descriptions-item v-for="p in settings.data.value.approvalPolicies" :key="p.name" :label="`Approval: ${p.name}`">
            {{ p.description }}<template v-if="p.requiredRole"> ({{ label(p.requiredRole) }})</template>
          </a-descriptions-item>
        </a-descriptions>
      </a-tab-pane>
      <a-tab-pane key="users" tab="Users & roles">
        <a-table class="card" :columns="userCols" :data-source="settings.data.value.users" row-key="id" :pagination="false" size="small">
          <template #emptyText>No users have access to this legal entity.</template>
          <template #bodyCell="{ column, record }">
            <template v-if="column.key === 'roles'"><StatusPill v-for="r in record.roles" :key="r" :label="label(r)" status="info" style="margin-right: 4px" /></template>
            <template v-else-if="column.key === 'scope'">{{ record.legalEntityIds.map((e: string) => (e === '*' ? 'All entities' : e)).join(', ') || '—' }}</template>
            <template v-else-if="column.key === 'act'">
              <StatusPill v-if="record.pendingChangeId" label="Change pending" status="warn" />
              <a-button v-else-if="can('access.manage')" size="small" type="link" @click="openPropose('USER_ACCESS', record.id, rowCurrent(record))">Propose</a-button>
            </template>
          </template>
        </a-table>
      </a-tab-pane>
      <a-tab-pane key="inbox" :tab="`Approvals inbox${inbox.data.value?.items.length ? ` (${inbox.data.value.items.length})` : ''}`">
        <StatePlaceholder v-if="inbox.placeholder.value" :state="inbox.placeholder.value" :message="inbox.placeholder.value === 'empty' ? 'No approval requests.' : inbox.message.value" />
        <a-table v-else class="card" :columns="inboxCols" :data-source="inbox.data.value?.items" row-key="id" :pagination="false" size="small">
          <template #bodyCell="{ column, record }">
            <template v-if="column.key === 'type'">{{ label(record.objectType) }}</template>
            <template v-else-if="column.key === 'proposer'">{{ record.proposer.name }}</template>
            <template v-else-if="column.key === 'role'">{{ label(record.requiredRole) }}</template>
            <template v-else-if="column.key === 'at'">{{ when(record.proposedAt) }}</template>
            <StatusPill v-else-if="column.key === 'can'" :label="record.canDecide ? 'Yes' : 'No'" :status="record.canDecide ? 'pass' : 'neutral'" />
          </template>
        </a-table>
      </a-tab-pane>
      <a-tab-pane key="history" tab="Change history">
        <StatePlaceholder v-if="history.placeholder.value" :state="history.placeholder.value" :message="history.placeholder.value === 'empty' ? 'No configuration changes recorded.' : history.message.value" />
        <a-table v-else class="card" :columns="historyCols" :data-source="history.data.value?.items" row-key="id" :pagination="false" size="small">
          <template #bodyCell="{ column, record }">
            <template v-if="column.key === 'target'">{{ label(record.targetKind) }} · <span class="mono">{{ record.targetId }}</span></template>
            <StatusPill v-else-if="column.key === 'status'" :label="label(record.status)" :status="statusPill(record.status)" />
            <template v-else-if="column.key === 'proposer'">{{ record.proposer.name }}</template>
            <template v-else-if="column.key === 'at'">{{ when(record.proposedAt) }}</template>
            <template v-else-if="column.key === 'ruleset'">{{ record.rulesetVersion ?? '—' }}</template>
          </template>
        </a-table>
        <div v-if="history.data.value?.page.hasMore" class="more">Showing the latest {{ history.data.value.items.length }} changes.</div>
      </a-tab-pane>
    </a-tabs>
  </div>

  <a-modal v-model:open="propose.open" title="Propose change" ok-text="Submit proposal" :confirm-loading="proposeCmd.pending.value" @ok="submitPropose">
    <a-form layout="vertical">
      <a-form-item label="Target kind" v-bind="errOf('targetKind').length ? { validateStatus: 'error', help: errOf('targetKind').join('; ') } : {}">
        <a-select v-model:value="propose.targetKind" :options="(['RULE', 'SOURCE', 'USER_ACCESS', 'APPROVAL_POLICY'] as const).map((k) => ({ value: k, label: label(k) }))" />
      </a-form-item>
      <a-form-item label="Target id" v-bind="errOf('targetId').length ? { validateStatus: 'error', help: errOf('targetId').join('; ') } : {}">
        <a-input v-model:value="propose.targetId" class="mono" />
      </a-form-item>
      <a-form-item label="Proposed object (full JSON)" v-bind="afterError || errOf('after').length ? { validateStatus: 'error', help: [afterError, ...errOf('after')].filter(Boolean).join('; ') } : {}">
        <a-textarea v-model:value="propose.after" :rows="8" class="mono" data-test="after" />
      </a-form-item>
      <a-form-item label="Effective from" v-bind="errOf('effectiveFrom').length ? { validateStatus: 'error', help: errOf('effectiveFrom').join('; ') } : {}">
        <a-input v-model:value="propose.effectiveFrom" placeholder="YYYY-MM-DD" />
      </a-form-item>
      <a-form-item label="Reason" v-bind="errOf('reason').length ? { validateStatus: 'error', help: errOf('reason').join('; ') } : {}">
        <a-textarea v-model:value="propose.reason" :rows="2" />
      </a-form-item>
      <a-alert v-if="proposeProblem" type="error" show-icon :message="proposeProblem" />
    </a-form>
  </a-modal>

  <a-modal v-model:open="decide.open" :title="`${decide.decision === 'APPROVE' ? 'Approve' : 'Reject'} ${decide.change?.id ?? ''}`"
    :ok-text="decide.decision === 'APPROVE' ? 'Approve change' : 'Reject change'" :ok-button-props="{ disabled: !decide.reason.trim(), danger: decide.decision === 'REJECT' }"
    :confirm-loading="decideCmd.pending.value" @ok="submitDecide">
    <a-textarea v-model:value="decide.reason" :rows="3" placeholder="Reason (required)" data-test="decide-reason" />
    <a-alert v-if="decideErr && isSeparationOfDuties(decideErr)" class="gap" type="error" show-icon message="Separation of duties" description="You proposed this change; a different person must decide it." />
    <a-alert v-else-if="decideErr && stale" class="gap" type="warning" show-icon message="This change was modified" description="Reload to see its latest state before deciding.">
      <template #action><a-button size="small" @click="reloadAfterStale">Reload</a-button></template>
    </a-alert>
    <a-alert v-else-if="decideErr" class="gap" type="error" show-icon :message="(decideErr as Error).message" />
  </a-modal>
</template>

<style scoped>
.card { overflow: hidden; }
.pend h3 { margin: 0 0 8px; font-size: 13px; font-weight: 600; }
.cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(320px, 1fr)); gap: 12px; margin-bottom: 16px; }
.chg { padding: 14px; display: flex; flex-direction: column; gap: 8px; }
.top { display: flex; justify-content: space-between; align-items: center; font-size: 12px; }
.ttl { font-weight: 500; }
.diff { margin: 0; padding-left: 16px; font-size: 12px; color: var(--ink-2); }
.meta { font-size: 12px; color: var(--ink-3); }
.gap { margin-top: 12px; }
.more { margin-top: 8px; color: var(--ink-3); font-size: 12px; }
.cfg :deep(.ant-table-thead > tr > th) { background: var(--surface-sunk); font-size: 10px; font-weight: 600; letter-spacing: 0.06em; text-transform: uppercase; color: var(--ink-3); }
.cfg :deep(.ant-table-cell) { font-size: 12px; }
</style>
