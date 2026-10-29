<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { StatePlaceholder, StatusPill, MoneyAmount } from '../../components'
import type { Status } from '../../components/StatusPill.vue'
import { listExceptions, sumMoney } from '../../api'
import type { ExceptionSummary, ExceptionStatus, Severity, ControlArea, Currency } from '../../api'

const router = useRouter()
const all = ref<ExceptionSummary[]>([])
const rows = ref<ExceptionSummary[]>([])
const total = ref(0)
const loading = ref(true)
const error = ref<string | null>(null)
const forbidden = ref(false)
const selected = ref<string[]>([])
const f = reactive<{ severity?: Severity; control?: ControlArea; status?: ExceptionStatus; owner?: string; currency?: Currency; search?: string; page: number }>({ page: 1 })
const PAGE_SIZE = 25

const sevStatus: Record<Severity, Status> = { CRITICAL: 'fail', HIGH: 'warn', MEDIUM: 'info', LOW: 'neutral' }
const controlLabel: Record<ControlArea, string> = {
  data_completeness: 'Data completeness', matching: 'Matching', independent_ledger: 'Independent ledger',
  transaction_lifecycle: 'Transaction lifecycle', safeguarding: 'Safeguarding',
}
const statusLabel: Record<ExceptionStatus, string> = {
  OPEN: 'Open', IN_INVESTIGATION: 'Investigating', WAITING_FOR_DATA: 'Waiting for evidence', PENDING_APPROVAL: 'Approval required',
  RESOLVED: 'Resolved', ACCEPTED_DIFFERENCE: 'Accepted difference', FALSE_POSITIVE: 'False positive',
}
const sevs: Severity[] = ['CRITICAL', 'HIGH', 'MEDIUM', 'LOW']
const opt = (o: Record<string, string>) => Object.entries(o).map(([value, label]) => ({ value, label }))

const isOpen = (e: ExceptionSummary) => !['RESOLVED', 'ACCEPTED_DIFFERENCE', 'FALSE_POSITIVE'].includes(e.status)
const open = computed(() => all.value.filter(isOpen))
const sevSummary = computed(() => sevs.map((s) => {
  const items = open.value.filter((e) => e.severity === s)
  const ccys = [...new Set(items.filter((e) => e.value).map((e) => e.value!.currency))]
  const values = ccys.map((c) => sumMoney(items.filter((e) => e.value?.currency === c).map((e) => e.value!), c))
  return { s, count: items.length, values }
}))
const overdue = computed(() => open.value.filter((e) => new Date(e.dueAt).getTime() < Date.now()))
const unassigned = computed(() => open.value.filter((e) => !e.owner))
const byControl = computed(() => Object.keys(controlLabel).map((c) => ({ c: c as ControlArea, n: open.value.filter((e) => e.control === c).length })).filter((x) => x.n))
const reasons = computed(() => {
  const m = new Map<string, number>()
  open.value.forEach((e) => m.set(e.reasonCode, (m.get(e.reasonCode) ?? 0) + 1))
  return [...m].filter(([, n]) => n > 1).sort((a, b) => b[1] - a[1])
})
const owners = computed(() => [...new Set(all.value.map((e) => e.owner).filter(Boolean) as string[])])

