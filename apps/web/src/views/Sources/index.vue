<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { message } from 'ant-design-vue'
import { StatePlaceholder, StatusPill, EvidenceLink } from '../../components'
import type { Status } from '../../components/StatusPill.vue'
import { useRunContext } from '../../composables/useRunContext'
import { useResource, usePaged } from '../../api/composables'
import { formatMoney } from '../../api/client'
import { listSourceDeliveries, getSourceDelivery, getDeliveryProcessingHistory, listDeliveryRecords } from '../../api/services'
import type { Schema } from '../../api/services'
import { FINDINGS_SHOWN, findingsNote } from './findingsNote'
import { useDeliveryCommands } from './deliveryCommands'
import { describeCommandError, type CommandErrorView } from './commandError'

type Delivery = Schema<'SourceDelivery'>
type DeliveryState = Schema<'DeliveryState'>

const { runId, legalEntityId, businessDate } = useRunContext()
const deliveries = useResource(() => runId.value, (id, signal) => listSourceDeliveries(id, { signal }), { isEmpty: (d) => !d.items.length })
const rows = computed(() => deliveries.data.value?.items ?? [])

const selectedId = ref<string | null>(null)
watch(rows, (r) => {
  if (!r.length) { selectedId.value = null; return }
  if (!selectedId.value || !r.some((x) => x.deliveryId === selectedId.value)) selectedId.value = (r.find((x) => x.state !== 'RECEIVED') ?? r[0]!).deliveryId
})
const detail = useResource(selectedId, (id, signal) => getSourceDelivery(id, { signal }))
const history = useResource(selectedId, (id, signal) => getDeliveryProcessingHistory(id, { signal }), { isEmpty: () => false })
const d = computed(() => detail.data.value)

const rejectedOnly = ref(false)
const search = ref('')
const query = ref('')
let timer: ReturnType<typeof setTimeout> | undefined
watch(search, (v) => { clearTimeout(timer); timer = setTimeout(() => (query.value = v.trim()), 300) })
const records = usePaged(
  async (q, signal) => {
    if (!selectedId.value) return { items: [] as Awaited<ReturnType<typeof listDeliveryRecords>>['items'], page: { hasMore: false, nextCursor: null } }
    return listDeliveryRecords(selectedId.value, { ...q, accepted: rejectedOnly.value ? false : undefined, search: query.value || undefined }, { signal })
  },
  [selectedId, rejectedOnly, query], { limit: 10, includeTotal: true },
)

const pill = (s: DeliveryState): Status => ({ RECEIVED: 'pass', QUALIFIED: 'warn', LATE: 'warn', MISSING: 'fail', REJECTED: 'fail', SUPERSEDED: 'neutral' } as const)[s]
const label = (s: string) => s.charAt(0) + s.slice(1).toLowerCase()
const fmt = (t: string | null | undefined) => (t ? t.replace('T', ' ').slice(0, 16) + ' UTC' : '—')
const hm = (t: string | null) => (t ? t.slice(11, 16) : '—')
const money = (m: { amount: string; currency: string }) => formatMoney(m.amount, m.currency)

// Counts only (records and deliveries), never money.
const count = (s: DeliveryState) => rows.value.filter((r) => r.state === s).length
const summary = computed<[string, number][]>(() => [
  ['Expected', rows.value.length], ['Received', rows.value.filter((r) => r.receivedAt).length],
  ['Late', count('LATE')], ['Missing', count('MISSING')], ['Qualified', count('QUALIFIED')], ['Superseded', count('SUPERSEDED')],
])
const rejectedTotal = computed(() => rows.value.reduce((a, r) => a + r.rejectedRecords, 0))
const rejectedSources = computed(() => rows.value.filter((r) => r.rejectedRecords).length)
const mismatch = (r: Delivery) => r.declaredRecordCount !== null && r.declaredRecordCount !== r.actualRecordCount
const note = (r: Delivery) => (mismatch(r) ? 'count mismatch' : r.rejectedRecords ? `${r.rejectedRecords} rejected` : '—')
const noteCls = (r: Delivery) => (mismatch(r) ? 'fail' : r.rejectedRecords ? 'warn' : 'dim')
const findingText = findingsNote

