<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { StatePlaceholder, StatusPill, MoneyAmount } from '../../components'
import { listSourceDeliveries, getSourceDelivery, listSourceRecords } from '../../api'
import type { SourceDelivery, SourceRecord, DeliveryState } from '../../api'

const rows = ref<SourceDelivery[]>([])
const state = ref<'loading' | 'ready' | 'error' | 'forbidden'>('loading')
const errMsg = ref('')
const selected = ref<SourceDelivery | null>(null)
const records = ref<SourceRecord[]>([])
const recTotal = ref(0)
const rejectedOnly = ref(false)
const detailLoading = ref(false)

const pill = (s: DeliveryState) =>
  ({ RECEIVED: 'pass', QUALIFIED: 'warn', LATE: 'warn', MISSING: 'fail', REJECTED: 'fail', SUPERSEDED: 'neutral' } as const)[s]
const label = (s: DeliveryState) => s.charAt(0) + s.slice(1).toLowerCase()
const fmt = (d: string | null) => (d ? d.replace('T', ' ').slice(0, 16) : '—')

const summary = computed(() => {
  const c = (s: DeliveryState) => rows.value.filter((r) => r.state === s).length
  return [
    ['Expected', rows.value.length], ['Received', rows.value.filter((r) => r.receivedAt).length],
    ['Late', c('LATE')], ['Missing', c('MISSING')], ['Qualified', c('QUALIFIED')], ['Superseded', c('SUPERSEDED')],
  ] as [string, number][]
})

const hm = (d: string | null) => (d ? d.slice(11, 16) : '—')
const rejectedTotal = computed(() => rows.value.reduce((a, r) => a + r.rejectedRecords, 0))
const rejectedSources = computed(() => rows.value.filter((r) => r.rejectedRecords).length)
const received = computed(() => rows.value.filter((r) => r.receivedAt).length)
const accepted = computed(() => rows.value.filter((r) => r.state === 'RECEIVED').length)
const qualified = computed(() => rows.value.filter((r) => r.state === 'QUALIFIED').length)
const acceptedRecs = computed(() => rows.value.reduce((a, r) => a + r.acceptedRecords, 0))
const note = (r: SourceDelivery) =>
  r.declaredRecordCount !== r.actualRecordCount ? 'count mismatch' : r.rejectedRecords ? `${r.rejectedRecords} rejected` : '—'
const noteCls = (r: SourceDelivery) => (r.declaredRecordCount !== r.actualRecordCount ? 'fail' : r.rejectedRecords ? 'warn' : 'dim')
const pipeline = computed(() => [
  ['Landing folder', `${rows.value.length} files seen`],
  ['Source adapter', `${received.value} picked up`],
  ['Evidence store', `${received.value} fingerprinted, immutable`],
  ['Validation', `${accepted.value} accepted · ${qualified.value} qualified`],
  ['Canonical records', `${acceptedRecs.value} accepted · ${rejectedTotal.value} rejected`],
])

async function load() {
  state.value = 'loading'
  try {
    rows.value = await listSourceDeliveries()
    if (rows.value.length) open((rows.value.find((r) => r.state !== 'RECEIVED') ?? rows.value[0]).deliveryId)
    state.value = 'ready'
  } catch (e: any) {
    errMsg.value = e?.message ?? ''
    state.value = e?.status === 403 ? 'forbidden' : 'error'
  }
}
async function loadRecords() {
  if (!selected.value) return
  const p = await listSourceRecords(selected.value.sourceName, { pageSize: 20, rejectedOnly: rejectedOnly.value })
  records.value = p.items
  recTotal.value = p.total
}
async function open(id: string) {
  detailLoading.value = true
  try {
    selected.value = await getSourceDelivery(id)
    await loadRecords()
  } catch (e: any) {
    errMsg.value = e?.message ?? ''
    selected.value = null
  } finally {
    detailLoading.value = false
  }
}
onMounted(load)
</script>

