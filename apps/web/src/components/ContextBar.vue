<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { SearchOutlined, BellOutlined } from '@ant-design/icons-vue'
import { useRunContext } from '../composables/useRunContext'
import { useShellStatus } from '../composables/useShellStatus'
import { resolveSearch } from '../composables/globalSearch'
import { isApiError } from '../api/client'

const router = useRouter()
const { entities, entity, legalEntityId, businessDate, businessDates, runsOfDate, run, runId, selectEntity, selectBusinessDate, selectRun, loading } = useRunContext()
const { approvals, approvalsMore, approvalsError, pendingCount } = useShellStatus()

const q = ref('')
function search() {
  const t = resolveSearch(q.value)
  if (!t) return
  router.push({ path: t.path, query: { ...t.query, ...(t.query.run || !runId.value ? {} : { run: runId.value }) } })
  q.value = ''
}
const entityOptions = computed(() => entities.value.map((e) => ({ value: e.id, label: `${e.id} · ${e.name}` })))
const dateOptions = computed(() => businessDates.value.map((d) => ({ value: d, label: d })))
const runOptions = computed(() => runsOfDate.value.map((r) => ({ value: r.id, label: `v${r.version} · ${r.state.replace(/_/g, ' ').toLowerCase()}` })))
const objectLabel = (s: string) => s.charAt(0) + s.slice(1).toLowerCase().replace(/_/g, ' ')
const approvalsMsg = computed(() => (isApiError(approvalsError.value) && approvalsError.value.status === 403 ? 'No approval inbox for your role' : 'Could not load approvals'))
</script>
<template>
  <header class="cb">
    <label class="sel"><span class="k">Entity</span>
      <a-select size="small" variant="borderless" :value="legalEntityId ?? undefined" :options="entityOptions" :loading="loading" placeholder="—" class="s" :dropdown-match-select-width="false" @change="(v: string) => selectEntity(v)" />
    </label>
    <label class="sel"><span class="k">Business date</span>
      <a-select size="small" variant="borderless" :value="businessDate ?? undefined" :options="dateOptions" placeholder="—" class="s" @change="(v: string) => selectBusinessDate(v)" />
    </label>
    <div class="sel"><span class="k">Currency</span>
      <span class="v mono">{{ entity ? entity.currencies.join(' · ') : '—' }}</span>
    </div>
    <label class="sel"><span class="k">Run</span>
      <a-select size="small" variant="borderless" :value="run?.id" :options="runOptions" placeholder="—" class="s mono" :dropdown-match-select-width="false" @change="(v: string) => selectRun(v)" />
    </label>
    <div class="gap" />
    <label class="search"><SearchOutlined class="ch" />
      <input v-model="q" aria-label="Global search" placeholder="Search F-019, TRF-004, BNK-12…" @keydown.enter="search" />
    </label>
    <a-popover trigger="click" placement="bottomRight">
      <template #content>
        <div class="inbox">
          <div v-if="approvalsError" class="muted">{{ approvalsMsg }}</div>
          <div v-else-if="!approvals.length" class="muted">No approvals waiting for you</div>
          <router-link v-for="a in approvals" :key="a.id" :to="{ path: '/settings', query: { run: runId ?? undefined } }" class="item">
            <b>{{ objectLabel(a.objectType) }}</b> <span class="mono">{{ a.objectId }}</span>
            <div class="muted">proposed by {{ a.proposer.name }}</div>
          </router-link>
          <div v-if="approvalsMore" class="muted">More items in Configuration and Access</div>
        </div>
      </template>
      <button class="bell" aria-label="Notifications"><a-badge :count="pendingCount" :overflow-count="99" size="small"><BellOutlined class="bi" /></a-badge></button>
    </a-popover>
  </header>
</template>
<style scoped>
.cb { display: flex; align-items: center; gap: 8px; padding: 12px 32px; background: var(--surface); border-bottom: 1px solid var(--border); }
.sel { display: flex; align-items: center; gap: 4px; padding: 2px 4px 2px 10px; border: 1px solid var(--border); border-radius: 6px; margin: 0; }
.k { color: var(--ink-3); font-size: 11px; } .v { color: var(--ink); font-size: 12px; font-weight: 500; padding: 0 6px; }
.s { min-width: 90px; font-size: 12px; font-weight: 500; }
.gap { flex: 1; }
.search { width: 340px; display: flex; align-items: center; gap: 8px; padding: 7px 10px; background: var(--surface-sunk); border-radius: 6px; margin: 0; }
.ch { color: var(--ink-3); font-size: 12px; }
.search input { flex: 1; border: 0; background: none; outline: 0; font: 12px var(--font-sans); color: var(--ink); }
.bell { width: 32px; height: 32px; border: 0; background: none; border-radius: 6px; cursor: pointer; }
.bi { font-size: 16px; color: var(--ink-2); }
.inbox { width: 280px; display: flex; flex-direction: column; gap: 8px; }
.item { color: var(--ink); text-decoration: none; padding: 4px 0; border-bottom: 1px solid var(--border); }
.muted { color: var(--ink-3); font-size: 12px; }
</style>