const checkPill = (r: string): Status => (r === 'PASS' ? 'pass' : r === 'FAIL' ? 'fail' : r === 'WARN' ? 'warn' : 'neutral')
const outcomePill = (r: string): Status => (r === 'OK' ? 'pass' : r === 'ERROR' ? 'fail' : 'warn')
const fieldsText = (f: Record<string, string>) => Object.entries(f).map(([k, v]) => `${k}=${v}`).join(' · ')

// Commands (buttons follow the server's allowedActions)
const cmd = useDeliveryCommands()
const dlg = reactive<{ open: null | 'retry' | 'supersede'; reason: string; by: string | undefined; err: CommandErrorView | null }>({ open: null, reason: '', by: undefined, err: null })
const busy = computed(() => cmd.retry.pending.value || cmd.supersede.pending.value)
const can = (a: 'RETRY_INGESTION' | 'MARK_SUPERSEDED') => !!d.value?.allowedActions?.includes(a)
const replacements = computed(() => rows.value.filter((r) => r.deliveryId !== selectedId.value).map((r) => ({ value: r.deliveryId, label: `${r.deliveryId} · ${r.sourceName} · ${r.fileName}` })))
function openDlg(k: 'retry' | 'supersede') { dlg.open = k; dlg.reason = ''; dlg.by = undefined; dlg.err = null; cmd.retry.reset(); cmd.supersede.reset() }
async function refreshAll() { await Promise.all([deliveries.reload(), detail.reload(), history.reload(), records.reload()]) }
async function submit() {
  dlg.err = null
  if (!dlg.reason.trim()) { dlg.err = { kind: 'validation', message: 'A reason is required.', fields: { reason: ['A reason is required.'] } }; return }
  if (dlg.open === 'supersede' && !dlg.by) { dlg.err = { kind: 'validation', message: 'Choose the replacing delivery.', fields: { supersededByDeliveryId: ['Choose the replacing delivery.'] } }; return }
  try {
    const id = selectedId.value!
    if (dlg.open === 'retry') await cmd.retry.run({ deliveryId: id, reason: dlg.reason.trim() })
    else await cmd.supersede.run({ deliveryId: id, supersededByDeliveryId: dlg.by!, reason: dlg.reason.trim() })
    message.success(dlg.open === 'retry' ? 'Ingestion retry accepted.' : 'Delivery marked as superseded.')
    dlg.open = null
    await refreshAll()
  } catch (e) {
    dlg.err = describeCommandError(e)
  }
}
async function reloadAfterConflict() { dlg.err = null; await refreshAll() }
</script>

