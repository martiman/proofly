<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { generateEvidencePack, getEvidencePack, listAuditEvents, listRuns, listSourceDeliveries } from '../../api'
import type { AuditEvent, EvidencePack, Run, SourceDelivery } from '../../api'
import { PageHeader, StatePlaceholder, StatusPill, EvidenceLink } from '../../components'

const loading = ref(true)
const error = ref<string | null>(null)
const forbidden = ref(false)
const run = ref<Run | null>(null)
const deliveries = ref<SourceDelivery[]>([])
const events = ref<AuditEvent[]>([])
const total = ref(0)
const pack = ref<EvidencePack | null>(null)
const generating = ref(false)
const route = useRoute()
const search = ref(String(route.query.transactionId ?? route.query.ref ?? ''))
const kind = ref<AuditEvent['subjectKind'] | undefined>()
const actorKind = ref<'all' | 'human' | 'system'>('all')

const isSystem = (a: string) => a === 'system' || a.startsWith('system') || a.startsWith('proofly')
const human = (s: string) => s.replace(/_/g, ' ').toLowerCase().replace(/^\w/, (c) => c.toUpperCase())
const fmt = (d: string | null) => (d ? d.replace('T', ' ').slice(0, 19) : '—')

async function loadEvents() {
  const r = await listAuditEvents({ search: search.value || undefined, subjectKind: kind.value, pageSize: 200 })
  events.value = r.items
  total.value = r.total
}
async function load() {
  loading.value = true
  error.value = null
  try {
    const runs = await listRuns()
    run.value = runs[runs.length - 1] ?? null
    ;[deliveries.value, pack.value] = await Promise.all([listSourceDeliveries(run.value?.id), getEvidencePack(run.value?.id), loadEvents()]).then((r) => [r[0], r[1]])
  } catch (e: any) {
    if (e?.status === 403) forbidden.value = true
    else error.value = e?.message ?? 'Failed to load evidence'
  } finally {
    loading.value = false
  }
}
onMounted(load)
let t: ReturnType<typeof setTimeout>
watch([search, kind], () => { clearTimeout(t); t = setTimeout(() => loadEvents().catch((e) => (error.value = e?.message)), 250) })

async function generate() {
  generating.value = true
  try {
    pack.value = await generateEvidencePack(run.value?.id)
    await loadEvents()
  } catch (e: any) {
    error.value = e?.message ?? 'Failed to generate pack'
  } finally {
    generating.value = false
  }
}

const shown = computed(() => events.value.filter((e) => actorKind.value === 'all' || (actorKind.value === 'system') === isSystem(e.actor)))
const kindOptions = (['RUN', 'DELIVERY', 'RECORD', 'EXCEPTION', 'EVIDENCE_PACK'] as const).map((v) => ({ value: v, label: human(v) }))
const totalRecords = computed(() => deliveries.value.reduce((n, d) => n + d.actualRecordCount, 0))
const delStatus = (s: string) => (s === 'RECEIVED' ? 'pass' : s === 'MISSING' || s === 'REJECTED' ? 'fail' : s === 'SUPERSEDED' ? 'neutral' : 'warn') as 'pass' | 'fail' | 'neutral' | 'warn'

function subjectLink(e: AuditEvent) {
  if (e.subjectKind === 'EXCEPTION') return `/exceptions/${e.subjectId}`
  if (e.subjectKind === 'DELIVERY' || e.subjectKind === 'RECORD') return '/sources'
  return undefined
}

const delCols = [
  { title: 'Source', dataIndex: 'sourceName', key: 'sourceName' },
  { title: 'File / delivery', key: 'file' },
  { title: 'Schema', dataIndex: 'schemaVersion', key: 'schemaVersion' },
  { title: 'Declared', dataIndex: 'declaredRecordCount', key: 'd', align: 'right' as const },
  { title: 'Actual', dataIndex: 'actualRecordCount', key: 'a', align: 'right' as const },
  { title: 'Received', key: 'received' },
  { title: 'State', key: 'state' },
]
const evCols = [
  { title: 'Time', key: 'at', width: 170 },
  { title: 'Actor', key: 'actor', width: 170 },
  { title: 'Event', key: 'type', width: 190 },
  { title: 'Subject', key: 'subject', width: 180 },
  { title: 'Summary', dataIndex: 'summary', key: 'summary' },
  { title: 'Fingerprint', key: 'hash', width: 120 },
]
</script>

