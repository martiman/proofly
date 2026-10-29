<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { getControlRoom } from '../../api'
import type { ControlRoomData, ControlState, DeliveryState, Severity, ControlArea } from '../../api'
import { StatePlaceholder, StatusPill, MoneyAmount, EvidenceLink } from '../../components'
import type { Status } from '../../components/StatusPill.vue'

const router = useRouter()
const data = ref<ControlRoomData | null>(null)
const loading = ref(true)
const error = ref<string | null>(null)
const forbidden = ref(false)
const severity = ref<Severity | undefined>()
const control = ref<ControlArea | undefined>()
const currency = ref<string | undefined>()

async function load() {
  loading.value = true
  error.value = null
  try {
    data.value = await getControlRoom()
  } catch (e: any) {
    if (e?.status === 403 || e?.code === 'FORBIDDEN') forbidden.value = true
    else error.value = e?.message ?? 'Failed to load control room'
  } finally {
    loading.value = false
  }
}
onMounted(load)

const ctrlStatus = (s: ControlState): Status =>
  s === 'PASSED' ? 'pass' : s === 'FAILED' ? 'fail' : s === 'BLOCKED' ? 'fail' : 'warn'
const delStatus = (s: DeliveryState): Status =>
  s === 'RECEIVED' ? 'pass' : s === 'MISSING' || s === 'REJECTED' ? 'fail' : s === 'SUPERSEDED' ? 'neutral' : 'warn'
const sevStatus = (s: Severity): Status => (s === 'CRITICAL' || s === 'HIGH' ? 'fail' : s === 'MEDIUM' ? 'warn' : 'neutral')
const human = (s: string) => s.replace(/_/g, ' ').toLowerCase().replace(/^\w/, (c) => c.toUpperCase())
const fmt = (d: string | null) => (d ? d.replace('T', ' ').slice(0, 16) : '—')

