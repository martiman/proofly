import { computed, onScopeDispose, ref, shallowRef, watch, type WatchSource } from 'vue'
import { isApiError } from '../client/errors'
import type { PageFetcher, PageInfo } from '../client/paging'
import type { PlaceholderState, ResourceState } from './useResource'

export interface UsePagedOptions {
  limit?: number
  /** Ask for the exact `page.total` on the first page of each query. */
  includeTotal?: boolean
  /** `replace` shows one page at a time (next/prev through a cursor stack); `append` keeps loading more (infinite list). */
  mode?: 'replace' | 'append'
}

/**
 * Cursor pagination for tables and lists. `deps` (filters) reset to the first page when they change.
 * `total` is only set when `includeTotal` is on and the server returned it (never derived locally).
 */
export function usePaged<T>(fetcher: PageFetcher<T>, deps: WatchSource | WatchSource[] = [], opts: UsePagedOptions = {}) {
  const items = shallowRef<T[]>([])
  const info = ref<PageInfo>()
  const loading = ref(false)
  const error = ref<unknown>(null)
  const loaded = ref(false)
  const stack = ref<(string | undefined)[]>([undefined]) // cursor used for each visited page; index = pageIndex
  const pageIndex = ref(0)
  const total = ref<number>()
  let ctl: AbortController | undefined
  const append = opts.mode === 'append'

  async function load(cursor: string | undefined, first: boolean) {
    ctl?.abort()
    const c = (ctl = new AbortController())
    loading.value = true; error.value = null
    try {
      const p = await fetcher({ limit: opts.limit, cursor, includeTotal: first ? opts.includeTotal : undefined }, c.signal)
      if (c.signal.aborted) return false
      items.value = append && !first ? [...items.value, ...p.items] : p.items
      info.value = p.page
      if (first) total.value = p.page.total
      loaded.value = true
      return true
    } catch (e) {
      if (c.signal.aborted) return false
      error.value = e
      if (first) { items.value = []; info.value = undefined; total.value = undefined }
      return false
    } finally {
      if (ctl === c) loading.value = false
    }
  }

  async function reload() { stack.value = [undefined]; pageIndex.value = 0; await load(undefined, true) }
  async function next() {
    const cur = info.value?.nextCursor
    if (!info.value?.hasMore || !cur) return
    if (await load(cur, false)) { stack.value = [...stack.value.slice(0, pageIndex.value + 1), cur]; pageIndex.value++ }
  }
  async function prev() {
    if (append || pageIndex.value === 0) return
    if (await load(stack.value[pageIndex.value - 1], false)) pageIndex.value--
  }

  const sources = Array.isArray(deps) ? deps : [deps]
  watch(sources, () => void reload(), { immediate: true })
  onScopeDispose(() => ctl?.abort())

  const state = computed<ResourceState>(() => {
    if (error.value && !items.value.length) return isApiError(error.value) && error.value.status === 403 ? 'forbidden' : 'error'
    if (!loaded.value) return 'loading'
    return items.value.length ? 'ready' : 'empty'
  })
  const placeholder = computed<PlaceholderState | null>(() => (state.value === 'ready' ? null : state.value))
  return {
    items, info, total, loading, error, state, placeholder, pageIndex,
    hasNext: computed(() => !!info.value?.hasMore && !!info.value.nextCursor),
    hasPrev: computed(() => !append && pageIndex.value > 0),
    next, prev, reload, loadMore: next,
  }
}
