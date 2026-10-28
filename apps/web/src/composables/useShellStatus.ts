import { computed, watch, ref } from 'vue'
import { getExceptionsSummary, listApprovals } from '../api/services'
import type { Schema } from '../api/services'
import { useRunContext } from './useRunContext'

const exceptionCount = ref<number | null>(null)
const approvals = ref<Schema<'Approval'>[]>([])
const approvalsMore = ref(false)
const approvalsError = ref<unknown>(null)
let started = false

/** Shell counters: exception total of the current run (server summary) and approvals awaiting the caller. */
export function useShellStatus() {
  const { runId, me } = useRunContext()
  if (!started) {
    started = true
    watch(runId, async (id) => {
      exceptionCount.value = null
      if (!id) return
      try {
        const s = await getExceptionsSummary(id)
        if (runId.value === id) exceptionCount.value = s.total
      } catch { exceptionCount.value = null }
    }, { immediate: true })
    watch(me, () => void reloadApprovals(), { immediate: true })
  }
  const pendingCount = computed(() => approvals.value.length)
  return { exceptionCount, approvals, approvalsMore, approvalsError, pendingCount, reloadApprovals }
}

export async function reloadApprovals(): Promise<void> {
  approvalsError.value = null
  try {
    const page = await listApprovals({ mine: true, limit: 20 })
    approvals.value = page.items.filter((a) => a.status === 'PENDING')
    approvalsMore.value = page.page.hasMore
  } catch (e) {
    approvals.value = []
    approvalsError.value = e
  }
}
