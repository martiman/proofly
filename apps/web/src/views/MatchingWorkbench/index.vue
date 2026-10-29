<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { message } from 'ant-design-vue'
import { StatePlaceholder, StatusPill, MoneyAmount, EvidenceLink } from '../../components'
import type { Status } from '../../components/StatusPill.vue'
import { getMatchingWorkbench, listMatchGroups } from '../../api'
import type { MatchGroup, MatchCandidate, MatchingWorkbench } from '../../api'

const route = useRoute()
const router = useRouter()
const loading = ref(true)
const error = ref<string | null>(null)
const forbidden = ref(false)
const wb = ref<MatchingWorkbench | null>(null)
const all = ref<MatchGroup[]>([])
const reason = ref('')

const statusMap: Record<string, { label: string; s: Status }> = {
  MATCHED: { label: 'Matched automatically', s: 'pass' },
  UNMATCHED: { label: 'Unmatched', s: 'fail' },
  VALUE_MISMATCH: { label: 'Value mismatch', s: 'warn' },
  NOT_APPLICABLE: { label: 'Not applicable', s: 'neutral' },
}
const st = (s: string) => statusMap[s] ?? { label: s.replace(/_/g, ' ').toLowerCase(), s: 'neutral' as Status }

const group = computed(() => wb.value?.groups[0] ?? null)
const counted = computed(() => all.value.filter((g) => g.status !== 'NOT_APPLICABLE'))
const matched = computed(() => counted.value.filter((g) => g.status === 'MATCHED').length)
const rate = computed(() => (counted.value.length ? Math.round((matched.value / counted.value.length) * 100) : 0))
const open = computed(() => all.value.filter((g) => g.status !== 'MATCHED' && g.status !== 'NOT_APPLICABLE'))
const unmatchedTotals = computed(() => {
  const m = new Map<string, number>()
  for (const c of wb.value?.unmatched ?? []) m.set(c.amount.currency, (m.get(c.amount.currency) ?? 0) + Number(c.amount.amount))
  return [...m].map(([currency, n]) => ({ currency, amount: n.toFixed(2) }))
})
const fmt = (iso: string) => new Date(iso).toLocaleString('en-GB', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' })
const sum = (cs: MatchCandidate[]) => cs.reduce((a, c) => a + Number(c.amount.amount), 0).toFixed(2)
const ccy = (cs: MatchCandidate[]) => cs[0]?.amount.currency ?? ''
const material = computed(() => !!group.value?.difference && Number(group.value.difference.amount) !== 0)

async function load() {
  loading.value = true; error.value = null; forbidden.value = false
  try {
    all.value = await listMatchGroups()
    const ref_ = { transactionId: route.query.transactionId as string | undefined, exceptionId: route.query.exceptionId as string | undefined }
    const fallback = async () => {
      const first = all.value.find((g) => g.status !== 'MATCHED' && g.status !== 'NOT_APPLICABLE') ?? all.value[0]
      const id = first && [...first.left, ...first.right][0]?.transactionId
      if (!id) { wb.value = null; return }
      wb.value = await getMatchingWorkbench({ transactionId: id })
    }
    if (!ref_.transactionId && !ref_.exceptionId) await fallback()
    else {
      try { wb.value = await getMatchingWorkbench(ref_) }
      catch (e: any) {
        // exceptions without linked transactions (e.g. aggregate shortfall) fall back to the first open group
        if (e?.status === 404 && !ref_.transactionId) await fallback()
        else throw e
      }
    }
  } catch (e: any) {
    if (e?.status === 403) forbidden.value = true
    else error.value = e?.message ?? 'Failed to load matching workbench'
  } finally { loading.value = false }
}
watch(() => [route.query.transactionId, route.query.exceptionId], load, { immediate: true })

const select = (c: MatchCandidate) => router.push({ path: '/matching', query: { transactionId: c.transactionId } })
const act = (t: string) => message.info(`${t}: recorded locally (mock).`)
function propose() {
  if (material.value && !reason.value.trim()) { message.warning('A reason is required for a material manual match.'); return }
  act('Manual match proposed; second-person approval required')
  reason.value = ''
}
const rows = (g: MatchGroup) => [
  ['Reference', g.left.map((c) => c.reference).join(' / '), g.right.map((c) => c.reference).join(' / ')],
  ['Booking time', g.left.map((c) => fmt(c.timestamp)).join(' / '), g.right.map((c) => fmt(c.timestamp)).join(' / ')],
]
</script>

<template>
  <div class="ph">
    <div>
      <div class="eyebrow">Matching · Bank ↔ instruction and ledger</div>
      <h1>Matching workbench</h1>
      <p>Precision workspace for records the automatic rules could not match safely.</p>
    </div>
    <div class="acts">
      <a-button @click="act('Rule rerun on test population')">Rerun rule on test population</a-button>
      <a-button type="primary" class="dark" :disabled="!group" @click="propose">Propose manual match</a-button>
    </div>
  </div>

  <StatePlaceholder v-if="loading" state="loading" />
  <StatePlaceholder v-else-if="forbidden" state="forbidden" />
  <StatePlaceholder v-else-if="error" state="error" :message="error"><template #action><a-button @click="load">Retry</a-button></template></StatePlaceholder>
  <StatePlaceholder v-else-if="!wb || !group" state="empty" message="No match group found for this record." />
  <template v-else>
    <div class="card strip">
      <div class="cell"><div class="lbl">Source pair</div><div class="n sm">Bank ↔ ledger</div><div class="sub">Instruction and ledger</div></div>
      <div class="cell"><div class="lbl">Auto-match rate</div><div class="n">{{ rate }}%</div><div class="sub">{{ matched }} of {{ counted.length }} groups</div></div>
      <div class="cell"><div class="lbl">Unmatched</div><div class="n failc">{{ wb.unmatched.length }}</div><div class="sub">records</div></div>
      <div class="cell">
        <div class="lbl">Unmatched value</div>
        <div v-if="!unmatchedTotals.length" class="n">-</div>
        <div v-for="t in unmatchedTotals" :key="t.currency" class="n failc"><MoneyAmount :amount="t.amount" :currency="t.currency" show-currency /></div>
      </div>
      <div class="cell"><div class="lbl">Open groups</div><div class="n warnc">{{ open.length }}</div><div class="sub">unmatched or mismatched</div></div>
    </div>

    <div class="wrow">
      <div class="card pop">
        <div class="ch"><span class="lbl">Unmatched population</span><span class="aside">{{ wb.unmatched.length }}</span></div>
        <a-empty v-if="!wb.unmatched.length" description="No unmatched records." />
        <div v-for="c in wb.unmatched" :key="c.transactionId + c.label" class="item" :class="{ sel: c.transactionId === wb.focusTransactionId }" @click="select(c)">
          <span class="mono">{{ c.transactionId }}</span>
          <MoneyAmount :amount="c.amount.amount" :currency="c.amount.currency" />
        </div>
      </div>

      <div class="stack">
        <div class="card">
          <div class="ch"><span class="lbl">Side by side</span><span class="mono did">{{ group.id }}</span><StatusPill :status="st(group.status).s" :label="st(group.status).label" /></div>
          <div class="two">
            <div class="side">
              <div class="lbl">Instruction + ledger</div>
              <a-empty v-if="!group.left.length" description="No records." />
              <div v-for="c in group.left" :key="c.label" class="rec">
                <EvidenceLink :label="c.transactionId" :to="`/transactions?id=${c.transactionId}`" /> <span class="muted">{{ c.label }}</span>
                <div><MoneyAmount :amount="c.amount.amount" :currency="c.amount.currency" /></div>
                <small class="muted">{{ c.reference }} · {{ fmt(c.timestamp) }}</small>
              </div>
            </div>
            <div class="side">
              <div class="lbl">Bank</div>
              <a-empty v-if="!group.right.length" description="No bank evidence found." />
              <div v-for="c in group.right" :key="c.label" class="rec">
                <EvidenceLink :label="c.transactionId" :to="`/transactions?id=${c.transactionId}`" /> <span class="muted">{{ c.label }}</span>
                <div><MoneyAmount :amount="c.amount.amount" :currency="c.amount.currency" /></div>
                <small class="muted">{{ c.reference }} · {{ fmt(c.timestamp) }}</small>
              </div>
            </div>
          </div>
          <a-table class="cmp" size="small" :pagination="false" row-key="0"
            :columns="[{ title: 'Attribute', dataIndex: 0, width: 140 }, { title: 'Instruction + ledger', dataIndex: 1 }, { title: 'Bank', dataIndex: 2 }]"
            :data-source="[...rows(group), ['Total', `${sum(group.left)} ${ccy(group.left)}`, `${sum(group.right)} ${ccy(group.right)}`]]" />
        </div>
      </div>

      <div class="stack">
        <div class="card">
          <div class="ch"><span class="lbl">Match explanation</span></div>
          <div class="kvs">
            <div class="kv"><span>Rule version</span><b>{{ group.ruleVersion }}</b></div>
            <div class="kv"><span>Tolerance</span><b>{{ group.tolerance }}</b></div>
            <div class="kv"><span>Cardinality</span><b>{{ group.left.length }} : {{ group.right.length }}{{ group.left.length > 1 || group.right.length > 1 ? ' (grouped)' : ' (one-to-one)' }}</b></div>
            <div class="kv"><span>Difference</span><b><MoneyAmount v-if="group.difference" :amount="group.difference.amount" :currency="group.difference.currency" /><template v-else>Not comparable</template></b></div>
            <div class="kv"><span>Rationale</span><b>{{ group.rationale }}</b></div>
            <div class="kv"><span>Exceptions</span><b>
              <template v-if="group.exceptionIds.length"><EvidenceLink v-for="e in group.exceptionIds" :key="e" :label="e" :to="`/exceptions/${e}`" /> </template>
              <template v-else>None</template></b></div>
          </div>
        </div>
        <div class="card">
          <div class="ch"><span class="lbl">Governed action</span></div>
          <div class="kvs">
            <label class="lbl">Reason{{ material ? ' (required)' : '' }}</label>
            <a-textarea v-model:value="reason" :rows="3" placeholder="Explain why these records belong together" />
            <p v-if="material" class="muted">Outside tolerance: needs a second-person approver. The proposer cannot approve.</p>
            <div class="btns">
              <a-button type="primary" class="dark" @click="propose">Propose manual match</a-button>
              <a-button @click="act('Marked as requiring external evidence')">Mark as requiring external evidence</a-button>
              <a-button v-if="group.exceptionIds[0]" @click="router.push(`/exceptions/${group.exceptionIds[0]}`)">Open exception {{ group.exceptionIds[0] }}</a-button>
              <a-button @click="act('Rule improvement proposed')">Propose rule improvement</a-button>
            </div>
          </div>
        </div>
      </div>
    </div>
  </template>
</template>

<style scoped>
.ph { display: flex; justify-content: space-between; align-items: flex-start; gap: 24px; margin-bottom: 16px; }
.eyebrow, .lbl { font-size: 10px; font-weight: 600; letter-spacing: 0.06em; text-transform: uppercase; color: var(--ink-3); }
h1 { margin: 6px 0 6px; font-size: 28px; font-weight: 600; line-height: 1.2; }
p { margin: 0; font-size: 13px; color: var(--ink-2); }
.acts { display: flex; gap: 8px; }
.dark { background: var(--ink); }
.card { overflow: hidden; }
.strip { display: flex; margin-bottom: 16px; }
.cell { flex: 1; padding: 14px 20px; display: flex; flex-direction: column; gap: 4px; border-right: 1px solid var(--border); }
.cell:last-child { border-right: 0; }
.n { font-size: 26px; font-weight: 500; line-height: 1.3; }
.n.sm { font-size: 22px; }
.sub, .muted, small { font-size: 11px; color: var(--ink-3); }
.warnc { color: var(--warn); } .failc { color: var(--fail); }
.wrow { display: grid; grid-template-columns: 220px minmax(0, 1fr) 340px; gap: 16px; align-items: start; }
.stack { display: flex; flex-direction: column; gap: 16px; }
.ch { display: flex; align-items: center; gap: 10px; padding: 14px 20px; }
.aside { margin-left: auto; font-size: 12px; color: var(--ink-3); }
.did { color: var(--accent); font-weight: 600; }
.item { display: flex; justify-content: space-between; padding: 12px 20px; cursor: pointer; border-top: 1px solid var(--border); font-size: 12px; }
.item:hover, .item.sel { background: var(--accent-soft); }
.mono { font-family: var(--font-mono); font-size: 12px; }
.two { display: grid; grid-template-columns: 1fr 1fr; border-top: 1px solid var(--border); }
.side { padding: 14px 20px; }
.side + .side { border-left: 1px solid var(--border); }
.rec { padding: 8px 0; font-size: 12px; }
.cmp { border-top: 1px solid var(--border); }
.cmp :deep(.ant-table-thead > tr > th) { background: var(--surface-sunk); font-size: 10px; text-transform: uppercase; letter-spacing: 0.06em; color: var(--ink-3); }
.kvs { padding: 0 20px 14px; display: flex; flex-direction: column; gap: 8px; border-top: 1px solid var(--border); padding-top: 14px; }
.kv { display: flex; gap: 12px; font-size: 12px; }
.kv span { width: 90px; flex: none; color: var(--ink-3); }
.kv b { font-weight: 500; }
.btns { display: flex; flex-direction: column; align-items: stretch; gap: 8px; margin-top: 4px; }
</style>