<template>
  <div class="ph">
    <div>
      <div class="eyebrow">Sources<template v-if="legalEntityId"> · {{ legalEntityId }}</template><template v-if="businessDate"> · {{ businessDate }}</template></div>
      <h1>Sources</h1>
      <p>Whether every required input arrived, passed validation and can be relied on for the selected business date.</p>
    </div>
    <div class="acts">
      <a-button v-if="can('RETRY_INGESTION')" :disabled="busy" @click="openDlg('retry')">Retry ingestion step</a-button>
      <a-button v-if="can('MARK_SUPERSEDED')" :disabled="busy" @click="openDlg('supersede')">Mark as superseded</a-button>
    </div>
  </div>
  <StatePlaceholder v-if="deliveries.placeholder.value" :state="deliveries.placeholder.value" :message="deliveries.placeholder.value === 'empty' ? 'No deliveries expected for this run.' : deliveries.message.value">
    <template #action><a-button v-if="deliveries.placeholder.value === 'error'" @click="deliveries.reload()">Retry</a-button></template>
  </StatePlaceholder>
  <template v-else>
    <div class="card strip">
      <div v-for="[k, v] in summary" :key="k" class="cell">
        <div class="lbl">{{ k }}</div>
        <div class="n" :class="{ warnc: k === 'Qualified' && v, failc: k === 'Missing' && v }">{{ v }}</div>
      </div>
      <div class="cell">
        <div class="lbl">Records rejected</div>
        <div class="n" :class="{ failc: rejectedTotal }">{{ rejectedTotal }}</div>
        <div class="sub">across {{ rejectedSources }} sources</div>
      </div>
    </div>

    <div class="card">
      <div class="ch"><span class="lbl">Deliveries</span><span class="aside">Times in UTC</span></div>
      <div class="tr th">
        <span class="c-src">Source</span><span>Expected</span><span>Received</span><span>Schema</span><span>Declared / got</span><span class="c-note">Notes</span><span class="c-pill">Validation</span>
      </div>
      <div v-for="r in rows" :key="r.deliveryId" class="tr row" :class="{ sel: selectedId === r.deliveryId }" @click="selectedId = r.deliveryId">
        <span class="c-src name" :class="{ link: r.state === 'QUALIFIED' }">{{ r.sourceName }} <small class="dim">{{ r.fileName }}</small></span>
        <span>{{ hm(r.expectedBy) }}</span><span>{{ hm(r.receivedAt) }}</span><span>{{ r.schemaVersion }}</span>
        <span :class="{ failc: mismatch(r) }">{{ r.declaredRecordCount ?? '—' }} / {{ r.actualRecordCount }}</span>
        <span class="c-note" :class="noteCls(r)">{{ note(r) }}</span>
        <span class="c-pill"><StatusPill :status="pill(r.state)" :label="label(r.state)" /></span>
      </div>
    </div>

    <StatePlaceholder v-if="detail.placeholder.value" :state="detail.placeholder.value" :message="detail.message.value">
      <template #action><a-button @click="detail.reload()">Retry</a-button></template>
    </StatePlaceholder>
    <div v-else-if="d" class="card">
      <div class="ch">
        <span class="lbl">Delivery detail</span>
        <span class="did">{{ d.deliveryId }}</span>
        <StatusPill :status="pill(d.state)" :label="label(d.state)" />
      </div>
      <div class="cols">
        <div class="col">
          <div class="lbl">Metadata</div>
          <div class="kv"><span>File</span><b>{{ d.fileName }}</b></div>
          <div class="kv"><span>Business date</span><b>{{ d.businessDate }}</b></div>
          <div class="kv"><span>Received</span><b>{{ fmt(d.receivedAt) }}</b></div>
          <div class="kv"><span>Schema</span><b>{{ d.schemaVersion }}</b></div>
          <div class="kv"><span>Fingerprint</span><b class="mono" :title="d.fingerprint.value">{{ d.fingerprint.algorithm }} {{ d.fingerprint.value.slice(0, 12) }}…</b></div>
          <div class="kv"><span>Correction of</span><b>{{ d.correctionOfDeliveryId ?? '—' }}</b></div>
          <div class="kv"><span>Superseded by</span><b>{{ d.supersededByDeliveryId ?? '—' }}</b></div>
          <div class="kv"><span>Sequence</span><b>{{ d.sequenceNumber ?? '—' }}</b></div>
        </div>
        <div class="col">
          <div class="lbl">Validation</div>
          <div class="kv"><span>Declared records</span><b>{{ d.declaredRecordCount ?? '—' }}</b></div>
          <div class="kv"><span>Actual records</span><b>{{ d.actualRecordCount }}</b></div>
          <div class="kv"><span>Accepted / rejected</span><b>{{ d.acceptedRecords }} / {{ d.rejectedRecords }}</b></div>
          <div class="kv"><span>Declared totals</span>
            <b><div v-for="m in d.declaredTotals" :key="m.currency">{{ money(m) }}</div><template v-if="!d.declaredTotals.length">—</template></b></div>
          <div class="kv"><span>Actual totals</span>
            <b><div v-for="m in d.actualTotals ?? []" :key="m.currency">{{ money(m) }}</div><template v-if="!d.actualTotals?.length">—</template></b></div>
          <div v-for="c in d.checks" :key="c.check" class="kv">
            <span>{{ label(c.check.replace('_', ' ')) }}</span>
            <b><StatusPill :status="checkPill(c.result)" :label="label(c.result.replace('_', ' '))" /> <small class="dim">{{ c.detail }}</small></b>
          </div>
          <div v-for="q in d.qualifications" :key="q" class="kv warn"><span>Qualification</span><b>{{ q }}</b></div>
          <div class="kv"><span>Findings</span>
            <b><router-link v-for="f in d.findingIds.slice(0, FINDINGS_SHOWN)" :key="f" :to="`/exceptions/${f}`" class="fl">{{ f }}</router-link><template v-if="!d.findingIds.length">none</template>
              <small v-if="findingText(d)" class="dim"> {{ findingText(d) }}</small></b></div>
        </div>
        <div class="col">
          <div class="lbl">Processing history</div>
          <StatePlaceholder v-if="history.placeholder.value" :state="history.placeholder.value" :message="history.message.value" />
          <div v-for="h in history.data.value?.items ?? []" :key="h.id" class="kv">
            <span>{{ fmt(h.at).slice(5, 16) }}</span>
            <b><StatusPill :status="outcomePill(h.outcome)" :label="label(h.stage.replace('_', ' '))" /> <small class="dim">{{ h.message }}</small></b>
          </div>
          <div v-if="!history.placeholder.value && !history.data.value?.items.length" class="sub">No history</div>
        </div>
      </div>
      <div class="recs">
        <div class="rh">
          <span class="lbl">Records <template v-if="records.total.value !== undefined">({{ records.total.value }})</template> · accepted first, then rejected</span>
          <span class="rf"><a-input-search v-model:value="search" placeholder="Search row id or value" allow-clear style="width: 240px" /><a-checkbox v-model:checked="rejectedOnly">Rejected only</a-checkbox></span>
        </div>
        <StatePlaceholder v-if="records.placeholder.value" :state="records.placeholder.value" :message="records.placeholder.value === 'empty' ? 'No records.' : undefined">
          <template #action><a-button v-if="records.placeholder.value === 'error'" @click="records.reload()">Retry</a-button></template>
        </StatePlaceholder>
        <div v-for="rec in records.items.value" v-else :key="rec.recordId" class="rec">
          <span class="mono">#{{ rec.rowNumber ?? '—' }}</span>
          <EvidenceLink :label="rec.sourceRowId" :to="`/evidence?ref=${encodeURIComponent(rec.recordId)}`" />
          <StatusPill :status="rec.accepted ? 'pass' : 'fail'" :label="rec.accepted ? 'Accepted' : 'Rejected'" />
          <span class="fv">{{ fieldsText(rec.fields) }}<template v-if="rec.rejectionReasons?.length"> <b class="fail">{{ rec.rejectionReasons.join('; ') }}</b></template></span>
          <span><router-link v-for="f in rec.findingIds" :key="f" :to="`/exceptions/${f}`" class="fl">{{ f }}</router-link></span>
        </div>
        <div v-if="records.hasPrev.value || records.hasNext.value" class="pg">
          <a-button size="small" :disabled="!records.hasPrev.value || records.loading.value" @click="records.prev()">Previous</a-button>
          <a-button size="small" :disabled="!records.hasNext.value || records.loading.value" @click="records.next()">Next</a-button>
        </div>
      </div>
    </div>
  </template>

  <a-modal :open="dlg.open !== null" :title="dlg.open === 'retry' ? 'Retry ingestion' : 'Mark delivery as superseded'" :ok-text="dlg.open === 'retry' ? 'Retry ingestion' : 'Mark superseded'"
    :confirm-loading="busy" :mask-closable="false" @ok="submit" @cancel="dlg.open = null">
    <a-alert v-if="dlg.err" :type="dlg.err.kind === 'stale' ? 'warning' : 'error'" show-icon :message="dlg.err.message" style="margin-bottom: 12px">
      <template v-if="dlg.err.kind === 'stale'" #action><a-button size="small" @click="reloadAfterConflict">Reload</a-button></template>
    </a-alert>
    <p v-if="d" class="sub">{{ d.deliveryId }} · {{ d.sourceName }} · {{ d.fileName }}. This is recorded in the audit trail.</p>
    <a-form layout="vertical">
      <a-form-item v-if="dlg.open === 'supersede'" label="Replacing delivery" :validate-status="dlg.err?.fields.supersededByDeliveryId ? 'error' : undefined" :help="dlg.err?.fields.supersededByDeliveryId?.join(' ')">
        <a-select v-model:value="dlg.by" :options="replacements" placeholder="Select the corrected delivery" />
      </a-form-item>
      <a-form-item label="Reason (required)" :validate-status="dlg.err?.fields.reason ? 'error' : undefined" :help="dlg.err?.fields.reason?.join(' ')">
        <a-textarea v-model:value="dlg.reason" :rows="3" />
      </a-form-item>
    </a-form>
  </a-modal>
