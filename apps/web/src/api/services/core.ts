import type { components, operations } from '../generated/schema'
import { getApiClient, uuid } from '../client'

/** Schema type by name, e.g. `Schema<'Run'>`. */
export type Schema<N extends keyof components['schemas']> = components['schemas'][N]

type OpId = keyof operations
type SuccessCode = 200 | 201 | 202 | 207
type SuccessKeys<K extends OpId> = SuccessCode & keyof operations[K]['responses']

/** JSON body of the successful response(s) of an operation. */
export type ApiResult<K extends OpId> = {
  [C in SuccessKeys<K>]: operations[K]['responses'][C] extends { content: { 'application/json': infer B } } ? B : never
}[SuccessKeys<K>]
/** Query parameters of an operation. */
export type ApiQuery<K extends OpId> = operations[K]['parameters'] extends { query?: infer Q } ? NonNullable<Q> : never
/** JSON request body of an operation. */
export type ApiBody<K extends OpId> = operations[K] extends { requestBody: { content: { 'application/json': infer B } } } ? B : never

export interface ReadOptions { signal?: AbortSignal }
export interface CommandOptions extends ReadOptions {
  /** Override the ETag used for If-Match. Default: the ETag last seen for the target resource. */
  ifMatch?: string
  /** Reuse the key of a previous attempt of the same user action (explicit retry). Default: a fresh uuid. */
  idempotencyKey?: string
}

/** Header parameters the generated types require on commands. Missing values are omitted so the server answers 428/422. */
export interface CommandHeaders { 'If-Match': string; 'Idempotency-Key': string }

interface Result<T> { data?: T; error?: unknown; response: Response }

/** Read call: the client throws `ApiError` for every non-2xx, so `data` is the success body. */
export async function read<T>(call: Promise<Result<T>>): Promise<T> {
  return (await call).data as T
}

/**
 * Command call. Resolves If-Match from `resourceKey` (the GET path of the target, e.g. `/exceptions/F-019`) unless given,
 * generates an Idempotency-Key unless given, and stores the ETag of the response under the same key (or, for create
 * commands, under `createdKey(result)`).
 */
export async function command<T>(resourceKey: string | null, opts: CommandOptions | undefined, call: (headers: CommandHeaders) => Promise<Result<T>>, createdKey?: (data: T) => string): Promise<T> {
  const c = getApiClient()
  const headers = {
    'If-Match': (opts?.ifMatch ?? (resourceKey ? c.etags.get(resourceKey) : undefined)) as string,
    'Idempotency-Key': opts?.idempotencyKey ?? uuid(),
  }
  const r = await call(headers)
  const key = resourceKey ?? (createdKey && r.data !== undefined ? createdKey(r.data) : null)
  if (key) c.etags.set(key, r.response.headers.get('ETag'))
  return r.data as T
}

/** Remember ETags carried by list items so bulk/inline commands need no extra read. */
export function rememberItemEtags<T extends { etag?: string | null }>(items: T[], key: (item: T) => string): void {
  const c = getApiClient()
  for (const i of items) c.etags.set(key(i), i.etag)
}

export const api = () => getApiClient().raw