<template>
  <PageHeader title="Evidence and Audit Explorer" subtitle="Reproduce a historical conclusion from source delivery to approval.">
    <template #actions>
      <a-button type="primary" :loading="generating" :disabled="loading || !run" @click="generate">
        {{ pack?.status === 'GENERATED' ? 'Regenerate evidence pack' : 'Generate evidence pack' }}
      </a-button>
    </template>
  </PageHeader>

  <StatePlaceholder v-if="loading" state="loading" />
  <StatePlaceholder v-else-if="forbidden" state="forbidden" message="Access restricted: your role cannot view evidence for this scope." />
  <StatePlaceholder v-else-if="error && !run" state="error" :message="error"><template #action><a-button @click="load">Retry</a-button></template></StatePlaceholder>
  <StatePlaceholder v-else-if="!run" state="empty" message="No runs available for the selected scope." />
  <template v-else>
    <a-alert v-if="error" type="error" show-icon :message="error" closable style="margin-bottom: 12px" />

    <div class="card sec">
      <h2>Run manifest</h2>
      <a-descriptions :column="4" size="small" bordered>
        <a-descriptions-item label="Run">{{ run.id }} (v{{ run.version }})</a-descriptions-item>
        <a-descriptions-item label="Business date">{{ run.businessDate }}</a-descriptions-item>
        <a-descriptions-item label="Ruleset version">{{ run.rulesetVersion }}</a-descriptions-item>
        <a-descriptions-item label="Outcome"><StatusPill :status="run.state === 'FAILED' ? 'fail' : 'info'" :label="human(run.state)" /></a-descriptions-item>
        <a-descriptions-item label="Started">{{ fmt(run.startedAt) }}</a-descriptions-item>
        <a-descriptions-item label="Last evidence arrival">{{ fmt(run.lastEvidenceArrivalAt) }}</a-descriptions-item>
        <a-descriptions-item label="Source deliveries">{{ deliveries.length }}</a-descriptions-item>
        <a-descriptions-item label="Records (actual)">{{ totalRecords }}</a-descriptions-item>
      </a-descriptions>
      <a-table class="tbl" size="small" :columns="delCols" :data-source="deliveries" row-key="deliveryId" :pagination="false">
        <template #bodyCell="{ column, record }">
          <template v-if="column.key === 'file'"><EvidenceLink :label="record.fileName" to="/sources" /> <span class="muted">{{ record.deliveryId }}</span></template>
          <template v-else-if="column.key === 'received'">{{ fmt(record.receivedAt) }}</template>
          <template v-else-if="column.key === 'state'"><StatusPill :status="delStatus(record.state)" :label="human(record.state)" /></template>
        </template>
      </a-table>
    </div>

    <div class="card sec">
      <h2>Evidence pack</h2>
      <div v-if="pack?.status === 'GENERATED'">
        <StatusPill status="pass" label="Generated" /> <span class="muted">{{ pack.id }} · {{ fmt(pack.generatedAt) }}</span>
        <ul><li v-for="c in pack.contents" :key="c">{{ c }}</li></ul>
      </div>
      <a-empty v-else description="No evidence pack generated for this run yet." />
    </div>

    <div class="card sec">
      <h2>Activity history <span class="muted">({{ total }} events)</span></h2>
      <div class="filters">
        <a-input-search v-model:value="search" placeholder="Search summary or subject" allow-clear style="width: 280px" />
        <a-select v-model:value="kind" :options="kindOptions" placeholder="Subject" allow-clear style="width: 160px" />
        <a-radio-group v-model:value="actorKind" size="small">
          <a-radio-button value="all">All</a-radio-button>
          <a-radio-button value="human">Human</a-radio-button>
          <a-radio-button value="system">System</a-radio-button>
        </a-radio-group>
      </div>
      <a-table size="small" :columns="evCols" :data-source="shown" row-key="id" :pagination="{ pageSize: 15 }" :locale="{ emptyText: 'No audit events match.' }">
        <template #bodyCell="{ column, record }">
          <template v-if="column.key === 'at'">{{ fmt(record.at) }}</template>
          <template v-else-if="column.key === 'actor'"><StatusPill :status="isSystem(record.actor) ? 'neutral' : 'info'" :label="isSystem(record.actor) ? 'System' : 'Human'" /> {{ record.actor }}</template>
          <template v-else-if="column.key === 'type'">{{ human(record.type) }}</template>
          <template v-else-if="column.key === 'subject'">
            <EvidenceLink v-if="subjectLink(record)" :label="record.subjectId" :to="subjectLink(record)" />
            <span v-else>{{ record.subjectId }}</span>
          </template>
          <template v-else-if="column.key === 'hash'"><code :title="record.hash">{{ record.hash.slice(0, 12) }}…</code></template>
        </template>
      </a-table>
    </div>
  </template>
</template>

<style scoped>
.sec { padding: 16px; margin-bottom: 16px; }
h2 { font: 600 10px var(--font-mono); letter-spacing: 0.4px; text-transform: uppercase; color: var(--ink-3); margin: 0 0 12px; }
:deep(.ant-table-thead > tr > th) { background: var(--surface-sunk); font: 600 10px var(--font-mono); color: var(--ink-3); text-transform: uppercase; }
.muted { color: var(--ink-2); font-size: 12px; font-weight: 400; }
.tbl { margin-top: 12px; }
.filters { display: flex; gap: 8px; margin-bottom: 12px; align-items: center; }
code { font-family: var(--font-mono); font-size: 12px; }
</style>
