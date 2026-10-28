<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { message, Modal } from 'ant-design-vue'
import { PageHeader, StatePlaceholder, StatusPill } from '../../components'
import type { Status } from '../../components/StatusPill.vue'
import { useRunContext } from '../../composables/useRunContext'
import { useAsync, useCommand, usePaged, useResource } from '../../api/composables'
import { formatMoney } from '../../api/client'
import {
  bulkAssignExceptions, createExceptionView, deleteExceptionView, getExceptionsSummary, listAssignableUsers,
  listExceptionViews, listExceptions, type Schema,
} from '../../api/services'
import {
  CONTROLS, SEVERITIES, STATUSES, describeError, emptyFilters, fromViewFilters, 
  sortFromTable, sortOrderFor, splitBulk, toListQuery, toSummaryQuery, toViewFilters, validateFilters,
  type BulkResultItem, type QueueFilters,
} from './logic'

type Item = Schema<'ExceptionSummary'>
const router = useRouter()
const { runId, run, me, currencies, legalEntityId } = useRunContext()

const f = reactive<QueueFilters>(emptyFilters())
const activeViewId = ref<string>()
const filterErrors = computed(() => validateFilters(f))
const listQuery = computed(() => JSON.stringify(toListQuery(f)))
const summaryQuery = computed(() => JSON.stringify(toSummaryQuery(f)))

