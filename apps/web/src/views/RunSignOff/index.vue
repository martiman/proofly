<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { message } from 'ant-design-vue'
import { PageHeader, StatePlaceholder, StatusPill, MoneyAmount, EvidenceLink } from '../../components'
import { getRunSignOff, signOffRun, returnRun, generateEvidencePack } from '../../api'
import type { RunSignOff, ControlState, ControlArea } from '../../api/types'

const data = ref<RunSignOff | null>(null)
const loading = ref(true)
const error = ref<string | null>(null)
const forbidden = ref(false)
const attested = ref(false)
const busy = ref(false)
const returnOpen = ref(false)
const returnReason = ref('')

async function load() {
  loading.value = true
  error.value = null
  try {
    data.value = await getRunSignOff()
  } catch (e: any) {
    if (e?.status === 403) forbidden.value = true
    else error.value = e?.message ?? 'Failed to load sign-off'
  } finally {
    loading.value = false
  }
}
onMounted(load)

const stateLabel = (s: string) => s.replace(/_/g, ' ').toLowerCase().replace(/^./, (c) => c.toUpperCase())
const ctlStatus = (s: ControlState) => (s === 'PASSED' ? 'pass' : s === 'QUALIFIED' ? 'warn' : s === 'REVIEW_REQUIRED' ? 'warn' : 'fail')
const signStatus = computed(() => {
  const s = data.value?.run.signOffState
  return s === 'SIGNED_OFF' ? 'pass' : s === 'READY_FOR_REVIEW' || s === 'APPROVAL_PENDING' ? 'info' : s === 'INVALIDATED' || s === 'RETURNED' ? 'fail' : 'warn'
})
const locked = computed(() => !data.value?.canSignOff || data.value.run.signOffState === 'SIGNED_OFF')
const resultRoute = (a: ControlArea) =>
  a === 'matching' ? '/matching' : a === 'safeguarding' ? '/safeguarding' : a === 'data_completeness' ? '/sources' : '/exceptions'
const sevStatus = (s: string) => (s === 'CRITICAL' || s === 'HIGH' ? 'fail' : s === 'MEDIUM' ? 'warn' : 'neutral')

