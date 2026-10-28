import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { getMe, getRun, listLegalEntities, listRuns } from '../api/services'
import type { Schema } from '../api/services'

type Me = Schema<'Me'>
type LegalEntity = Schema<'LegalEntity'>
type Run = Schema<'Run'>
type Permission = Schema<'Permission'>

const me = ref<Me | null>(null)
const entities = ref<LegalEntity[]>([])
const runs = ref<Run[]>([])
const entityId = ref<string | null>(null)
const loading = ref(false)
const error = ref<unknown>(null)
const runsError = ref<unknown>(null)
let loaded: Promise<void> | null = null

/** Newest business date first, then highest version first. Plain ISO date / integer ordering, no financial logic. */
export const byNewest = (a: Run, b: Run) =>
  a.businessDate === b.businessDate ? b.version - a.version : a.businessDate < b.businessDate ? 1 : -1

async function loadRuns(eid: string) {
  runsError.value = null
  try {
    runs.value = (await listRuns({ legalEntityId: eid, includeSuperseded: true, limit: 100 })).items
  } catch (err) {
    runs.value = []
    runsError.value = err
  }
}

async function load() {
  loading.value = true
  error.value = null
  try {
    const [m, e] = await Promise.all([getMe(), listLegalEntities()])
    me.value = m
    entities.value = e
    const eid = entityId.value && e.some((x) => x.id === entityId.value) ? entityId.value : e[0]?.id ?? null
    entityId.value = eid
    if (eid) await loadRuns(eid)
  } catch (err) {
    error.value = err
  } finally {
    loading.value = false
  }
}

/** Latest non-superseded run, else the newest run. */
export function pickDefaultRun(list: Run[]): Run | null {
  const sorted = [...list].sort(byNewest)
  return sorted.find((r) => r.state !== 'SUPERSEDED') ?? sorted[0] ?? null
}

/**
 * Shared application context: the signed-in user, the legal entities in scope and the selected run.
 * The selected run id lives in the route query (`?run=<id>`) so links are shareable and survive navigation (the router
 * guard carries it); without it the latest run of the selected entity is used. Screens call `useRunContext()`.
 * Owned by the shell (ContextBar); keep this public shape stable.
 */
export function useRunContext() {
  const route = useRoute()
  const router = useRouter()
  loaded ??= load()

  const queryRun = computed(() => (typeof route.query.run === 'string' && route.query.run ? route.query.run : null))
  const runId = computed<string | null>(() => queryRun.value ?? pickDefaultRun(runs.value)?.id ?? null)
  const run = computed(() => runs.value.find((r) => r.id === runId.value) ?? null)
  const legalEntityId = computed(() => run.value?.legalEntityId ?? entityId.value ?? entities.value[0]?.id ?? null)
  const businessDate = computed(() => run.value?.businessDate ?? null)
  const entity = computed(() => entities.value.find((x) => x.id === legalEntityId.value) ?? null)
  /** Currencies the selected legal entity holds client funds in (LegalEntity.currencies). */
  const currencies = computed<string[]>(() => entity.value?.currencies ?? [])
  const businessDates = computed(() => [...new Set(runs.value.map((r) => r.businessDate))].sort().reverse())
  const runsOfDate = computed(() => runs.value.filter((r) => r.businessDate === businessDate.value).sort(byNewest))
  /** No run exists for the selected entity (distinct from an error). */
  const noRun = computed(() => !loading.value && !error.value && !runsError.value && !!entityId.value && runs.value.length === 0)

  const can = (p: Permission): boolean => me.value?.permissions.includes(p) ?? false

  function selectRun(id: string) {
    router.replace({ query: { ...route.query, run: id } })
  }
  function selectBusinessDate(date: string) {
    const r = pickDefaultRun(runs.value.filter((x) => x.businessDate === date))
    if (r) selectRun(r.id)
  }
  async function selectEntity(id: string) {
    entityId.value = id
    await loadRuns(id)
    const r = pickDefaultRun(runs.value)
    const { run: _drop, ...rest } = route.query
    await router.replace({ query: r ? { ...rest, run: r.id } : rest })
  }
  async function reload() {
    loaded = load()
    await loaded
  }

  // A `?run=` of another entity (shared link): find its entity and load that entity's runs.
  watch([queryRun, () => runs.value, loading], async ([q]) => {
    if (!q || loading.value || runs.value.some((r) => r.id === q)) return
    try {
      const r = await getRun(q)
      if (r.legalEntityId !== entityId.value && entities.value.some((e) => e.id === r.legalEntityId)) {
        entityId.value = r.legalEntityId
        await loadRuns(r.legalEntityId)
      } else if (!runs.value.some((x) => x.id === r.id)) {
        runs.value = [...runs.value, r]
      }
    } catch { /* unknown run id: screens show their own not-found state */ }
  }, { immediate: true })

  return {
    me, entities, runs, runId, run, legalEntityId, businessDate, loading, error, selectRun, reload,
    entity, currencies, businessDates, runsOfDate, noRun, runsError, can, selectBusinessDate, selectEntity,
  }
}