</template>

<style scoped>
.ph { display: flex; justify-content: space-between; align-items: flex-start; gap: 24px; margin-bottom: 16px; }
.eyebrow, .lbl { font-size: 10px; font-weight: 600; letter-spacing: 0.06em; text-transform: uppercase; color: var(--ink-3); }
h1 { margin: 6px 0 6px; font-size: 28px; font-weight: 600; line-height: 1.2; }
p { margin: 0; font-size: 13px; color: var(--ink-2); }
.acts { display: flex; gap: 8px; }
.card { margin-bottom: 16px; overflow: hidden; }
.strip { display: flex; }
.cell { flex: 1; padding: 14px 20px; display: flex; flex-direction: column; gap: 4px; border-right: 1px solid var(--border); }
.cell:last-child { border-right: 0; }
.n { font-size: 26px; font-weight: 500; line-height: 1.3; }
.sub { font-size: 11px; color: var(--ink-3); }
.warnc { color: var(--warn); } .failc { color: var(--fail); }
.ch { display: flex; align-items: center; gap: 10px; padding: 14px 20px; }
.aside { margin-left: auto; font-size: 12px; color: var(--ink-3); }
.did { font-size: 12px; font-weight: 600; color: var(--accent); }
.pipe { display: flex; align-items: center; gap: 8px; padding: 14px 20px; background: var(--surface-sunk); }
.step { flex: 1; background: var(--surface); border: 1px solid var(--border); border-radius: 5px; padding: 9px 12px; display: flex; flex-direction: column; gap: 3px; font-size: 12px; }
.step span { font-size: 11px; color: var(--ink-3); }
.arr { color: var(--ink-3); }
.tr { display: grid; grid-template-columns: 2.4fr 0.7fr 0.7fr 0.6fr 1fr 1.2fr 1fr; gap: 12px; padding: 11px 20px; align-items: center; font-size: 11px; color: var(--ink-2); }
.th { background: var(--surface-sunk); padding: 8px 20px; font-size: 10px; font-weight: 600; letter-spacing: 0.06em; text-transform: uppercase; color: var(--ink-3); }
.row { border-top: 1px solid var(--border); cursor: pointer; }
.row.sel, .row:hover { background: var(--accent-soft); }
.name { font-size: 12px; font-weight: 500; color: var(--ink); }
.name.link { color: var(--accent); }
.warn { color: var(--warn); } .fail { color: var(--fail); } .dim { color: var(--ink-3); }
.cols { display: grid; grid-template-columns: repeat(3, 1fr); border-top: 1px solid var(--border); }
.col { padding: 14px 20px; display: flex; flex-direction: column; gap: 8px; border-right: 1px solid var(--border); }
.col:last-child { border-right: 0; }
.kv { display: flex; gap: 12px; font-size: 12px; align-items: flex-start; }
.kv b { min-width: 0; overflow-wrap: anywhere; }
.kv span { width: 120px; flex: none; color: var(--ink-3); }
.kv b { font-weight: 500; color: var(--ink); }
.kv.warn b { color: var(--warn); }
.fl { margin-right: 8px; }
.rh { display: flex; justify-content: space-between; align-items: center; }
.mono { font-family: var(--font-mono); font-size: 12px; }
small.dim { font-size: 11px; font-weight: 400; }
.recs { border-top: 1px solid var(--border); padding: 14px 20px; display: flex; flex-direction: column; gap: 8px; }
.rf { display: flex; gap: 12px; align-items: center; }
.rec { display: grid; grid-template-columns: 50px 160px 90px 1fr 120px; gap: 12px; align-items: center; font-size: 12px; padding: 4px 0; border-top: 1px solid var(--border); }
.fv { color: var(--ink-2); overflow-wrap: anywhere; font-size: 11px; }
.pg { display: flex; gap: 8px; justify-content: flex-end; }
</style>
