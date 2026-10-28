<script setup lang="ts">
import { computed, onScopeDispose, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { message } from 'ant-design-vue'
import { PageHeader, StatePlaceholder, StatusPill, EvidenceLink } from '../../components'
import { useRunContext } from '../../composables/useRunContext'
import { useResource, usePaged, useCommand } from '../../api/composables'
import { isApiError } from '../../api/client'
import { generateEvidencePack, getEvidencePack, downloadEvidencePack, listAuditEvents, getLineage, getSourceRecord, getDeliveryProcessingHistory, listSourceDeliveries } from '../../api/services'
import type { Schema } from '../../api/services'
import { pollEvidencePack, humanize, fmtBytes, verificationChecklist } from './logic'

type Ev = Schema<'AuditEvent'>
const route = useRoute()
const { runId, run, me } = useRunContext()

const search = ref(String(route.query.transactionId ?? route.query.ref ?? ''))
const q = ref(search.value)
const kind = ref<Schema<'AuditSubjectKind'> | undefined>()
const actorType = ref<Schema<'ActorType'> | undefined>()
const thisRun = ref(true)
let t: ReturnType<typeof setTimeout>
watch(search, (v) => { clearTimeout(t); t = setTimeout(() => (q.value = v.trim()), 300) })

const events = usePaged((p, signal) => listAuditEvents({ limit: p.limit, cursor: p.cursor, search: q.value || undefined, subjectKind: kind.value, actorType: actorType.value, runId: thisRun.value ? (runId.value ?? undefined) : undefined }, { signal }),
  [q, kind, actorType, thisRun, runId], { limit: 25 })
const selected = ref<Ev | null>(null)
watch(() => events.items.value, (i) => { if (!i.find((e) => e.id === selected.value?.id)) selected.value = i[0] ?? null })

const deliveries = useResource(() => runId.value, (id, signal) => listSourceDeliveries(id, { signal }))
const pack = useResource(() => runId.value, (id, signal) => getEvidencePack(id, { signal }))
const packView = computed(() => pack.data.value)
const checklist = computed(() => verificationChecklist(packView.value))

// lineage of selected subject
const lineageKey = computed(() => {
  const e = selected.value
  const k = e && ({ EXCEPTION: 'EXCEPTION', RECORD: 'RECORD' } as Record<string, 'EXCEPTION' | 'RECORD'>)[e.subjectKind]
  return k ? `${k}|${e!.subjectId}` : null
})
const lineage = useResource(() => lineageKey.value, (k, signal) => { const [kd, id] = k.split('|') as ['EXCEPTION' | 'RECORD', string]; return getLineage(kd, id, { runId: runId.value ?? undefined }, { signal }) })
const history = useResource(() => (selected.value?.subjectKind === 'DELIVERY' ? selected.value.subjectId : null), (id, signal) => getDeliveryProcessingHistory(id, { signal }))
const recordId = ref<string | null>(null)
const record = useResource(() => recordId.value, (id, signal) => getSourceRecord(id, { signal }))
const nodeLabel = (id: string) => lineage.data.value?.nodes.find((n) => n.id === id)?.label ?? id

// commands
const polling = ref(false)
const ctl = new AbortController(); onScopeDispose(() => ctl.abort())
const gen = useCommand((_: null, o) => generateEvidencePack(runId.value!, o))
async function generate() {
  try {
    const p = await gen.run(null)
    pack.data.value = p
    polling.value = true
    const last = await pollEvidencePack(runId.value!, { signal: ctl.signal, onUpdate: (x) => (pack.data.value = x) })
    if (last.status === 'FAILED') message.error(last.failureReason ?? 'Evidence pack generation failed')
    else if (last.status === 'GENERATED') message.success('Evidence pack generated')
    await events.reload()
  } catch (e) { message.error(isApiError(e) ? `${e.title}${e.detail ? ': ' + e.detail : ''}` : String(e)) }
  finally { polling.value = false }
}
const downloading = ref(false)
async function download() {
  downloading.value = true
  try {
    const { blob, filename } = await downloadEvidencePack(runId.value!)
    const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = filename ?? `evidence-pack-${runId.value}.zip`; a.click(); URL.revokeObjectURL(a.href)
  } catch (e) { message.error(isApiError(e) ? e.title : String(e)) } finally { downloading.value = false }
}
const canExport = computed(() => !!me.value?.permissions.includes('evidence.export'))
const fmt = (d: string | null | undefined) => (d ? d.replace('T', ' ').slice(0, 19) : '—')
const chip = (cur: unknown, v: unknown) => (cur === v ? 'primary' : 'default')
const kinds: Schema<'AuditSubjectKind'>[] = ['RUN', 'DELIVERY', 'RECORD', 'EXCEPTION', 'MATCH_GROUP', 'SIGN_OFF', 'EVIDENCE_PACK']
function subjectLink(e: Ev) {
  if (e.subjectKind === 'EXCEPTION') return `/exceptions/${e.subjectId}`
  if (e.subjectKind === 'DELIVERY') return { path: '/sources', query: { ...route.query, delivery: e.subjectId } }
  if (e.subjectKind === 'SIGN_OFF' || e.subjectKind === 'RUN') return { path: '/sign-off', query: route.query }
}
</script>

<template>
  <PageHeader title="Evidence and audit explorer" eyebrow="Evidence · Audit" subtitle="Reproduce a historical conclusion from source delivery to approval.">
    <template #actions>
      <a-button v-if="packView?.status === 'GENERATED' && canExport" :loading="downloading" @click="download">Download evidence pack</a-button>
      <a-button v-if="canExport" type="primary" class="dark" :loading="polling || gen.pending.value" :disabled="!runId" @click="generate">{{ packView?.status === 'GENERATED' ? 'Regenerate evidence pack' : 'Generate evidence pack' }}</a-button>
    </template>
  </PageHeader>

  <div class="searchrow">
    <a-input-search v-model:value="search" placeholder="Search summary, subject or request id" allow-clear class="sb" />
    <a-checkbox v-model:checked="thisRun">This run only</a-checkbox>
  </div>
  <div class="chips">
    <a-button size="small" :type="chip(kind, undefined)" @click="kind = undefined">All subjects</a-button>
    <a-button v-for="k in kinds" :key="k" size="small" :type="chip(kind, k)" @click="kind = k">{{ humanize(k) }}</a-button>
    <span class="sep" />
    <a-button size="small" :type="chip(actorType, undefined)" @click="actorType = undefined">Any actor</a-button>
    <a-button size="small" :type="chip(actorType, 'USER')" @click="actorType = 'USER'">Human</a-button>
    <a-button size="small" :type="chip(actorType, 'SYSTEM')" @click="actorType = 'SYSTEM'">System</a-button>
  </div>

  <div class="card strip">
    <div class="cell"><div class="lbl">Run</div><div class="v mono">{{ run?.id ?? '—' }}</div><div class="sub">v{{ run?.version ?? '—' }} · {{ run?.businessDate ?? '—' }}</div></div>
    <div class="cell"><div class="lbl">Ruleset</div><div class="v">{{ run?.rulesetVersion ?? '—' }}</div></div>
    <div class="cell"><div class="lbl">Outcome</div><div class="v">{{ run ? humanize(run.state) : '—' }}</div><div class="sub">sign-off {{ run ? humanize(run.signOffState) : '—' }}</div></div>
    <div class="cell"><div class="lbl">Started</div><div class="v">{{ fmt(run?.startedAt) }}</div></div>
    <div class="cell"><div class="lbl">Source deliveries</div>
      <div class="v">{{ deliveries.data.value?.items.length ?? '—' }}</div>
      <div class="sub"><router-link :to="{ path: '/sources', query: route.query }">Open sources</router-link></div></div>
    <div class="cell"><div class="lbl">Evidence pack</div><div class="v">{{ packView ? humanize(packView.status) : '—' }}</div><div class="sub">{{ fmtBytes(packView?.sizeBytes) }}</div></div>
  </div>

  <div class="cols">
    <div class="left">
      <div class="card">
        <div class="ch"><span class="lbl">Activity history</span><span class="sub">append-only, hash-chained</span></div>
        <StatePlaceholder v-if="events.placeholder.value" :state="events.placeholder.value" :message="events.placeholder.value === 'empty' ? 'No audit events match.' : events.placeholder.value === 'forbidden' ? 'Access restricted: your role cannot view the audit trail.' : undefined">
          <template #action><a-button @click="events.reload">Retry</a-button></template>
        </StatePlaceholder>
        <template v-else>
          <a-table size="small" row-key="id" :pagination="false" :data-source="events.items.value" :loading="events.loading.value"
            :custom-row="(r: Ev) => ({ onClick: () => (selected = r), class: r.id === selected?.id ? 'selrow' : '' })"
            :columns="[{ title: 'Time', key: 'at', width: 150 }, { title: 'Actor', key: 'actor', width: 150 }, { title: 'Event', key: 'type', width: 190 }, { title: 'Subject', key: 'subject', width: 130 }, { title: 'Summary', dataIndex: 'summary' }]">
            <template #bodyCell="{ column, record }">
              <template v-if="column.key === 'at'">{{ fmt(record.at) }}</template>
              <template v-else-if="column.key === 'actor'"><StatusPill :status="record.actor.type === 'USER' ? 'info' : 'neutral'" :label="record.actor.type === 'USER' ? 'Human' : 'System'" /> {{ record.actor.name ?? record.actor.id ?? '' }}</template>
              <template v-else-if="column.key === 'type'">{{ humanize(record.type) }}</template>
              <template v-else-if="column.key === 'subject'"><span class="mono">{{ record.subjectId }}</span></template>
            </template>
          </a-table>
          <div class="pager"><span class="sub">Page {{ events.pageIndex.value + 1 }}</span><a-button size="small" :disabled="!events.hasPrev.value" @click="events.prev">Previous</a-button><a-button size="small" :disabled="!events.hasNext.value" @click="events.next">Next</a-button></div>
        </template>
      </div>

      <div class="card">
        <div class="ch"><span class="lbl">Lineage</span><span class="sub">{{ selected ? `${humanize(selected.subjectKind)} ${selected.subjectId}` : '' }}</span></div>
        <div class="pad">
          <template v-if="!selected"><span class="sub">Select an event.</span></template>
          <template v-else-if="selected.subjectKind === 'DELIVERY'">
            <StatePlaceholder v-if="history.placeholder.value" :state="history.placeholder.value" message="No processing history." />
            <ol v-else class="steps"><li v-for="s in history.data.value?.items" :key="s.id"><b>{{ humanize(s.stage) }}</b> · {{ s.outcome }} · {{ fmt(s.at) }} <span class="sub">{{ s.message }}</span></li></ol>
          </template>
          <template v-else-if="!lineageKey"><span class="sub">No lineage graph for {{ humanize(selected.subjectKind) }} subjects.</span></template>
          <StatePlaceholder v-else-if="lineage.placeholder.value" :state="lineage.placeholder.value" :message="lineage.placeholder.value === 'empty' ? 'No lineage.' : lineage.message.value"><template #action><a-button @click="lineage.reload">Retry</a-button></template></StatePlaceholder>
          <template v-else-if="lineage.data.value">
            <a-table size="small" row-key="id" :pagination="false" :data-source="lineage.data.value.nodes"
              :columns="[{ title: 'Kind', key: 'k' }, { title: 'Record', key: 'l' }, { title: 'Source row', key: 's' }, { title: 'Amount', key: 'a', align: 'right' }]">
              <template #bodyCell="{ column, record }">
                <template v-if="column.key === 'k'">{{ humanize(record.kind) }}</template>
                <template v-else-if="column.key === 'l'">{{ record.label }}</template>
                <template v-else-if="column.key === 's'"><a v-if="record.recordId" class="mono" @click="recordId = record.recordId">{{ record.source }} · {{ record.sourceRowId ?? '—' }}</a><span v-else class="mono">{{ record.source }} · {{ record.sourceRowId ?? '—' }}</span></template>
                <template v-else>{{ record.amount ? `${record.amount.amount} ${record.amount.currency}` : '—' }}</template>
              </template>
            </a-table>
            <ul class="edges"><li v-for="(e, i) in lineage.data.value.edges" :key="i">{{ nodeLabel(e.from) }} <b>{{ e.relation }}</b> {{ nodeLabel(e.to) }}</li></ul>
            <div v-if="lineage.data.value.deliveries?.length" class="sub">Deliveries:
              <router-link v-for="d in lineage.data.value.deliveries" :key="d.deliveryId" :to="{ path: '/sources', query: { ...route.query, delivery: d.deliveryId } }" class="mono dl">{{ d.deliveryId }}<template v-if="d.rowNumber"> row {{ d.rowNumber }}</template></router-link></div>
          </template>
        </div>
      </div>
    </div>

    <div class="right">
      <div class="card">
        <div class="ch"><span class="lbl">Event detail</span></div>
        <div v-if="selected" class="pad kvs">
          <div class="kv"><span>Event</span><b>{{ humanize(selected.type) }}</b></div>
          <div class="kv"><span>Time</span><b>{{ fmt(selected.at) }}</b></div>
          <div class="kv"><span>Actor</span><b>{{ selected.actor.name ?? selected.actor.id ?? 'System' }} ({{ selected.actor.type }})</b></div>
          <div class="kv"><span>Subject</span><b><EvidenceLink v-if="subjectLink(selected)" :label="selected.subjectId" :to="subjectLink(selected)" /><span v-else>{{ selected.subjectId }}</span> ({{ humanize(selected.subjectKind) }})</b></div>
          <div class="kv"><span>Run</span><b>{{ selected.runId ?? '—' }}</b></div>
          <div class="kv"><span>Rule version</span><b>{{ selected.ruleVersion ?? '—' }}</b></div>
          <div class="kv"><span>Reason</span><b>{{ selected.reason ?? '—' }}</b></div>
          <div class="kv"><span>Request</span><b class="mono">{{ selected.requestId ?? '—' }}</b></div>
          <div class="kv"><span>Hash</span><b class="mono brk">{{ selected.hash }}</b></div>
          <div class="kv"><span>Previous</span><b class="mono brk">{{ selected.previousHash ?? '—' }}</b></div>
          <div class="kv"><span>Summary</span><b>{{ selected.summary }}</b></div>
        </div>
        <div v-else class="pad sub">No event selected.</div>
      </div>

      <div class="card">
        <div class="ch"><span class="lbl">Evidence pack and verification</span></div>
        <StatePlaceholder v-if="pack.placeholder.value && pack.placeholder.value !== 'empty'" :state="pack.placeholder.value" :message="pack.message.value"><template #action><a-button @click="pack.reload">Retry</a-button></template></StatePlaceholder>
        <div v-else class="pad kvs">
          <div class="kv"><span>Pack</span><b>{{ packView?.id ?? 'Not generated' }}</b></div>
          <div class="kv"><span>Generated</span><b>{{ fmt(packView?.generatedAt) }}</b></div>
          <div class="kv"><span>Size</span><b>{{ fmtBytes(packView?.sizeBytes) }}</b></div>
          <a-alert v-if="packView?.status === 'FAILED'" type="error" show-icon :message="packView.failureReason ?? 'Generation failed'" />
          <div v-for="c in checklist" :key="c.label" class="check"><StatusPill :status="c.state === 'pass' ? 'pass' : c.state === 'fail' ? 'fail' : 'neutral'" :label="c.state === 'pass' ? 'OK' : c.state === 'fail' ? 'Failed' : 'n/a'" /> <b>{{ c.label }}</b><div class="sub brk">{{ c.note }}</div></div>
          <div v-if="packView?.contents.length" class="sub">Contents: {{ packView.contents.join(', ') }}</div>
        </div>
      </div>
    </div>
  </div>

  <a-drawer :open="!!recordId" title="Source record" width="460" @close="recordId = null">
    <StatePlaceholder v-if="record.placeholder.value" :state="record.placeholder.value" :message="record.message.value" />
    <a-descriptions v-else-if="record.data.value" :column="1" size="small" bordered>
      <a-descriptions-item label="Record">{{ record.data.value.recordId }}</a-descriptions-item>
      <a-descriptions-item label="Delivery"><router-link :to="{ path: '/sources', query: { ...route.query, delivery: record.data.value.deliveryId } }">{{ record.data.value.deliveryId }}</router-link> row {{ record.data.value.rowNumber ?? '—' }}</a-descriptions-item>
      <a-descriptions-item label="Accepted">{{ record.data.value.accepted ? 'Yes' : 'No' }}</a-descriptions-item>
      <a-descriptions-item v-for="(v, k) in record.data.value.fields" :key="k" :label="String(k)">{{ v }}</a-descriptions-item>
    </a-descriptions>
  </a-drawer>
</template>

<style scoped>
.dark { background: var(--ink); }
.searchrow { display: flex; gap: 16px; align-items: center; margin-bottom: 8px; }
.sb { max-width: 480px; }
.chips { display: flex; gap: 6px; flex-wrap: wrap; margin-bottom: 12px; align-items: center; }
.sep { width: 1px; height: 18px; background: var(--border); margin: 0 6px; }
.lbl { font: 600 10px var(--font-mono); letter-spacing: 0.4px; text-transform: uppercase; color: var(--ink-3); }
.sub { font-size: 11px; color: var(--ink-3); }
.mono { font-family: var(--font-mono); font-size: 12px; }
.strip { display: flex; margin-bottom: 16px; }
.cell { flex: 1; padding: 12px 16px; border-right: 1px solid var(--border); display: flex; flex-direction: column; gap: 3px; min-width: 0; }
.cell:last-child { border-right: 0; }
.v { font-size: 15px; font-weight: 500; }
.cols { display: grid; grid-template-columns: minmax(0, 1fr) 360px; gap: 16px; align-items: start; }
.left, .right { display: flex; flex-direction: column; gap: 16px; }
.ch { display: flex; gap: 10px; align-items: center; padding: 14px 16px; }
.pad { padding: 0 16px 14px; }
.kvs { display: flex; flex-direction: column; gap: 8px; }
.kv { display: flex; gap: 10px; font-size: 12px; }
.kv span { width: 84px; flex: none; color: var(--ink-3); }
.kv b { font-weight: 500; min-width: 0; }
.brk { word-break: break-all; }
.check { font-size: 12px; }
.pager { display: flex; gap: 8px; justify-content: flex-end; align-items: center; padding: 10px 16px; }
.edges { margin: 8px 0 0; padding-left: 18px; font-size: 12px; }
.steps { margin: 0; padding-left: 18px; font-size: 12px; }
.dl { margin-left: 8px; }
:deep(.ant-table-thead > tr > th) { background: var(--surface-sunk); font: 600 10px var(--font-mono); color: var(--ink-3); text-transform: uppercase; }
:deep(tr.selrow > td) { background: var(--accent-soft) !important; }
:deep(.ant-table-tbody > tr) { cursor: pointer; }
a { cursor: pointer; }
</style>