async function doSignOff() {
  busy.value = true
  try {
    await signOffRun(data.value!.run.id, attested.value)
    message.success('Run signed off')
    attested.value = false
    await load()
  } catch (e: any) {
    message.error(e?.message ?? 'Sign-off failed')
  } finally {
    busy.value = false
  }
}
async function doReturn() {
  if (!returnReason.value.trim()) return message.warning('Reason is required')
  busy.value = true
  try {
    await returnRun(data.value!.run.id, returnReason.value)
    returnOpen.value = false
    returnReason.value = ''
    message.success('Run returned for investigation')
    await load()
  } catch (e: any) {
    message.error(e?.message ?? 'Return failed')
  } finally {
    busy.value = false
  }
}
async function doPack() {
  busy.value = true
  try {
    const p = await generateEvidencePack(data.value!.run.id)
    message.success(`Evidence pack ${p.id} generated`)
    await load()
  } catch (e: any) {
    message.error(e?.message ?? 'Evidence pack failed')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <PageHeader title="Run sign-off" subtitle="An accountable decision point. Every control links to its result and cannot be marked passed by hand.">
    <template #actions>
      <a-button :disabled="!data || busy" @click="returnOpen = true">Return for investigation</a-button>
      <a-button :disabled="!data || busy" @click="doPack">Generate evidence pack</a-button>
      <a-button type="primary" :disabled="!data || locked || !attested" :loading="busy" @click="doSignOff">Sign off</a-button>
    </template>
  </PageHeader>

  <StatePlaceholder v-if="loading" state="loading" />
  <StatePlaceholder v-else-if="forbidden" state="forbidden" />
  <StatePlaceholder v-else-if="error" state="error" :message="error"><template #action><a-button @click="load">Retry</a-button></template></StatePlaceholder>
  <StatePlaceholder v-else-if="!data" state="empty" />
  <template v-else>
    <div class="banner" :class="{ ok: !data.blockers.length }">
      <div class="bt">
        <b>{{ data.blockers.length ? 'Preparation incomplete' : 'Preparation complete' }}</b>
        <span v-if="data.blockers.length">{{ data.blockers.length }} mandatory control(s) have not passed. Sign-off unlocks when each is passed or covered by an approved qualification.</span>
      </div>
      <StatusPill :status="signStatus" :label="stateLabel(data.run.signOffState)" />
      <div class="kv"><i>Legal entity</i>{{ data.run.legalEntityId }}</div>
      <div class="kv"><i>Business date</i>{{ data.run.businessDate }}</div>
      <div class="kv"><i>Run</i>v{{ data.run.version }} · {{ data.run.rulesetVersion }}</div>
      <div class="kv"><i>Prepared</i>{{ data.run.startedAt }}</div>
    </div>

    <div class="cols">
    <div class="left">
    <div class="card block">
      <h3>Mandatory control checklist</h3>
      <a-table :data-source="data.checklist" row-key="id" :pagination="false" size="small">
        <a-table-column title="Control" data-index="label" />
        <a-table-column title="State" key="state">
          <template #default="{ record }"><StatusPill :status="ctlStatus(record.state)" :label="stateLabel(record.state)" /></template>
        </a-table-column>
        <a-table-column title="Result" data-index="note" />
        <a-table-column title="" key="link">
          <template #default="{ record }"><EvidenceLink label="Open result" :to="resultRoute(record.control)" /></template>
        </a-table-column>
      </a-table>
    </div>

    <div class="card block">
      <h3>Open matters</h3>
      <a-table :data-source="data.openMatters" row-key="id" :pagination="false" size="small">
        <template #emptyText>No unresolved matters.</template>
        <a-table-column title="Exception" key="id">
          <template #default="{ record }"><EvidenceLink :label="record.id" :to="`/exceptions/${record.id}`" /></template>
        </a-table-column>
        <a-table-column title="Title" data-index="title" />
        <a-table-column title="Severity" key="sev">
          <template #default="{ record }"><StatusPill :status="sevStatus(record.severity)" :label="record.severity" /></template>
        </a-table-column>
        <a-table-column title="Status" key="status">
          <template #default="{ record }">{{ stateLabel(record.status) }}</template>
        </a-table-column>
        <a-table-column title="Value" key="value" align="right">
          <template #default="{ record }"><MoneyAmount v-if="record.value" :amount="record.value.amount" :currency="record.value.currency" /><span v-else>-</span></template>
        </a-table-column>
      </a-table>
    </div>

    </div>
    <div class="right">
    <div class="card block">
      <h3>Changes since preparation</h3>
      <ul><li v-for="c in data.changesSincePreparation" :key="c">{{ c }}</li></ul>
    </div>

    <div class="card block">
      <h3>Attestation</h3>
      <p>{{ data.attestationText }}</p>
      <a-descriptions :column="2" size="small">
        <a-descriptions-item label="Run">{{ data.run.legalEntityId }} · {{ data.run.businessDate }} · v{{ data.run.version }}</a-descriptions-item>
        <a-descriptions-item label="Evidence pack">{{ data.evidencePackId ?? 'Not generated' }}</a-descriptions-item>
      </a-descriptions>
      <a-checkbox v-model:checked="attested" :disabled="locked">I attest to the statement above</a-checkbox>
      <a-alert v-if="data.blockers.length" type="warning" show-icon class="gap" :message="`Sign-off locked: ${data.blockers.join('; ')}`" />
    </div>
    </div>
    </div>

    <a-modal v-model:open="returnOpen" title="Return for investigation" ok-text="Return" :confirm-loading="busy" @ok="doReturn">
      <a-textarea v-model:value="returnReason" :rows="3" placeholder="Reason" />
    </a-modal>
  </template>
</template>

<style scoped>
.block { padding: 16px; margin-bottom: 16px; }
h3 { margin: 0 0 12px; font-size: 14px; font-weight: 600; }
.banner { display: flex; gap: 20px; align-items: center; background: #FBEFD9; border: 1px solid #EBD3A4; border-radius: 8px; padding: 14px 16px; margin-bottom: 16px; }
.banner.ok { background: #E3F2E8; border-color: #B7DCC2; }
.bt { flex: 1; display: flex; flex-direction: column; gap: 2px; font-size: 13px; }
.bt b { font-size: 15px; }
.kv { display: flex; flex-direction: column; font-size: 13px; font-weight: 500; }
.kv i { font-style: normal; font-size: 11px; color: var(--ink-3); text-transform: uppercase; }
.cols { display: grid; grid-template-columns: 2fr 1fr; gap: 16px; align-items: start; }
.left, .right { min-width: 0; }
.gap { margin-top: 12px; }
</style>
