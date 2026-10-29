<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { getSettings } from '../../api'
import type { SettingsData } from '../../api/types'
import { StatePlaceholder, StatusPill } from '../../components'

const data = ref<SettingsData | null>(null)
const state = ref<'loading' | 'error' | 'forbidden' | 'ready'>('loading')
const errMsg = ref('')
const tab = ref('users')

async function load() {
  state.value = 'loading'
  try {
    data.value = await getSettings()
    state.value = 'ready'
  } catch (e: any) {
    errMsg.value = e?.message ?? ''
    state.value = e?.status === 403 ? 'forbidden' : 'error'
  }
}
onMounted(load)

const label = (s: string) => s.charAt(0) + s.slice(1).toLowerCase().replace(/_/g, ' ')
const sev = (s: string) => (s === 'CRITICAL' ? 'error' : s === 'HIGH' ? 'warning' : 'neutral') as any

const sourceCols = [
  { title: 'Source', dataIndex: 'sourceName', key: 'sourceName' },
  { title: 'Schema version', dataIndex: 'schemaVersion', key: 'schemaVersion' },
  { title: 'Expected by (local)', dataIndex: 'expectedByLocalTime', key: 'expectedByLocalTime' },
  { title: 'Mandatory', dataIndex: 'mandatory', key: 'mandatory' },
]
const ruleCols = [
  { title: 'Rule', dataIndex: 'ruleId', key: 'ruleId' },
  { title: 'Control', dataIndex: 'control', key: 'control' },
  { title: 'Version', dataIndex: 'version', key: 'version' },
  { title: 'Description', dataIndex: 'description', key: 'description' },
  { title: 'Tolerance', dataIndex: 'tolerance', key: 'tolerance' },
  { title: 'Severity', dataIndex: 'severity', key: 'severity' },
  { title: 'Effective from', dataIndex: 'effectiveFrom', key: 'effectiveFrom' },
]
const userCols = [
  { title: 'Name', dataIndex: 'name', key: 'name' },
  { title: 'Email', dataIndex: 'email', key: 'email' },
  { title: 'Roles', dataIndex: 'roles', key: 'roles' },
  { title: 'Legal-entity scope', dataIndex: 'legalEntityIds', key: 'legalEntityIds' },
]
</script>

<template>
  <div class="ph">
    <div>
      <div class="eyebrow">Configuration and Access<template v-if="data"> · {{ data.legalEntity.id }}</template></div>
      <h1>Configuration &amp; access</h1>
      <p>Controlled administration of sources, control parameters, users and approval responsibilities.</p>
    </div>
    <a-tooltip title="oMVP: configuration changes are controlled file-based changes.">
      <a-button type="primary" disabled>Create draft change</a-button>
    </a-tooltip>
  </div>

  <StatePlaceholder v-if="state === 'loading'" state="loading" />
  <StatePlaceholder v-else-if="state === 'forbidden'" state="forbidden" />
  <StatePlaceholder v-else-if="state === 'error'" state="error" :message="errMsg || undefined">
    <template #action><a-button @click="load">Retry</a-button></template>
  </StatePlaceholder>
  <StatePlaceholder v-else-if="!data" state="empty" />

  <div v-else class="cfg">
    <a-tabs v-model:activeKey="tab">
            <a-tab-pane key="sources" tab="Sources">
        <a-table class="card" :columns="sourceCols" :data-source="data.sources" row-key="sourceName" :pagination="false" size="small">
          <template #bodyCell="{ column, record }">
            <StatusPill
              v-if="column.key === 'mandatory'"
              :label="record.mandatory ? 'Mandatory' : 'Optional'"
              :status="record.mandatory ? 'info' : 'neutral'"
            />
          </template>
        </a-table>
      </a-tab-pane>
      <a-tab-pane key="rules" tab="Control parameters">
        <a-table class="card" :columns="ruleCols" :data-source="data.rules" row-key="ruleId" :pagination="false" size="small">
          <template #bodyCell="{ column, record }">
            <template v-if="column.key === 'control'">{{ label(record.control) }}</template>
            <StatusPill v-else-if="column.key === 'severity'" :label="label(record.severity)" :status="sev(record.severity)" />
          </template>
        </a-table>
        <a-descriptions title="Policies" bordered size="small" :column="1" class="card" style="margin-top: 16px">
          <a-descriptions-item label="Freshness">{{ data.freshnessPolicy }}</a-descriptions-item>
          <a-descriptions-item v-for="p in data.approvalPolicies" :key="p.name" :label="`Approval: ${p.name}`">
            {{ p.description }}
          </a-descriptions-item>
        </a-descriptions>
      </a-tab-pane>
      <a-tab-pane key="users" tab="Users & roles">
        <a-table class="card" :columns="userCols" :data-source="data.users" row-key="id" :pagination="false" size="small">
          <template #bodyCell="{ column, record }">
            <template v-if="column.key === 'roles'">
              <StatusPill v-for="r in record.roles" :key="r" :label="label(r)" status="info" style="margin-right: 4px" />
            </template>
            <template v-else-if="column.key === 'legalEntityIds'">{{ record.legalEntityIds.join(', ') }}</template>
          </template>
        </a-table>
      </a-tab-pane>
      <a-tab-pane key="history" tab="Change history">
        <a-empty description="No change records in the oMVP. Versions are managed as controlled configuration files." />
      </a-tab-pane>
    </a-tabs>
  </div>
</template>

<style scoped>
.ph { display: flex; justify-content: space-between; align-items: flex-start; gap: 24px; margin-bottom: 16px; }
.eyebrow { font-size: 10px; font-weight: 600; letter-spacing: 0.06em; text-transform: uppercase; color: var(--ink-3); }
h1 { margin: 6px 0 6px; font-size: 28px; font-weight: 600; line-height: 1.2; }
p { margin: 0; font-size: 13px; color: var(--ink-2); }
.card { overflow: hidden; }
.cfg :deep(.ant-table-thead > tr > th) { background: var(--surface-sunk); font-size: 10px; font-weight: 600; letter-spacing: 0.06em; text-transform: uppercase; color: var(--ink-3); }
.cfg :deep(.ant-table-cell) { font-size: 12px; }
.cfg :deep(.ant-tabs-tab) { font-size: 13px; }
.cfg :deep(.ant-tabs-tab-active .ant-tabs-tab-btn) { font-weight: 600; color: var(--ink) !important; }
.cfg :deep(.ant-tabs-ink-bar) { background: var(--ink); }
.cfg :deep(.ant-btn-primary) { background: var(--ink); }
</style>