<template>
  <div class="ph">
    <div>
      <div class="eyebrow">Sources</div>
      <h1>Sources</h1>
      <p>Whether every required input arrived, passed validation and can be relied on for the selected business date.</p>
    </div>
    <div class="acts">
      <a-button disabled>Export source manifest</a-button>
      <a-button @click="load">Retry ingestion step</a-button>
    </div>
  </div>
  <StatePlaceholder v-if="state === 'loading'" state="loading" />
  <StatePlaceholder v-else-if="state === 'forbidden'" state="forbidden" />
  <StatePlaceholder v-else-if="state === 'error'" state="error" :message="errMsg || undefined">
    <template #action><a-button @click="load">Retry</a-button></template>
  </StatePlaceholder>
  <StatePlaceholder v-else-if="!rows.length" state="empty" message="No deliveries expected." />
  <template v-else>
    <div class="card strip">
      <div v-for="[k, v] in summary" :key="k" class="cell">
        <div class="lbl">{{ k }}</div>
        <div class="n" :class="{ warnc: k === 'Qualified' && v }">{{ v }}</div>
      </div>
      <div class="cell">
        <div class="lbl">Records rejected</div>
        <div class="n" :class="{ failc: rejectedTotal }">{{ rejectedTotal }}</div>
        <div class="sub">across {{ rejectedSources }} sources</div>
      </div>
    </div>

    <div class="card">
      <div class="ch"><span class="lbl">Ingestion channel</span><span class="aside">Source adapters read the files. The browser never does.</span></div>
      <div class="pipe">
        <template v-for="(p, i) in pipeline" :key="p[0]">
          <span v-if="i" class="arr">→</span>
          <div class="step"><b>{{ p[0] }}</b><span>{{ p[1] }}</span></div>
        </template>
      </div>
    </div>

    <div class="card">
      <div class="ch"><span class="lbl">Deliveries</span><span class="aside">Times in GMT</span></div>
      <div class="tr th">
        <span class="c-src">Source</span><span>Expected</span><span>Received</span><span>Schema</span><span>Declared / got</span><span class="c-note">Notes</span><span class="c-pill">Validation</span>
      </div>
      <div v-for="r in rows" :key="r.deliveryId" class="tr row" :class="{ sel: selected?.deliveryId === r.deliveryId }" @click="open(r.deliveryId)">
        <span class="c-src name" :class="{ link: r.state === 'QUALIFIED' }">{{ r.sourceName }}</span>
        <span>{{ hm(r.expectedBy) }}</span><span>{{ hm(r.receivedAt) }}</span><span>{{ r.schemaVersion }}</span>
        <span :class="{ failc: r.declaredRecordCount !== r.actualRecordCount }">{{ r.declaredRecordCount }} / {{ r.actualRecordCount }}</span>
        <span class="c-note" :class="noteCls(r)">{{ note(r) }}</span>
        <span class="c-pill"><StatusPill :status="pill(r.state)" :label="label(r.state)" /></span>
      </div>
    </div>

    <div v-if="selected" class="card">
      <div class="ch">
        <span class="lbl">Delivery detail</span>
        <span class="did">{{ selected.deliveryId }}</span>
        <StatusPill :status="pill(selected.state)" :label="label(selected.state)" />
      </div>
      <a-spin :spinning="detailLoading">
        <div class="cols">
          <div class="col">
            <div class="lbl">Metadata</div>
            <div class="kv"><span>File</span><b>{{ selected.fileName }}</b></div>
            <div class="kv"><span>Business date</span><b>{{ selected.businessDate }}</b></div>
            <div class="kv"><span>Received</span><b>{{ fmt(selected.receivedAt) }}</b></div>
            <div class="kv"><span>Schema</span><b>{{ selected.schemaVersion }}</b></div>
            <div class="kv"><span>Correction of</span><b>{{ selected.correctionOfDeliveryId ?? '—' }}</b></div>
            <div class="kv"><span>Declared totals</span>
              <b><template v-for="m in selected.declaredTotals" :key="m.currency"><MoneyAmount :amount="m.amount" :currency="m.currency" show-currency />&nbsp; </template><template v-if="!selected.declaredTotals.length">—</template></b></div>
          </div>
          <div class="col">
            <div class="lbl">Validation</div>
            <div class="kv"><span>Declared records</span><b>{{ selected.declaredRecordCount }}</b></div>
            <div class="kv"><span>Actual records</span><b>{{ selected.actualRecordCount }}</b></div>
            <div class="kv"><span>Accepted / rejected</span><b>{{ selected.acceptedRecords }} / {{ selected.rejectedRecords }}</b></div>
            <div v-for="q in selected.qualifications" :key="q" class="kv warn"><span>Qualification</span><b>{{ q }}</b></div>
            <div class="kv"><span>Findings</span>
              <b><router-link v-for="f in selected.findingIds" :key="f" :to="`/exceptions/${f}`" class="fl">{{ f }}</router-link><template v-if="!selected.findingIds.length">none</template></b></div>
          </div>
          <div class="col">
            <div class="rh"><span class="lbl">Records ({{ recTotal }})</span>
              <a-checkbox v-model:checked="rejectedOnly" @change="loadRecords">Rejected only</a-checkbox></div>
            <div v-for="rec in records.slice(0, 8)" :key="rec.sourceRowId" class="kv">
              <span>#{{ rec.rowNumber }} {{ rec.sourceRowId }}</span>
              <b><StatusPill :status="rec.accepted ? 'pass' : 'fail'" :label="rec.accepted ? 'Accepted' : 'Rejected'" /></b>
            </div>
            <div v-if="!records.length" class="sub">No records</div>
          </div>
        </div>
      </a-spin>
    </div>
  </template>
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
.kv { display: flex; gap: 12px; font-size: 12px; align-items: center; }
.kv span { width: 120px; flex: none; color: var(--ink-3); }
.kv b { font-weight: 500; color: var(--ink); }
.kv.warn b { color: var(--warn); }
.fl { margin-right: 8px; }
.rh { display: flex; justify-content: space-between; }
</style>
