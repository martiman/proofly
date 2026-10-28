import type { components } from '../generated/schema'

type Problem = components['schemas']['Problem']
export type FieldError = NonNullable<Problem['errors']>[number]

export const PROBLEM_BASE = 'https://proofly.example/problems/'

/** Problem slugs of the contract (docs/integrations/api/README.md, Errors). Unknown slugs are tolerated. */
export type ProblemSlug =
  | 'unauthenticated' | 'forbidden' | 'out-of-scope' | 'separation-of-duties' | 'not-found'
  | 'invalid-state' | 'record-already-used' | 'run-superseded' | 'stale-signoff' | 'stale-proposal'
  | 'idempotency-in-progress' | 'duplicate' | 'precondition-failed' | 'validation' | 'precondition-required'
  | 'internal' | 'network' | 'unparseable'

/**
 * RFC 9457 problem details as thrown by every API call. `status` 0 means the request never got a response
 * (network failure, type `network`). Never swallow it: surface `title`/`detail`/`errors` to the user.
 */
export class ApiError extends Error {
  readonly status: number
  readonly type: string
  readonly title: string
  readonly detail?: string
  readonly requestId?: string
  readonly errors: FieldError[]
  readonly problem?: Problem

  constructor(init: { status: number; type: string; title: string; detail?: string; requestId?: string; errors?: FieldError[]; problem?: Problem }) {
    super(init.detail ? `${init.title}: ${init.detail}` : init.title)
    this.name = 'ApiError'
    this.status = init.status
    this.type = init.type
    this.title = init.title
    this.detail = init.detail
    this.requestId = init.requestId
    this.errors = init.errors ?? []
    this.problem = init.problem
  }

  /** Last path segment of `type`, e.g. `separation-of-duties`. */
  get slug(): string {
    const t = this.type
    return t.slice(t.lastIndexOf('/') + 1)
  }
}

export const isApiError = (e: unknown): e is ApiError => e instanceof ApiError
const slugIs = (e: unknown, ...slugs: string[]): boolean => isApiError(e) && slugs.includes(e.slug)

export const isUnauthenticated = (e: unknown) => isApiError(e) && e.status === 401
export const isForbidden = (e: unknown) => isApiError(e) && e.status === 403
export const isSeparationOfDuties = (e: unknown) => slugIs(e, 'separation-of-duties')
export const isOutOfScope = (e: unknown) => slugIs(e, 'out-of-scope')
export const isNotFound = (e: unknown) => isApiError(e) && e.status === 404
/** The resource changed under the caller: 412 stale If-Match, or 409 stale sign-off / stale proposal. Re-read and retry. */
export const isStale = (e: unknown) => isApiError(e) && (e.status === 412 || slugIs(e, 'stale-signoff', 'stale-proposal'))
export const isPreconditionRequired = (e: unknown) => isApiError(e) && e.status === 428
export const isConflict = (e: unknown) => isApiError(e) && e.status === 409
export const isRunSuperseded = (e: unknown) => slugIs(e, 'run-superseded')
export const isIdempotencyInProgress = (e: unknown) => slugIs(e, 'idempotency-in-progress')
export const isDuplicate = (e: unknown) => slugIs(e, 'duplicate')
export const isValidation = (e: unknown) => isApiError(e) && e.status === 422
export const isNetworkError = (e: unknown) => isApiError(e) && e.status === 0
export const isServerError = (e: unknown) => isApiError(e) && e.status >= 500

/** Field-level messages keyed by JSON pointer / parameter name (without the leading `/`). */
export function fieldErrors(e: unknown): Record<string, string[]> {
  const out: Record<string, string[]> = {}
  if (!isApiError(e)) return out
  for (const f of e.errors) (out[f.pointer.replace(/^\//, '')] ??= []).push(f.message)
  return out
}

/** Build an ApiError from a non-2xx response. Accepts problem+json; anything else becomes a generic problem. */
export async function errorFromResponse(res: Response): Promise<ApiError> {
  const headerId = res.headers.get('X-Request-Id') ?? undefined
  let text = ''
  try { text = await res.text() } catch { /* body unreadable */ }
  let body: Partial<Problem> | undefined
  try {
    const parsed: unknown = text ? JSON.parse(text) : undefined
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) body = parsed as Partial<Problem>
  } catch { /* not JSON */ }
  const isProblem = !!body && typeof body.type === 'string' && typeof body.title === 'string'
  if (isProblem) {
    return new ApiError({
      status: typeof body!.status === 'number' ? body!.status : res.status,
      type: body!.type!, title: body!.title!, detail: body!.detail,
      requestId: body!.requestId ?? headerId,
      errors: Array.isArray(body!.errors) ? body!.errors : [], problem: body as Problem,
    })
  }
  return new ApiError({
    status: res.status, type: PROBLEM_BASE + 'unparseable',
    title: res.statusText || `HTTP ${res.status}`,
    detail: text ? text.slice(0, 500) : undefined, requestId: headerId,
  })
}

export const networkError = (cause: unknown): ApiError =>
  new ApiError({ status: 0, type: PROBLEM_BASE + 'network', title: 'Network error', detail: cause instanceof Error ? cause.message : String(cause) })
