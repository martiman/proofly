import { computed, ref } from 'vue'
import { uuid } from '../client/uuid'
import type { CommandOptions } from '../services/core'

/**
 * One user action = one Idempotency-Key. The key is created on the first attempt and reused when the user retries the
 * same input after a failure (network error, 409 idempotency-in-progress, 5xx), so the server replays instead of
 * executing twice. It is dropped after success, and when the input changes (a different body with the same key is a 422).
 *
 *   const assign = useCommand((body: AssignBody, o) => assignException(id, body, o))
 *   await assign.run({ ownerId, reason })
 */
export function useCommand<A, R>(fn: (arg: A, options: CommandOptions) => Promise<R>) {
  const pending = ref(false)
  const error = ref<unknown>(null)
  const result = ref<R>()
  let key: string | undefined
  let lastInput: string | undefined
  let inflight: Promise<R> | undefined

  function run(arg: A, options: Omit<CommandOptions, 'idempotencyKey'> = {}): Promise<R> {
    if (inflight) return inflight // submit lock: a re-entrant call joins the running request, no second request
    inflight = exec(arg, options).finally(() => { inflight = undefined })
    return inflight
  }
  async function exec(arg: A, options: Omit<CommandOptions, 'idempotencyKey'>): Promise<R> {
    const input = JSON.stringify(arg) ?? ''
    if (key === undefined || input !== lastInput) key = uuid()
    lastInput = input
    pending.value = true; error.value = null
    try {
      const r = await fn(arg, { ...options, idempotencyKey: key })
      result.value = r
      key = undefined; lastInput = undefined // success: the next invocation is a new user action
      return r
    } catch (e) {
      error.value = e // key kept for an explicit retry
      throw e
    } finally {
      pending.value = false
    }
  }
  /** Forget the key (user cancelled the action or started a different one). */
  const reset = () => { key = undefined; lastInput = undefined; error.value = null }
  return { run, reset, pending, error, result, idempotencyKey: computed(() => key) }
}