const fmtDue = (iso: string) => new Date(iso).toLocaleString('en-GB', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' })

async function load() {
  loading.value = true; error.value = null
  try {
    const { page, ...rest } = f
    const p = await listExceptions({ ...rest, page, pageSize: PAGE_SIZE })
    rows.value = p.items; total.value = p.total
  } catch (e: any) {
    if (e?.status === 403) forbidden.value = true
    else error.value = e?.message ?? 'Failed to load exceptions'
  } finally { loading.value = false }
}
onMounted(async () => {
  try { all.value = (await listExceptions({ pageSize: 1000 })).items } catch { /* summary optional; table reports errors */ }
  load()
})
watch(() => [f.severity, f.control, f.status, f.owner, f.currency, f.search], () => { f.page = 1; load() })
watch(() => f.page, load)

const columns = [
  { title: 'Severity', key: 'severity', width: 110 },
  { title: 'Exception', key: 'exception' },
  { title: 'Control', key: 'control', width: 160 },
  { title: 'Value', key: 'value', align: 'right' as const, width: 150 },
  { title: 'Owner / due', key: 'owner', width: 150 },
  { title: 'Status', key: 'status', width: 170 },
]
const rowProps = (r: ExceptionSummary) => ({ style: { cursor: 'pointer' }, onClick: () => router.push(`/exceptions/${r.id}`) })
const isOverdue = (r: ExceptionSummary) => isOpen(r) && new Date(r.dueAt).getTime() < Date.now()

function exportCsv() {
  const head = 'id,severity,reason,control,amount,currency,owner,due,status'
  const body = rows.value.map((r) => [r.id, r.severity, r.reasonCode, r.control, r.value?.amount ?? '', r.value?.currency ?? '', r.owner ?? '', r.dueAt, r.status].join(','))
  const url = URL.createObjectURL(new Blob([[head, ...body].join('\n')], { type: 'text/csv' }))
  Object.assign(document.createElement('a'), { href: url, download: 'exceptions.csv' }).click()
  URL.revokeObjectURL(url)
}
</script>

<template>
  <div class="ph">
    <div>
      <div class="eyebrow">EXCEPTIONS · 27 OCT 2026</div>
      <h1>Exceptions queue</h1>
      <p>{{ open.length }} open exceptions. Counts and values are shown separately.</p>
    </div>
    <div class="acts">
      <a-button @click="exportCsv">Export filtered queue</a-button>
      <a-button type="primary" :disabled="!selected.length">Assign selected</a-button>
    </div>
  </div>

  <StatePlaceholder v-if="forbidden" state="forbidden" />
  <template v-else>
    <div class="card cards">
      <div class="sum"><div class="lbl">OPEN</div><div class="num">{{ open.length }}</div><div class="vals">{{ byControl.length }} control areas</div></div>
      <div v-for="x in sevSummary.filter((y) => y.count)" :key="x.s" class="sum">
        <div class="lbl">{{ x.s }}</div>
        <div class="num" :class="x.s === 'MEDIUM' ? 'warn' : x.s === 'LOW' ? '' : 'fail'">{{ x.count }}</div>
        <div class="vals"><MoneyAmount v-for="v in x.values" :key="v.currency" :amount="v.amount" :currency="v.currency" /><span v-if="!x.values.length">no value</span></div>
      </div>
      <div class="sum"><div class="lbl">OVERDUE</div><div class="num fail">{{ overdue.length }}</div><div class="vals">{{ overdue.map((e) => e.id).join(', ') || 'none' }}</div></div>
      <div class="sum"><div class="lbl">UNASSIGNED</div><div class="num warn">{{ unassigned.length }}</div><div class="vals">no owner yet</div></div>
    </div>

    <div class="analysis">
      <div class="card">
        <div class="ch"><span class="lbl">BY CONTROL AREA</span><span class="muted">Open exceptions</span></div>
        <div v-for="x in byControl" :key="x.c" class="bar"><span class="bn">{{ controlLabel[x.c] }}</span>
          <span class="track"><i :style="{ width: (x.n / Math.max(...byControl.map((y) => y.n))) * 100 + '%' }" /></span><b>{{ x.n }}</b></div>
      </div>
      <div class="card">
        <div class="ch"><span class="lbl">REPEATED REASONS</span><span class="muted">Grouping never changes severity or owner</span></div>
        <div v-for="[r, n] in reasons" :key="r" class="line"><span class="mono">{{ r }}</span><b>×{{ n }}</b></div>
        <div v-if="!reasons.length" class="muted">No repeated reasons.</div>
      </div>
    </div>

    <div class="card filters">
      <a-input-search v-model:value="f.search" placeholder="Search exceptions" allow-clear style="width: 220px" />
      <a-select v-model:value="f.status" placeholder="Status" allow-clear style="width: 190px" :options="opt(statusLabel)" />
      <a-select v-model:value="f.severity" placeholder="Severity" allow-clear style="width: 120px" :options="sevs.map((s) => ({ value: s, label: s }))" />
      <a-select v-model:value="f.control" placeholder="Control" allow-clear style="width: 190px" :options="opt(controlLabel)" />
      <a-select v-model:value="f.owner" placeholder="Owner" allow-clear style="width: 140px" :options="owners.map((o) => ({ value: o, label: o }))" />
      <a-select v-model:value="f.currency" placeholder="Currency" allow-clear style="width: 120px" :options="['GBP', 'EUR', 'USD'].map((c) => ({ value: c, label: c }))" />
    </div>

    <StatePlaceholder v-if="error" state="error" :message="error"><template #action><a-button @click="load">Retry</a-button></template></StatePlaceholder>
    <StatePlaceholder v-else-if="loading && !rows.length" state="loading" />
    <StatePlaceholder v-else-if="!rows.length" state="empty" message="No exceptions match the filters." />
    <div v-else class="card">
      <a-table
        :columns="columns" :data-source="rows" row-key="id" size="middle" :loading="loading" :custom-row="rowProps"
        :row-selection="{ selectedRowKeys: selected, onChange: (k: (string | number)[]) => (selected = k as string[]) }"
        :pagination="{ current: f.page, pageSize: PAGE_SIZE, total, showSizeChanger: false, onChange: (p: number) => (f.page = p) }"
      >
        <template #bodyCell="{ column, record }">
          <StatusPill v-if="column.key === 'severity'" :status="sevStatus[record.severity as Severity]" :label="record.severity" />
          <template v-else-if="column.key === 'exception'">
            <div><b>{{ record.id }}</b> <span class="muted">{{ record.reasonCode }}</span><a-tag v-if="record.blocking" color="red" style="margin-left: 6px">Blocking</a-tag></div>
            <div>{{ record.title }}</div>
          </template>
          <template v-else-if="column.key === 'control'">{{ controlLabel[record.control as ControlArea] }}</template>
          <template v-else-if="column.key === 'value'">
            <MoneyAmount v-if="record.value" :amount="record.value.amount" :currency="record.value.currency" /><span v-else class="muted">—</span>
          </template>
          <template v-else-if="column.key === 'owner'">
            <div :class="{ muted: !record.owner }">{{ record.owner ?? 'Unassigned' }}</div>
            <div :class="isOverdue(record) ? 'od' : 'muted'">{{ isOverdue(record) ? 'Overdue ' : '' }}{{ fmtDue(record.dueAt) }}</div>
          </template>
          <StatusPill v-else-if="column.key === 'status'" status="neutral" :label="statusLabel[record.status as ExceptionStatus]" />
        </template>
      </a-table>
      <div class="muted foot">Showing {{ rows.length }} of {{ total }} · sorted by severity · bulk actions limited to assignment, tagging and evidence requests</div>
    </div>
  </template>
</template>

<style scoped>
.ph { display: flex; justify-content: space-between; align-items: flex-start; margin-bottom: 16px; }
.eyebrow { font-size: 10px; font-weight: 600; letter-spacing: .08em; color: var(--ink-3); }
h1 { margin: 4px 0; font-size: 28px; font-weight: 600; line-height: 1.3; }
.ph p { margin: 0; color: var(--ink-2); font-size: 13px; }
.acts { display: flex; gap: 8px; }
.cards { display: flex; margin-bottom: 16px; }
.sum { flex: 1; padding: 14px 16px; border-right: 1px solid var(--border); }
.sum:last-child { border-right: 0; }
.lbl { font-size: 10px; font-weight: 600; letter-spacing: .08em; color: var(--ink-3); margin-bottom: 4px; }
.num { font-size: 28px; font-weight: 600; line-height: 1.3; }
.num.fail { color: var(--fail); } .num.warn { color: var(--warn); }
.vals { display: flex; flex-direction: column; font-size: 11px; color: var(--ink-3); }
.analysis { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; margin-bottom: 16px; align-items: start; }
.analysis .card { padding: 0 16px 12px; }
.ch { display: flex; gap: 12px; align-items: center; height: 44px; }
.ch .lbl { margin: 0; }
.bar { display: flex; align-items: center; gap: 12px; padding: 4px 0; font-size: 12px; }
.bn { width: 150px; }
.track { flex: 1; height: 8px; background: var(--surface-sunk); border-radius: 4px; overflow: hidden; }
.track i { display: block; height: 100%; background: var(--ink); border-radius: 4px; }
.line { display: flex; justify-content: space-between; padding: 5px 0; font-size: 12px; border-top: 1px solid var(--border); }
.filters { display: flex; flex-wrap: wrap; gap: 8px; padding: 10px 16px; margin-bottom: 12px; }
.muted { color: var(--ink-3); font-size: 12px; }
.od { color: var(--fail); font-size: 12px; font-weight: 500; }
.foot { padding: 12px 16px; background: var(--surface-sunk); border-radius: 0 0 5px 5px; }
</style>