const longDate = computed(() =>
  data.value ? new Date(data.value.run.businessDate + 'T00:00:00').toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric' }) : '')
const blockers = computed(() => (data.value?.attentionQueue ?? []).filter((e) => e.blocking))
const progress = computed(() => {
  const d = data.value
  if (!d) return []
  const del = d.evidenceReadiness
  const got = del.filter((x) => x.receivedAt).length
  const st = d.run.state
  const processed = st !== 'AWAITING_EVIDENCE' && st !== 'PROCESSING'
  return [
    { label: 'Evidence received', detail: `${got} / ${del.length} sources`, tone: got === del.length ? 'pass' : 'warn' },
    { label: 'Source validation', detail: del.some((x) => x.state === 'REJECTED' || x.state === 'MISSING') ? 'Issues found' : 'Completed', tone: del.some((x) => x.state === 'REJECTED' || x.state === 'MISSING') ? 'fail' : 'pass' },
    { label: 'Processing', detail: processed ? 'Completed' : 'In progress', tone: processed ? 'pass' : 'warn' },
    { label: 'Review', detail: blockers.value.length ? `${blockers.value.length} blocking issues` : human(st), tone: blockers.value.length ? 'fail' : st === 'READY_FOR_SIGN_OFF' || st === 'SIGNED_OFF' ? 'pass' : 'warn' },
    { label: 'Sign-off', detail: human(d.run.signOffState), tone: 'neutral' },
  ]
})
const impact = (s: ControlState): [string, string] =>
  s === 'PASSED' ? ['None', 'none'] : s === 'FAILED' || s === 'BLOCKED' ? ['Blocks', 'fail'] : ['Needs approval', 'warn']

const queue = computed(() =>
  (data.value?.attentionQueue ?? []).filter(
    (e) =>
      (!severity.value || e.severity === severity.value) &&
      (!control.value || e.control === control.value) &&
      (!currency.value || e.value?.currency === currency.value),
  ),
)
const currencies = computed(() => [...new Set(data.value?.safeguarding.map((p) => p.currency))])
const controlOptions = computed(() => data.value?.controlAreas.map((c) => ({ value: c.area, label: c.label })) ?? [])
const severityOptions = (['CRITICAL', 'HIGH', 'MEDIUM', 'LOW'] as Severity[]).map((v) => ({ value: v, label: human(v) }))

function summaryLink(l: { kind: string; id: string }) {
  if (l.kind === 'exception') return `/exceptions/${l.id}`
  if (l.kind === 'source') return '/sources'
  return '/exceptions'
}

const columns = [
  { title: 'ID', key: 'id' },
  { title: 'Exception', key: 'title' },
  { title: 'Control', key: 'control' },
  { title: 'Severity', key: 'severity' },
  { title: 'Status', key: 'status' },
  { title: 'Owner', key: 'owner' },
  { title: 'Value', key: 'value', align: 'right' as const },
  { title: 'Due', key: 'dueAt' },
]
</script>

<template>
  <div class="dh">
    <div>
      <div class="eyebrow">DAILY CONTROL ROOM</div>
      <h1>Business date {{ longDate }}</h1>
      <div class="ctx">{{ data ? `Run v${data.run.version} · Ruleset ${data.run.rulesetVersion}` : '' }}</div>
    </div>
    <div class="acts">
      <span v-if="data" class="upd">Last evidence {{ fmt(data.run.lastEvidenceArrivalAt) }}</span>
      <a-button @click="router.push('/exceptions')">Open exceptions</a-button>
      <a-button type="primary" :disabled="!data || data.run.state !== 'READY_FOR_SIGN_OFF'" @click="router.push('/sign-off')">
        Proceed to sign-off
      </a-button>
    </div>
  </div>

  <StatePlaceholder v-if="loading" state="loading" />
  <StatePlaceholder v-else-if="forbidden" state="forbidden" />
  <StatePlaceholder v-else-if="error" state="error" :message="error">
    <template #action><a-button @click="load">Retry</a-button></template>
  </StatePlaceholder>
  <StatePlaceholder v-else-if="!data" state="empty" message="No run available for this business date." />

  <template v-else>
    <div class="concl" :class="blockers.length ? 'bad' : 'ok'">
      <div class="cl">
        <span class="icon">{{ blockers.length ? '!' : '✓' }}</span>
        <div>
          <div class="eyebrow">RUN CONCLUSION</div>
          <div class="ct">{{ blockers.length ? 'Sign-off blocked' : human(data.run.state) }}</div>
          <div class="cr">
            {{ blockers.length
              ? `${blockers.length} blocking issue${blockers.length === 1 ? '' : 's'} must be resolved before this run can be signed off.`
              : 'No blocking issues.' }}
          </div>
        </div>
      </div>
      <div class="ca">
        <a-button v-if="blockers.length" type="primary" @click="router.push('/exceptions')">Review {{ blockers.length }} blockers</a-button>
        <router-link to="/exceptions">View all exceptions ({{ data.attentionQueue.length }})</router-link>
      </div>
    </div>

    <div class="card progress">
      <div v-for="p in progress" :key="p.label" class="step">
        <span class="mk" :class="p.tone">{{ p.tone === 'pass' ? '✓' : p.tone === 'fail' ? '!' : '' }}</span>
        <div>
          <div class="sl">{{ p.label }}</div>
          <div class="sd" :class="p.tone">{{ p.detail }}</div>
        </div>
      </div>
    </div>

    <div v-if="blockers.length" class="card blk">
      <div class="bh">
        <span class="eyebrow fail">BLOCKING SIGN-OFF</span><span class="cnt">{{ blockers.length }}</span>
        <span class="hint">Each one stops the run on its own. Resolve it, or obtain an approved qualification.</span>
      </div>
      <div v-for="(e, i) in blockers" :key="e.id" class="brow">
        <span class="num" :class="{ solid: i === 0 }">{{ i + 1 }}</span>
        <div class="issue">
          <div class="it">{{ e.title }}</div>
          <span class="tag">{{ e.id }} · {{ e.severity }}</span>
        </div>
        <div class="val">
          <MoneyAmount v-if="e.value" :amount="e.value.amount" :currency="e.value.currency" />
          <div class="muted">impact</div>
        </div>
        <div class="own">
          <div :class="e.owner ? 'on' : 'on warn'">{{ e.owner ?? 'Unassigned' }}</div>
          <div class="muted">Due {{ fmt(e.dueAt) }}</div>
        </div>
        <a-button size="small" @click="router.push(`/exceptions/${e.id}`)">Open</a-button>
      </div>
    </div>

    <div class="area">
      <div class="card ctrls">
        <div class="ch"><span class="eyebrow">MANDATORY CONTROLS</span><span class="muted">Evaluated separately · no composite score</span></div>
        <div class="th"><span>CONTROL</span><span>STATE</span><span>RESULT</span><span>SIGN-OFF IMPACT</span></div>
        <div v-for="c in data.controlAreas" :key="c.area" class="tr">
          <span class="cn">{{ c.label }}</span>
          <span><StatusPill :status="ctrlStatus(c.state)" :label="human(c.state)" /></span>
          <span class="res">
            {{ c.recordCount.toLocaleString('en-GB') }} records
            ({{ c.changeFromPrevious >= 0 ? '+' : '' }}{{ c.changeFromPrevious }} vs previous) · {{ c.openExceptions }} open
            <template v-for="m in c.value" :key="m.currency"> · <MoneyAmount :amount="m.amount" :currency="m.currency" /></template>
          </span>
          <span :class="['imp', impact(c.state)[1]]">{{ impact(c.state)[0] }}</span>
        </div>
        <div class="legend">
          <div><b class="fail">Blocks</b> Must be resolved, or covered by an approved qualification, before the run can be signed.</div>
          <div><b class="warn">Needs approval</b> Owned exception. The control owner must approve the resolution or accepted difference.</div>
        </div>
      </div>
      <div class="card sg">
        <div class="ch"><span class="eyebrow">SAFEGUARDING</span><span class="muted">By currency · no netting</span>
          <router-link to="/safeguarding">Full calculation</router-link></div>
        <div v-for="p in data.safeguarding" :key="p.currency" class="cur">
          <div class="crow"><b>{{ p.currency }}</b>
            <StatusPill :status="p.state === 'COVERED' || p.state === 'SURPLUS' || p.state === 'NO_OBLIGATION' ? 'pass' : p.state === 'SHORTFALL' ? 'fail' : 'warn'" :label="human(p.state)" />
          </div>
          <div class="muted">Obligation <MoneyAmount :amount="p.obligation.amount" :currency="p.currency" /> · Eligible <MoneyAmount :amount="p.eligibleBalance.amount" :currency="p.currency" /></div>
          <div class="muted">Result <MoneyAmount :amount="p.result.amount" :currency="p.currency" /></div>
        </div>
      </div>
    </div>

    <div v-if="data.summary.length" class="card block">
      <h3>Since previous business date</h3>
      <ul class="summary">
        <li v-for="(s, i) in data.summary" :key="i">
          {{ s.text }}
          <EvidenceLink v-if="s.link" :label="s.link.id" :to="summaryLink(s.link)" />
        </li>
      </ul>
    </div>

    <div class="card block">
      <h3>Evidence readiness</h3>
      <a-table :data-source="data.evidenceReadiness" :pagination="false" size="small" row-key="deliveryId"
        :columns="[
          { title: 'Source', key: 'src' }, { title: 'File', dataIndex: 'fileName' },
          { title: 'Expected by', key: 'exp' }, { title: 'Received', key: 'rec' },
          { title: 'Records', key: 'cnt', align: 'right' }, { title: 'State', key: 'state' },
        ]">
        <template #bodyCell="{ column, record }">
          <EvidenceLink v-if="column.key === 'src'" :label="record.sourceName" to="/sources" />
          <template v-else-if="column.key === 'exp'">{{ fmt(record.expectedBy) }}</template>
          <template v-else-if="column.key === 'rec'">{{ fmt(record.receivedAt) }}</template>
          <template v-else-if="column.key === 'cnt'">{{ record.acceptedRecords }} / {{ record.declaredRecordCount }}</template>
          <StatusPill v-else-if="column.key === 'state'" :status="delStatus(record.state)" :label="human(record.state)" />
        </template>
      </a-table>
    </div>

    <div class="card block">
      <div class="row">
        <h3>Attention queue</h3>
        <div class="filters">
          <a-select v-model:value="severity" :options="severityOptions" placeholder="Severity" allow-clear style="width: 130px" />
          <a-select v-model:value="control" :options="controlOptions" placeholder="Control area" allow-clear style="width: 190px" />
          <a-select v-model:value="currency" :options="currencies.map((c) => ({ value: c, label: c }))" placeholder="Currency" allow-clear style="width: 110px" />
        </div>
      </div>
      <a-table :data-source="queue" :columns="columns" :pagination="false" size="small" row-key="id"
        :locale="{ emptyText: 'No items need attention.' }">
        <template #bodyCell="{ column, record }">
          <EvidenceLink v-if="column.key === 'id'" :label="record.id" :to="`/exceptions/${record.id}`" />
          <template v-else-if="column.key === 'title'">
            {{ record.title }} <a-tag v-if="record.blocking" color="red">Blocking</a-tag>
          </template>
          <template v-else-if="column.key === 'control'">{{ human(record.control) }}</template>
          <StatusPill v-else-if="column.key === 'severity'" :status="sevStatus(record.severity)" :label="human(record.severity)" />
          <template v-else-if="column.key === 'status'">{{ human(record.status) }}</template>
          <template v-else-if="column.key === 'owner'">{{ record.owner ?? 'Unassigned' }}</template>
          <MoneyAmount v-else-if="column.key === 'value' && record.value" :amount="record.value.amount" :currency="record.value.currency" />
          <template v-else-if="column.key === 'dueAt'">{{ fmt(record.dueAt) }}</template>
        </template>
      </a-table>
    </div>

  </template>