const sevStatus: Record<string, Status> = { CRITICAL: 'fail', HIGH: 'warn', MEDIUM: 'info', LOW: 'neutral' }
const label = (s: string) => s.toLowerCase().replace(/_/g, ' ').replace(/^\w/, (c) => c.toUpperCase())
const money = (m: { amount: string; currency: string } | null | undefined) => (m ? formatMoney(m.amount, m.currency) : '—')
const when = (iso: string) => new Date(iso).toLocaleString('en-GB', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' })
const opts = (xs: readonly string[]) => xs.map((value) => ({ value, label: label(value) }))

// ---- list (cursor paging, server sort/filter) ----
const PAGE = 25
const list = usePaged<Item>(
  (p, signal) => listExceptions(runId.value!, { ...JSON.parse(listQuery.value), limit: p.limit, cursor: p.cursor, includeTotal: p.includeTotal }, { signal }),
  [runId, listQuery], { limit: PAGE, includeTotal: true },
)
// ---- summary: every total shown comes from here ----
const summary = useResource(
  () => (runId.value ? `${runId.value}|${summaryQuery.value}` : null),
  (_k, signal) => getExceptionsSummary(runId.value!, JSON.parse(summaryQuery.value), { signal }),
  { isEmpty: () => false },
)
const sevCards = computed(() => {
  const s = summary.data.value
  if (!s) return []
  return SEVERITIES.map((sev) => ({
    sev, count: s.bySeverity?.[sev] ?? 0,
    values: (s.bySeverityCurrency ?? []).filter((x) => x.severity === sev),
  }))
})
const controlRows = computed(() => CONTROLS.map((c) => ({ c, n: summary.data.value?.byControl?.[c] ?? 0 })).filter((x) => x.n > 0))
const maxControl = computed(() => Math.max(1, ...controlRows.value.map((x) => x.n)))
const reasons = computed(() => (summary.data.value?.byReason ?? []).slice(0, 6))

// ---- owners for filter and bulk picker ----
const assignable = useResource(() => legalEntityId.value ?? null, (le, signal) => listAssignableUsers(le, { signal }).catch(() => null), { isEmpty: () => false })
const ownerOptions = computed(() => {
  const users = assignable.data.value
  const base = users ? users.map((u) => ({ value: u.id, label: u.name })) : []
  if (me.value && !base.some((o) => o.value === me.value!.id)) base.unshift({ value: me.value.id, label: `${me.value.name} (me)` })
  return base
})
const canAssign = computed(() => !!me.value?.permissions.includes('exception.assign'))

// ---- saved views ----
const views = useAsync((signal) => listExceptionViews({ signal }), { isEmpty: () => false })
const viewOptions = computed(() => (views.data.value?.items ?? []).map((v) => ({ value: v.id, label: v.name + (v.shared ? ' (shared)' : '') })))
function applyView(id?: string) {
  activeViewId.value = id
  const v = views.data.value?.items.find((x) => x.id === id)
  if (!v) return
  Object.assign(f, fromViewFilters(v.filters, v.sort))
}
const saveOpen = ref(false)
const viewName = ref('')
const viewShared = ref(false)
const saveCmd = useCommand((b: { name: string; shared: boolean }, o) => createExceptionView({ name: b.name, shared: b.shared, filters: toViewFilters(f), sort: f.sort }, o))
const saveError = ref('')
async function saveView() {
  saveError.value = ''
  try {
    const v = await saveCmd.run({ name: viewName.value.trim(), shared: viewShared.value })
    message.success('View saved'); saveOpen.value = false; viewName.value = ''
    await views.reload(); activeViewId.value = v.id
  } catch (e) { saveError.value = describeError(e) }
}
function removeView() {
  const v = views.data.value?.items.find((x) => x.id === activeViewId.value)
  if (!v) return
  Modal.confirm({
    title: `Delete view "${v.name}"?`, content: 'Saved views are personal preferences; the exception population is not changed.',
    okText: 'Delete', okButtonProps: { danger: true },
    onOk: async () => {
      try { await deleteExceptionView(v.id, { ifMatch: v.etag! }); activeViewId.value = undefined; await views.reload(); message.success('View deleted') }
      catch (e) { message.error(describeError(e)) }
    },
  })
}

// ---- filters ----
function resetFilters() { Object.assign(f, emptyFilters()); activeViewId.value = undefined }
const filtersActive = computed(() => JSON.stringify({ ...f, sort: '' }) !== JSON.stringify({ ...emptyFilters(), sort: '' }))
const sortedBy = (field: string) => sortOrderFor(f.sort, field)
function onTable(_p: unknown, _flt: unknown, s: { field?: string; order?: 'ascend' | 'descend' | null }) { f.sort = sortFromTable(s.field, s.order) }

// ---- selection and bulk assign ----
const selected = ref<string[]>([])
watch([runId, listQuery], () => { selected.value = [] })
const bulkOwner = ref<string>()
const bulkNote = ref('')
const bulkOutcome = ref<{ ok: BulkResultItem[]; failed: BulkResultItem[]; owner: string } | null>(null)
const bulkError = ref('')
const bulk = useCommand((b: { ownerId: string; note: string; items: { exceptionId: string; etag: string }[] }, o) =>
  bulkAssignExceptions(runId.value!, { ownerId: b.ownerId, note: b.note || undefined, items: b.items }, o))
const ownerName = (id: string) => ownerOptions.value.find((o) => o.value === id)?.label ?? id
function confirmBulk() {
  if (!bulkOwner.value) return
  Modal.confirm({
    title: `Assign ${selected.value.length} exception(s) to ${ownerName(bulkOwner.value)}?`,
    content: 'Each exception is checked separately; failures are listed per item.',
    okText: 'Assign', onOk: doBulk,
  })
}
async function doBulk() {
  const byId = new Map(list.items.value.map((i) => [i.id, i]))
  const items = selected.value.map((id) => ({ exceptionId: id, etag: byId.get(id)!.etag }))
  bulkError.value = ''
  try {
    const r = await bulk.run({ ownerId: bulkOwner.value!, note: bulkNote.value.trim(), items })
    const out = splitBulk(r.results)
    bulkOutcome.value = { ...out, owner: ownerName(bulkOwner.value!) }
    selected.value = out.failed.map((x) => x.exceptionId)
    if (!out.failed.length) { bulkNote.value = ''; message.success(`${out.ok.length} assigned`) }
    await Promise.all([list.reload(), summary.reload()])
    selected.value = out.failed.map((x) => x.exceptionId).filter((id) => list.items.value.some((i) => i.id === id))
  } catch (e) { bulkError.value = describeError(e) }
}
const problemText = (r: BulkResultItem) => r.problem ? (r.problem.detail ? `${r.problem.title}: ${r.problem.detail}` : r.problem.title) : `HTTP ${r.status}`

const columns = computed(() => [
  { title: 'Severity', key: 'sev', width: 120, sorter: true, sortOrder: sortedBy('severity') },
  { title: 'Exception', key: 'ex' },
  { title: 'Value', key: 'value', align: 'right' as const, width: 150, sorter: true, sortOrder: sortedBy('value') },
  { title: 'Owner / due', key: 'due', width: 150, sorter: true, sortOrder: sortedBy('dueAt'), dataIndex: 'dueAt' },
  { title: 'Status', key: 'status', width: 110 },
  { title: 'Age', key: 'age', width: 64, align: 'right' as const, sorter: true, sortOrder: sortedBy('ageDays'), dataIndex: 'ageDays' },
  { title: 'Source', key: 'src', width: 170 },
])
const open = (id: string) => router.push({ path: `/exceptions/${id}`, query: runId.value ? { run: runId.value } : {} })
const tableChange = (_p: unknown, _f: unknown, s: unknown) => onTable(_p, _f, s as { field?: string; order?: 'ascend' | 'descend' | null })
</script>

<template>
  <PageHeader eyebrow="Exceptions" title="Exceptions queue"
    :subtitle="run ? `Run ${run.id} · ${run.businessDate}` : undefined" />

  <!-- summary (server aggregates only) -->
  <StatePlaceholder v-if="summary.state.value === 'forbidden'" state="forbidden" />
  <a-alert v-else-if="summary.state.value === 'error'" type="error" show-icon :message="summary.message.value" class="mb">
    <template #action><a-button size="small" @click="summary.reload()">Retry</a-button></template>
  </a-alert>
  <template v-else-if="summary.data.value">
    <div class="cards">
      <div class="card kpi"><div class="k">Exceptions</div><div class="n">{{ summary.data.value.total }}</div>
        <div class="sub">
          <span v-for="c in summary.data.value.byCurrency" :key="c.currency" class="ccyline">{{ c.count }} · {{ formatMoney(c.absoluteValue.amount, c.absoluteValue.currency) }} absolute</span>
          <span v-if="summary.data.value.withoutValue">{{ summary.data.value.withoutValue }} without value</span>
        </div></div>
      <div v-for="c in sevCards" :key="c.sev" class="card kpi">
        <div class="k">{{ label(c.sev) }}</div><div class="n" :class="{ fail: c.sev === 'CRITICAL' && c.count }">{{ c.count }}</div>
        <div class="sub"><span v-for="v in c.values" :key="v.currency" class="ccyline">{{ formatMoney(v.absoluteValue.amount, v.absoluteValue.currency) }} <i>({{ v.count }})</i></span><span v-if="!c.values.length">—</span></div>
      </div>
      <div class="card kpi"><div class="k">Blocking</div><div class="n" :class="{ fail: summary.data.value.blocking }">{{ summary.data.value.blocking ?? 0 }}</div></div>
      <div class="card kpi"><div class="k">Unassigned</div><div class="n">{{ summary.data.value.unassigned ?? 0 }}</div></div>
      <div class="card kpi"><div class="k">Overdue</div><div class="n" :class="{ fail: summary.data.value.overdue }">{{ summary.data.value.overdue ?? 0 }}</div></div>
    </div>
    <div class="two">
      <section class="card pad">
        <h3>By control area</h3>
        <div v-for="x in controlRows" :key="x.c" class="bar"><span>{{ label(x.c) }}</span>
          <span class="track"><i :style="{ width: (x.n / maxControl) * 100 + '%' }" /></span><b>{{ x.n }}</b></div>
        <div v-if="!controlRows.length" class="hint">No exceptions.</div>
      </section>
      <section class="card pad">
        <h3>Repeated reasons <span class="hint">top {{ reasons.length }} of {{ summary.data.value.byReason?.length ?? 0 }}</span></h3>
        <div v-for="r in reasons" :key="r.reasonCode" class="bar"><span class="mono">{{ r.reasonCode }}</span><span /><b>{{ r.count }}</b></div>
        <div v-if="!reasons.length" class="hint">No exceptions.</div>
      </section>
    </div>
  </template>
  <div v-else class="card pad mb"><a-spin /></div>

  <!-- filter and saved views bar -->
  <section class="card pad filters">
    <div class="frow">
      <a-select v-model:value="f.severity" mode="multiple" allow-clear placeholder="Severity" :options="opts(SEVERITIES)" :max-tag-count="1" class="w140" />
      <a-select v-model:value="f.status" mode="multiple" allow-clear placeholder="Status" :options="opts(STATUSES)" :max-tag-count="1" class="w160" />
      <a-select v-model:value="f.control" mode="multiple" allow-clear placeholder="Control" :options="opts(CONTROLS)" :max-tag-count="1" class="w160" />
      <a-select v-model:value="f.currency" allow-clear placeholder="Currency" :options="currencies.map((c) => ({ value: c, label: c }))" class="w100" />
      <a-select v-model:value="f.ownerId" allow-clear placeholder="Owner" :options="ownerOptions" :disabled="f.unassigned" class="w150" />
      <a-checkbox v-model:checked="f.unassigned">Unassigned</a-checkbox>
      <a-checkbox v-model:checked="f.blocking">Blocking</a-checkbox>
    </div>
    <div class="frow">
      <a-input v-model:value="f.valueMin" placeholder="Value min" class="w110" :status="filterErrors.valueMin ? 'error' : undefined" />
      <a-input v-model:value="f.valueMax" placeholder="Value max" class="w110" :status="filterErrors.valueMax ? 'error' : undefined" />
      <a-input-number v-model:value="f.ageDaysMin" :min="0" placeholder="Age ≥ days" class="w120" />
      <a-input-number v-model:value="f.ageDaysMax" :min="0" placeholder="Age ≤ days" class="w120" />
      <a-input-search v-model:value="f.search" placeholder="Search id, title, reason…" allow-clear class="w240" />
      <a-button v-if="filtersActive" type="link" @click="resetFilters">Clear filters</a-button>
      <span class="gap" />
      <a-select :value="activeViewId" allow-clear placeholder="Saved views" :options="viewOptions" class="w180" @change="applyView" />
      <a-button v-if="activeViewId" danger @click="removeView">Delete view</a-button>
      <a-button :disabled="!!Object.keys(filterErrors).length" @click="saveOpen = true">Save view</a-button>
    </div>
    <div v-for="(m, k) in filterErrors" :key="k" class="ferr">{{ m }}</div>
    <div v-if="views.error.value" class="ferr">Saved views unavailable: {{ describeError(views.error.value) }}</div>
  </section>

  <!-- bulk assign -->
  <section v-if="canAssign && selected.length" class="card pad bulk">
    <b>{{ selected.length }} selected</b>
    <a-select v-model:value="bulkOwner" placeholder="Assign to…" :options="ownerOptions" class="w200" />
    <a-input v-model:value="bulkNote" placeholder="Note (optional)" class="w240" maxlength="2000" />
    <a-button type="primary" :disabled="!bulkOwner" :loading="bulk.pending.value" @click="confirmBulk">Assign selected</a-button>
    <a-button type="link" @click="selected = []">Clear selection</a-button>
    <a-alert v-if="bulkError" type="error" show-icon :message="bulkError" class="full" />
  </section>
  <a-alert v-if="bulkOutcome" :type="bulkOutcome.failed.length ? 'warning' : 'success'" show-icon closable class="mb" @close="bulkOutcome = null">
    <template #message>{{ bulkOutcome.ok.length }} of {{ bulkOutcome.ok.length + bulkOutcome.failed.length }} assigned to {{ bulkOutcome.owner }}</template>
    <template v-if="bulkOutcome.failed.length" #description>
      <div>Not assigned (reload the row and retry):</div>
      <ul class="fails"><li v-for="r in bulkOutcome.failed" :key="r.exceptionId"><span class="mono">{{ r.exceptionId }}</span> · {{ r.status }} · {{ problemText(r) }}</li></ul>
    </template>
  </a-alert>

  <!-- table -->
  <StatePlaceholder v-if="list.placeholder.value && list.state.value !== 'empty'" :state="list.placeholder.value" :message="list.error.value ? describeError(list.error.value) : undefined">
    <template #action><a-button @click="list.reload()">Retry</a-button></template>
  </StatePlaceholder>
  <StatePlaceholder v-else-if="list.state.value === 'empty'" state="empty" :message="filtersActive ? 'No exceptions match these filters.' : 'No exceptions in this run.'">
    <template #action><a-button v-if="filtersActive" @click="resetFilters">Clear filters</a-button></template>
  </StatePlaceholder>
  <section v-else class="card">
    <a-table :columns="columns" :data-source="list.items.value" row-key="id" size="small" :pagination="false" :loading="list.loading.value"
      :row-selection="canAssign ? { selectedRowKeys: selected, onChange: (k: (string | number)[]) => (selected = k as string[]) } : undefined"
      :custom-row="(r: Item) => ({ onClick: (ev: MouseEvent) => { if (!(ev.target as HTMLElement).closest('a,.ant-checkbox-wrapper')) open(r.id) }, style: 'cursor:pointer' })"
      @change="tableChange">
      <template #bodyCell="{ column, record }">
        <template v-if="column.key === 'sev'">
          <div class="pills"><StatusPill :status="sevStatus[record.severity]" :label="label(record.severity)" /><StatusPill v-if="record.blocking" status="fail" label="Blocking" /></div>
        </template>
        <template v-else-if="column.key === 'ex'">
          <router-link :to="{ path: `/exceptions/${record.id}`, query: runId ? { run: runId } : {} }" class="mono id">{{ record.id }}</router-link>
          <div class="title">{{ record.title }}</div>
          <div class="hint"><span class="mono">{{ record.reasonCode }}</span> · {{ label(record.control) }}</div>
        </template>
        <template v-else-if="column.key === 'value'"><span :class="['tab', { neg: record.value?.amount.startsWith('-') }]">{{ money(record.value) }}</span></template>
        <template v-else-if="column.key === 'status'"><StatusPill status="neutral" :label="label(record.status)" /></template>
        <template v-else-if="column.key === 'due'"><div :class="record.owner ? 'own' : 'own warn'">{{ record.owner?.name ?? 'Unassigned' }}</div><div class="hint">Due {{ when(record.dueAt) }}</div></template>
        <template v-else-if="column.key === 'age'">{{ record.ageDays ?? '—' }}d</template>
        <template v-else-if="column.key === 'src'"><span class="mono small src">{{ record.sourceFile ?? '—' }}<template v-if="record.sourceRowId"> · {{ record.sourceRowId }}</template></span></template>
      </template>
    </a-table>
    <div class="pager">
      <span class="hint">{{ list.items.value.length }} shown<template v-if="list.total.value != null"> of {{ list.total.value }}</template> · page {{ list.pageIndex.value + 1 }}</span>
      <a-button :disabled="!list.hasPrev.value || list.loading.value" @click="list.prev()">Previous</a-button>
      <a-button :disabled="!list.hasNext.value || list.loading.value" @click="list.next()">Next</a-button>
    </div>
  </section>

  <a-modal v-model:open="saveOpen" title="Save current filters as a view" ok-text="Save" :confirm-loading="saveCmd.pending.value" :ok-button-props="{ disabled: !viewName.trim() }" @ok="saveView">
    <a-input v-model:value="viewName" placeholder="View name" maxlength="80" />
    <a-checkbox v-model:checked="viewShared" class="mt">Share with my team</a-checkbox>
    <a-alert v-if="saveError" type="error" show-icon :message="saveError" class="mt" />
  </a-modal>
</template>

<style scoped>
.mb { margin-bottom: 16px; }
.mt { margin-top: 12px; }
.pad { padding: 16px; }
.cards { display: grid; grid-template-columns: 1.4fr repeat(4, 1fr) repeat(3, .8fr); gap: 12px; margin-bottom: 16px; }
.kpi { padding: 12px 14px; }
.k { font-size: 10px; font-weight: 600; text-transform: uppercase; letter-spacing: .08em; color: var(--ink-3); }
.n { font-size: 26px; font-weight: 600; margin: 2px 0; }
.n.fail { color: var(--fail); }
.sub { font-size: 11px; color: var(--ink-3); display: flex; flex-direction: column; gap: 1px; }
.sub i { font-style: normal; }
.two { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; margin-bottom: 16px; }
h3 { font-size: 10px; font-weight: 600; text-transform: uppercase; letter-spacing: .08em; color: var(--ink-3); margin: 0 0 10px; }
.bar { display: grid; grid-template-columns: 170px 1fr 36px; gap: 10px; align-items: center; font-size: 12px; margin: 4px 0; }
.bar b { text-align: right; }
.track { background: var(--surface-sunk); height: 8px; border-radius: 4px; overflow: hidden; }
.track i { display: block; height: 100%; background: var(--accent); }
.filters { margin-bottom: 16px; display: flex; flex-direction: column; gap: 10px; }
.frow { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
.gap { flex: 1; }
.ferr { color: var(--fail); font-size: 12px; }
.w100 { width: 100px } .w110 { width: 110px } .w120 { width: 120px } .w140 { width: 140px } .w150 { width: 150px }
.w160 { width: 160px } .w180 { width: 180px } .w200 { width: 200px } .w240 { width: 240px }
.bulk { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin-bottom: 12px; }
.full { width: 100%; }
.fails { margin: 4px 0 0; padding-left: 18px; }
.pills { display: flex; flex-direction: column; gap: 4px; align-items: flex-start; }
.id { font-weight: 600; font-size: 12px; color: var(--accent); }
.title { font-size: 13px; margin: 2px 0; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; }
.hint { color: var(--ink-3); font-size: 12px; }
.small { font-size: 11px; }
.src { overflow-wrap: anywhere; }
.own { font-size: 12px; font-weight: 500; } .own.warn { color: var(--warn); }
.tab { white-space: nowrap; font-variant-numeric: tabular-nums; }
.neg { color: var(--fail); }
.pager { display: flex; justify-content: flex-end; align-items: center; gap: 8px; padding: 12px 16px; }
.pager .hint { margin-right: auto; }
</style>
