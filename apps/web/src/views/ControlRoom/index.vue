<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { compareRuns, getControlRoom, getRunSignOff } from '../../api/services'
import { formatMoney } from '../../api/client'
import type { Schema } from '../../api/services'
import { useResource } from '../../api/composables'
import { StatePlaceholder, StatusPill, EvidenceLink } from '../../components'
import { useRunContext } from '../../composables/useRunContext'
import { conclusion, ctrlStatus, delStatus, exceptionLink, fmtDate, human, impact, sevStatus, sgStatus, showing, summaryLink } from './logic'

type Money = Schema<'Money'>
const router = useRouter()
const ctx = useRunContext()
const { runId } = ctx
const room = useResource(() => runId.value, (id, signal) => getControlRoom(id, { signal }))
// Best effort: the sign-off pack carries the authoritative blockers and totals. Callers without signoff.view fall back to the queue.
const pack = useResource(() => runId.value, (id, signal) => getRunSignOff(id, { openMattersLimit: 1 }, { signal }).catch(() => null))

const data = computed(() => room.data.value)
const signOff = computed(() => pack.data.value ?? null)
const money = (m: Money) => formatMoney(m.amount, m.currency)
const isNeg = (m: Money) => m.amount.startsWith('-')

const longDate = computed(() =>
  data.value ? new Date(data.value.run.businessDate + 'T00:00:00').toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric' }) : '')
const queueBlocking = computed(() => (data.value?.attentionQueue ?? []).filter((e) => e.blocking))
const blockers = computed(() => signOff.value?.blockers ?? null)
const concl = computed(() => conclusion(data.value?.run.state ?? '', blockers.value ? blockers.value.length : null, queueBlocking.value.length))
const openTotal = computed(() => signOff.value?.openMattersTotal ?? null)
const owner = (o: { name: string } | null) => o?.name ?? 'Unassigned'

const progress = computed(() => {
  const d = data.value
  if (!d) return []
  const del = d.evidenceReadiness
  const got = del.filter((x) => x.receivedAt).length
  const bad = del.some((x) => x.state === 'REJECTED' || x.state === 'MISSING')
  const st = d.run.state
  const processed = st !== 'AWAITING_EVIDENCE' && st !== 'PROCESSING'
  const nb = blockers.value?.length ?? queueBlocking.value.length
  return [
    { label: 'Evidence received', detail: `${got} / ${del.length} sources`, tone: got === del.length ? 'pass' : 'warn' },
    { label: 'Source validation', detail: bad ? 'Issues found' : 'Completed', tone: bad ? 'fail' : 'pass' },
    { label: 'Processing', detail: processed ? 'Completed' : 'In progress', tone: processed ? 'pass' : 'warn' },
    { label: 'Review', detail: nb ? `${nb} blocking issues` : human(st), tone: nb ? 'fail' : st === 'READY_FOR_SIGN_OFF' || st === 'SIGNED_OFF' ? 'pass' : 'warn' },
    { label: 'Sign-off', detail: human(d.run.signOffState), tone: 'neutral' },
  ]
})

// Run comparison (server side)
const against = ref<string | undefined>()
const compareOptions = computed(() =>
  ctx.runs.value.filter((r) => r.id !== runId.value).map((r) => ({ value: r.id, label: `${r.businessDate} v${r.version}` })))
const cmp = useResource(() => (runId.value && against.value ? { run: runId.value, against: against.value } : null),
  (a, signal) => compareRuns(a.run, { against: a.against }, { signal }))
const compared = computed(() => cmp.data.value)

const columns = [
  { title: 'ID', key: 'id' }, { title: 'Exception', key: 'title' }, { title: 'Control', key: 'control' },
  { title: 'Severity', key: 'severity' }, { title: 'Status', key: 'status' }, { title: 'Owner', key: 'owner' },
  { title: 'Value', key: 'value', align: 'right' as const }, { title: 'Due', key: 'dueAt' },
]
const q = computed(() => (runId.value ? { run: runId.value } : {}))
const retry = () => { void room.reload(); void pack.reload() }
</script>

<template>
  <div class="dh">
    <div>
      <div class="eyebrow">DAILY CONTROL ROOM</div>
      <h1>{{ data ? `Business date ${longDate}` : 'Business date' }}</h1>
      <div class="ctx">{{ data ? `${data.run.legalEntityId} · Run v${data.run.version} · ${human(data.run.state)} · Ruleset ${data.run.rulesetVersion}` : '' }}</div>
    </div>
    <div class="acts">
      <span v-if="data" class="upd">Last evidence {{ fmtDate(data.run.lastEvidenceArrivalAt) }}</span>
      <a-select v-model:value="against" :options="compareOptions" placeholder="Compare with run" allow-clear style="width: 190px" />
      <a-button @click="router.push({ path: '/exceptions', query: q })">Open exceptions</a-button>
      <a-button type="primary" :disabled="!data" @click="router.push({ path: '/sign-off', query: q })">Proceed to sign-off</a-button>
    </div>
  </div>

  <StatePlaceholder v-if="!runId && !ctx.loading.value && !ctx.error.value" state="empty" message="No run available." />
  <StatePlaceholder v-else-if="room.placeholder.value" :state="room.placeholder.value" :message="room.message.value">
    <template #action><a-button @click="retry">Retry</a-button></template>
  </StatePlaceholder>

  <template v-else-if="data">
    <a-alert v-if="data.run.state === 'SUPERSEDED' && data.run.supersededByRunId" type="warning" show-icon class="gap"
      message="This run version is superseded.">
      <template #description><router-link :to="{ path: '/control-room', query: { run: data.run.supersededByRunId } }">Open {{ data.run.supersededByRunId }}</router-link></template>
    </a-alert>

    <div class="concl" :class="concl.blocked ? 'bad' : 'ok'">
      <div class="cl">
        <span class="icon">{{ concl.blocked ? '!' : '✓' }}</span>
        <div>
          <div class="eyebrow">RUN CONCLUSION</div>
          <div class="ct">{{ concl.headline }}</div>
          <div class="cr">{{ concl.reason }}</div>
        </div>
      </div>
      <div class="ca">
        <a-button v-if="concl.blocked" type="primary" @click="router.push({ path: '/exceptions', query: { ...q, blocking: 'true' } })">Review blockers</a-button>
        <router-link :to="{ path: '/exceptions', query: q }">View all exceptions{{ openTotal != null ? ` (${openTotal})` : '' }}</router-link>
      </div>
    </div>

    <div v-if="compared" class="card block" data-test="compare">
      <h3>Compared with {{ compared.against.businessDate }} v{{ compared.against.version }}</h3>
      <div class="cmp">
        <span v-for="c in compared.controlAreas" :key="c.area" class="cmpi">
          {{ human(c.area) }}: <StatusPill :status="ctrlStatus(c.againstState)" :label="human(c.againstState)" /> →
          <StatusPill :status="ctrlStatus(c.state)" :label="human(c.state)" /> ({{ c.recordCountDelta > 0 ? '+' : '' }}{{ c.recordCountDelta }} records)
        </span>
      </div>
      <div class="muted">{{ compared.newExceptionIds.length }} new and {{ compared.resolvedExceptionIds.length }} resolved exceptions.
        <span v-for="d in compared.safeguardingDelta ?? []" :key="d.label + d.currency"> {{ d.label }} {{ d.currency }}: {{ d.change }};</span></div>
    </div>
    <StatePlaceholder v-else-if="against && cmp.placeholder.value" :state="cmp.placeholder.value" :message="cmp.message.value" />

    <div class="card progress">
      <div v-for="p in progress" :key="p.label" class="step">
        <span class="mk" :class="p.tone">{{ p.tone === 'pass' ? '✓' : p.tone === 'fail' ? '!' : '' }}</span>
        <div>
          <div class="sl">{{ p.label }}</div>
          <div class="sd" :class="p.tone">{{ p.detail }}</div>
        </div>
      </div>
    </div>

    <div v-if="concl.blocked" class="card blk" data-test="blockers">
      <div class="bh">
        <span class="eyebrow fail">BLOCKING SIGN-OFF</span><span class="cnt">{{ blockers ? blockers.length : queueBlocking.length }}</span>
        <span class="hint">Each one stops the run on its own. Resolve it, or obtain an approved qualification.</span>
      </div>
      <template v-if="blockers">
        <div v-for="(b, i) in blockers.slice(0, 5)" :key="b" class="brow">
          <span class="num" :class="{ solid: i === 0 }">{{ i + 1 }}</span>
          <div class="issue"><div class="it">{{ b }}</div></div>
        </div>
        <div v-if="blockers.length > 5" class="brow muted">
          Showing 5 of {{ blockers.length }} · <router-link :to="{ path: '/sign-off', query: q }">all blockers on the sign-off page</router-link>
        </div>
      </template>
      <div v-for="(e, i) in queueBlocking" v-else :key="e.id" class="brow">
        <span class="num" :class="{ solid: i === 0 }">{{ i + 1 }}</span>
        <div class="issue"><div class="it">{{ e.title }}</div><span class="tag">{{ e.id }} · {{ e.severity }}</span></div>
        <div class="val"><span v-if="e.value" class="tabular">{{ money(e.value) }}</span><div class="muted">impact</div></div>
        <div class="own"><div :class="e.owner ? 'on' : 'on warn'">{{ owner(e.owner) }}</div><div class="muted">Due {{ fmtDate(e.dueAt) }}</div></div>
        <a-button size="small" @click="router.push(exceptionLink(e.id, runId))">Open</a-button>
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
            <template v-for="m in c.value" :key="m.currency"> · <span class="tabular">{{ money(m) }}</span></template>
          </span>
          <span :class="['imp', impact(c.state)[1]]">{{ impact(c.state)[0] }}</span>
        </div>
        <div v-if="!data.controlAreas.length" class="muted pad">No control reported records or exceptions for this run.</div>
        <div class="legend">
          <div><b class="fail">Blocks</b> Must be resolved, or covered by an approved qualification, before the run can be signed.</div>
          <div><b class="warn">Needs approval</b> Owned exception. The control owner must approve the resolution or accepted difference.</div>
        </div>
      </div>
      <div class="card sg">
        <div class="ch"><span class="eyebrow">SAFEGUARDING</span><span class="muted">By currency · no netting</span>
          <router-link :to="{ path: '/safeguarding', query: q }">Full calculation</router-link></div>
        <div v-for="p in data.safeguarding" :key="p.currency" class="cur">
          <div class="crow"><b>{{ p.currency }}</b>
            <span>
              <a-tag v-if="p.stale" color="orange" title="An adjustment changed after processing; reprocess the run.">Stale</a-tag>
              <StatusPill :status="sgStatus(p.state)" :label="human(p.state)" />
            </span>
          </div>
          <div class="muted">Obligation {{ money(p.obligation) }} · Eligible {{ money(p.eligibleBalance) }}</div>
          <div class="muted">Result <span :class="{ fail: p.result && isNeg(p.result) }">{{ p.result ? money(p.result) : '—' }}</span><template v-if="!p.result"> (not computed: qualified)</template></div>
          <div v-if="p.exceptionCount" class="muted">{{ showing(p.exceptionIds.length, p.exceptionCount) }} open safeguarding exception(s)</div>
        </div>
        <div v-if="!data.safeguarding.length" class="muted pad">No safeguarding currencies in this run.</div>
      </div>
    </div>

    <div v-if="data.summary.length" class="card block">
      <h3>Since previous business date</h3>
      <ul class="summary">
        <li v-for="(s, i) in data.summary" :key="i">
          {{ s.text }}
          <EvidenceLink v-if="s.link" :label="s.link.id" :to="summaryLink(s.link, runId)" />
        </li>
      </ul>
    </div>

    <div class="card block">
      <h3>Evidence readiness</h3>
      <a-table :data-source="data.evidenceReadiness" :pagination="false" size="small" row-key="deliveryId"
        :columns="[
          { title: 'Source', key: 'src' }, { title: 'File', dataIndex: 'fileName' },
          { title: 'Expected by', key: 'exp' }, { title: 'Received', key: 'rec' },
          { title: 'Records', key: 'cnt', align: 'right' }, { title: 'Findings', key: 'find' }, { title: 'State', key: 'state' },
        ]">
        <template #bodyCell="{ column, record }">
          <EvidenceLink v-if="column.key === 'src'" :label="record.sourceName" :to="{ path: '/sources', query: q }" />
          <template v-else-if="column.key === 'exp'">{{ fmtDate(record.expectedBy) }}</template>
          <template v-else-if="column.key === 'rec'">{{ fmtDate(record.receivedAt) }}</template>
          <template v-else-if="column.key === 'cnt'">{{ record.acceptedRecords }} / {{ record.declaredRecordCount }}<span v-if="record.rejectedRecords" class="fail"> ({{ record.rejectedRecords }} rejected)</span></template>
          <template v-else-if="column.key === 'find'">{{ record.findingCount ?? record.findingIds.length }}</template>
          <StatusPill v-else-if="column.key === 'state'" :status="delStatus(record.state)" :label="human(record.state)" />
        </template>
      </a-table>
    </div>

    <div class="card block">
      <div class="row">
        <h3>Attention queue</h3>
        <span class="muted">{{ showing(data.attentionQueue.length, openTotal) }} · most urgent first</span>
        <router-link :to="{ path: '/exceptions', query: q }">Open the full queue</router-link>
      </div>
      <a-table :data-source="data.attentionQueue" :columns="columns" :pagination="false" size="small" row-key="id"
        :locale="{ emptyText: 'No items need attention.' }">
        <template #bodyCell="{ column, record }">
          <EvidenceLink v-if="column.key === 'id'" :label="record.id" :to="exceptionLink(record.id, runId)" />
          <template v-else-if="column.key === 'title'">
            {{ record.title }} <a-tag v-if="record.blocking" color="red">Blocking</a-tag>
          </template>
          <template v-else-if="column.key === 'control'">{{ human(record.control) }}</template>
          <StatusPill v-else-if="column.key === 'severity'" :status="sevStatus(record.severity)" :label="human(record.severity)" />
          <template v-else-if="column.key === 'status'">{{ human(record.status) }}</template>
          <template v-else-if="column.key === 'owner'">{{ owner(record.owner) }}</template>
          <template v-else-if="column.key === 'value'"><span class="tabular">{{ record.value ? money(record.value) : '—' }}</span></template>
          <template v-else-if="column.key === 'dueAt'">{{ fmtDate(record.dueAt) }}</template>
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
.gap { margin-bottom: 16px; }
.pad { padding: 12px 16px; }
.fail { color: var(--fail); }
.cmp { display: flex; flex-direction: column; gap: 4px; margin-bottom: 8px; font-size: 12px; }
.row .muted { flex: 1; }
.row { margin-bottom: 12px; }
</style>