</template>

<style scoped>
.block { padding: 16px; margin-bottom: 16px; }
h3 { margin: 0 0 12px; font-size: 15px; font-weight: 600; }
.muted { color: var(--ink-3); font-size: 11px; }
.eyebrow { font-size: 10px; font-weight: 600; letter-spacing: .08em; color: var(--ink-3); }
.eyebrow.fail { color: var(--fail); }
.dh { display: flex; justify-content: space-between; align-items: flex-start; margin-bottom: 16px; }
.dh h1 { margin: 4px 0; font-size: 30px; font-weight: 600; line-height: 1.3; }
.ctx { color: var(--ink-2); font-size: 13px; }
.acts { display: flex; gap: 8px; align-items: center; }
.upd { color: var(--ink-3); font-size: 12px; margin-right: 8px; }
.concl { display: flex; justify-content: space-between; align-items: center; padding: 18px 24px; border-radius: var(--radius-card); margin-bottom: 16px; }
.concl.bad { background: var(--fail-soft); }
.concl.ok { background: var(--pass-soft); }
.cl { display: flex; gap: 16px; align-items: center; }
.icon { width: 42px; height: 42px; border-radius: 50%; color: #fff; display: grid; place-items: center; font-size: 20px; font-weight: 700; }
.bad .icon { background: var(--fail); } .ok .icon { background: var(--pass); }
.ct { font-size: 28px; font-weight: 600; line-height: 1.3; }
.bad .ct { color: var(--fail); } .ok .ct { color: var(--pass); }
.cr { color: var(--ink-2); font-size: 14px; }
.ca { display: flex; flex-direction: column; align-items: flex-end; gap: 8px; }
.progress { display: flex; margin-bottom: 16px; padding: 14px 16px; }
.step { flex: 1; display: flex; gap: 10px; align-items: flex-start; }
.mk { width: 22px; height: 22px; border-radius: 50%; background: var(--surface-sunk); color: #fff; display: grid; place-items: center; font-size: 12px; font-weight: 700; flex: none; }
.mk.pass { background: var(--pass); } .mk.fail { background: var(--fail); } .mk.warn { background: var(--warn); }
.sl { font-size: 12px; font-weight: 600; }
.sd { font-size: 11px; color: var(--ink-3); }
.sd.fail { color: var(--fail); } .sd.warn { color: var(--warn); }
.blk { margin-bottom: 16px; }
.bh { display: flex; gap: 10px; align-items: center; padding: 14px 16px; border-bottom: 1px solid var(--border); }
.cnt { background: var(--fail); color: #fff; border-radius: 8px; font-size: 11px; font-weight: 600; padding: 0 6px; }
.hint { color: var(--ink-3); font-size: 12px; }
.brow { display: flex; gap: 16px; align-items: center; padding: 16px; border-bottom: 1px solid var(--border); }
.brow:last-child { border-bottom: 0; }
.num { width: 24px; height: 24px; border-radius: 50%; background: var(--fail-soft); color: var(--fail); display: grid; place-items: center; font-size: 12px; font-weight: 600; flex: none; }
.num.solid { background: var(--fail); color: #fff; }
.issue { flex: 1; display: flex; flex-direction: column; gap: 6px; align-items: flex-start; }
.it { font-size: 15px; font-weight: 600; }
.tag { background: var(--neutral-soft); color: var(--ink-2); border-radius: 3px; font-size: 10px; font-weight: 500; padding: 2px 6px; }
.val { width: 170px; text-align: right; color: var(--fail); }
.own { width: 150px; }
.on { font-size: 12px; font-weight: 500; } .on.warn { color: var(--warn); }
.area { display: grid; grid-template-columns: 1fr 380px; gap: 16px; margin-bottom: 16px; align-items: start; }
.ch { display: flex; gap: 12px; align-items: center; padding: 14px 16px; }
.ch .muted { flex: 1; font-size: 12px; }
.th, .tr { display: grid; grid-template-columns: 1.4fr .9fr 2.4fr 1.1fr; gap: 12px; padding: 0 16px; align-items: center; }
.th { background: var(--surface-sunk); font-size: 10px; font-weight: 600; letter-spacing: .06em; color: var(--ink-3); height: 29px; }
.tr { min-height: 48px; padding-top: 8px; padding-bottom: 8px; border-bottom: 1px solid var(--border); }
.cn { font-size: 13px; font-weight: 500; }
.res { font-size: 12px; color: var(--ink-2); }
.imp { font-size: 12px; font-weight: 600; } .imp.fail { color: var(--fail); } .imp.warn { color: var(--warn); font-weight: 400; } .imp.none { color: var(--ink-3); font-weight: 400; }
.legend { background: var(--surface-sunk); padding: 12px 16px; display: flex; flex-direction: column; gap: 6px; font-size: 11px; color: var(--ink-3); border-radius: 0 0 5px 5px; }
.legend b.fail { color: var(--fail); margin-right: 8px; } .legend b.warn { color: var(--warn); margin-right: 8px; }
.cur { margin: 0 16px; padding: 10px 0; border-top: 1px solid var(--border); display: flex; flex-direction: column; gap: 4px; }
.crow { display: flex; justify-content: space-between; align-items: center; }
.row { display: flex; justify-content: space-between; align-items: center; gap: 8px; }
.row h3 { margin: 0; }
.summary { margin: 0; padding-left: 18px; display: flex; flex-direction: column; gap: 6px; }
.filters { display: flex; gap: 8px; }
.row + .ant-table-wrapper, .row + :deep(.ant-table-wrapper) { margin-top: 12px; }
</style>
