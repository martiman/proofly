import { computed, onScopeDispose, ref, shallowRef, watch, type ComputedRef, type Ref, type WatchSource } from 'vue'
import { isApiError } from '../client/errors'

/** `ready` plus the four states `StatePlaceholder` renders. */
export type ResourceState = 'loading' | 'ready' | 'empty' | 'error' | 'forbidden'
export type PlaceholderState = Exclude<ResourceState, 'ready'>

export interface UseResourceOptions<T> {
  /** Decides `empty` for a successful result. Default: arrays and `items`-pages with no items. */
  isEmpty?: (data: T) => boolean
  /** Run immediately (default true). */
  immediate?: boolean
}

export interface Resource<T> {
  data: Ref<T | undefined>
  error: Ref<unknown>
  state: ComputedRef<ResourceState>
  /** Non-null when the page should show `<StatePlaceholder :state=...>` instead of content. */
  placeholder: ComputedRef<PlaceholderState | null>
  /** Message for the placeholder (problem title/detail), when in error/forbidden. */
  message: ComputedRef<string | undefined>
  loading: Ref<boolean>
  reload: () => Promise<void>
}

const defaultIsEmpty = (d: unknown): boolean =>
  Array.isArray(d) ? d.length === 0 : !!d && typeof d === 'object' && Array.isArray((d as { items?: unknown }).items) && (d as { items: unknown[] }).items.length === 0

function make<T>(run: (signal: AbortSignal) => Promise<T> | null, opts: UseResourceOptions<T>): Resource<T> & { start: () => Promise<void> } {
  const data = shallowRef<T>()
  const error = ref<unknown>(null)
  const loading = ref(false)
  let ctl: AbortController | undefined
  const isEmpty = opts.isEmpty ?? defaultIsEmpty

  async function start() {
    ctl?.abort()
    const c = (ctl = new AbortController())
    const p = run(c.signal)
    if (!p) { loading.value = false; data.value = undefined; error.value = null; return }
    loading.value = true; error.value = null
    try {
      const d = await p
      if (c.signal.aborted) return
      data.value = d
    } catch (e) {
      if (c.signal.aborted) return
      data.value = undefined; error.value = e
    } finally {
      if (ctl === c) loading.value = false
    }
  }
  onScopeDispose(() => ctl?.abort())

  const state = computed<ResourceState>(() => {
    if (loading.value) return 'loading'
    if (error.value) return isApiError(error.value) && (error.value.status === 403) ? 'forbidden' : 'error'
    if (data.value === undefined) return 'loading'
    return isEmpty(data.value) ? 'empty' : 'ready'
  })
  const placeholder = computed(() => (state.value === 'ready' ? null : state.value))
  const message = computed(() => {
    const e = error.value
    if (!e) return undefined
    return isApiError(e) ? (e.detail ? `${e.title}: ${e.detail}` : e.title) + (e.requestId ? ` (request ${e.requestId})` : '') : e instanceof Error ? e.message : String(e)
  })
  return { data, error, state, placeholder, message, loading, reload: start, start }
}

/** Load once (and on `reload()`): `useAsync((signal) => getMe({ signal }))`. */
export function useAsync<T>(fn: (signal: AbortSignal) => Promise<T>, opts: UseResourceOptions<T> = {}): Resource<T> {
  const r = make<T>(fn, opts)
  if (opts.immediate !== false) void r.start()
  return r
}

/**
 * Reactive resource: refetches when `source()` changes, cancels the previous request, and does nothing while the source
 * is null (for example the run id is not known yet).
 * `useResource(() => runId.value, (id, signal) => getControlRoom(id, { signal }))`
 */
export function useResource<S, T>(source: WatchSource<S | null | undefined>, fetcher: (arg: S, signal: AbortSignal) => Promise<T>, opts: UseResourceOptions<T> = {}): Resource<T> {
  const r = make<T>((signal) => {
    const arg = typeof source === 'function' ? (source as () => S | null | undefined)() : (source as Ref<S | null | undefined>).value
    return arg == null ? null : fetcher(arg, signal)
  }, opts)
  watch(source, () => void r.start(), { immediate: opts.immediate !== false, deep: false })
  return r
}
