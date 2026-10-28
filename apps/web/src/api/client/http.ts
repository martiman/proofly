import createClient from 'openapi-fetch'
import type { paths } from '../generated/schema'
import { devTokenProvider, type TokenProvider } from './auth'
import { ApiError, errorFromResponse, networkError } from './errors'
import { EtagCache } from './etag'
import { uuid } from './uuid'

export const DEFAULT_BASE_URL = '/api/v1'

export interface UnauthorizedContext { request: Request; error: ApiError }

export interface ApiClientOptions {
  /** Default `VITE_API_BASE_URL` or `/api/v1`. Relative values are resolved against `location.origin` when there is one. */
  baseUrl?: string
  tokenProvider?: TokenProvider
  /** Called on every 401 that is not recovered by `tokenProvider.refresh()`. SEAM: production redirects to the IdP login here. */
  onUnauthorized?: (ctx: UnauthorizedContext) => void | Promise<void>
  fetch?: typeof fetch
  etags?: EtagCache
  requestId?: () => string
}

export interface ApiClient {
  /** openapi-fetch client typed from the generated `paths`. Errors are thrown as `ApiError`, never returned. */
  readonly raw: ReturnType<typeof createClient<paths>>
  readonly etags: EtagCache
  readonly baseUrl: string
  /** Path of a request relative to the API base: the key under which its ETag is cached. */
  resourceKey(url: string): string
}

function resolveBase(baseUrl: string): string {
  if (/^https?:\/\//i.test(baseUrl)) return baseUrl.replace(/\/+$/, '')
  const origin = globalThis.location?.origin
  return (origin ? new URL(baseUrl, origin).toString() : baseUrl).replace(/\/+$/, '')
}

export function createApiClient(opts: ApiClientOptions = {}): ApiClient {
  const baseUrl = resolveBase(opts.baseUrl ?? (import.meta.env?.VITE_API_BASE_URL as string | undefined) ?? DEFAULT_BASE_URL)
  const tokens = opts.tokenProvider ?? devTokenProvider()
  const etags = opts.etags ?? new EtagCache()
  const doFetch: typeof fetch = opts.fetch ?? ((input, init) => globalThis.fetch(input, init))
  const newRequestId = opts.requestId ?? uuid
  const resourceKey = (url: string) => {
    const path = url.startsWith(baseUrl) ? url.slice(baseUrl.length) : new URL(url, 'http://x').pathname
    return path.split('?')[0]!
  }

  async function send(req: Request): Promise<Response> {
    const token = await tokens.getToken()
    if (token) req.headers.set('Authorization', `Bearer ${token}`)
    if (!req.headers.has('X-Request-Id')) req.headers.set('X-Request-Id', newRequestId())
    if (!req.headers.has('Accept')) req.headers.set('Accept', 'application/json, application/problem+json')
    const cached = req.method === 'GET' ? etags.getBody(req.url) : undefined
    if (cached && !req.headers.has('If-None-Match')) req.headers.set('If-None-Match', cached.etag)
    try {
      return await doFetch(req)
    } catch (e) {
      if (req.signal.aborted || (e instanceof DOMException && e.name === 'AbortError')) throw e
      throw networkError(e)
    }
  }

  async function apiFetch(request: Request): Promise<Response> {
    const retryable = tokens.refresh ? request.clone() : undefined
    let res = await send(request)
    if (res.status === 401) {
      const error = await errorFromResponse(res.clone())
      if (tokens.refresh && retryable && (await tokens.refresh())) res = await send(retryable)
      if (res.status === 401) {
        await opts.onUnauthorized?.({ request, error })
        throw error
      }
    }
    if (res.status === 304) {
      const hit = etags.getBody(request.url)
      if (!hit) throw new ApiError({ status: 304, type: 'about:blank#unexpected-304', title: 'Unexpected 304 Not Modified', requestId: res.headers.get('X-Request-Id') ?? undefined })
      const h = new Headers(res.headers)
      h.set('Content-Type', 'application/json'); h.set('ETag', hit.etag)
      return new Response(hit.body, { status: 200, headers: h })
    }
    if (!res.ok) throw await errorFromResponse(res)
    const etag = res.headers.get('ETag')
    if (request.method === 'GET' && etag) {
      etags.set(resourceKey(request.url), etag)
      if ((res.headers.get('Content-Type') ?? '').includes('json')) etags.setBody(request.url, etag, await res.clone().text())
    }
    return res
  }

  const raw = createClient<paths>({ baseUrl, fetch: apiFetch })
  return { raw, etags, baseUrl, resourceKey }
}

let current: { client: ApiClient; options: ApiClientOptions } | undefined
let overrides: ApiClientOptions = {}

/** Configure the shared client (token provider, 401 hook, base URL). Call once at startup, before the first request. */
export function configureApi(options: ApiClientOptions): void {
  overrides = { ...overrides, ...options }
  current = undefined
}

/** The shared client used by the service layer. */
export function getApiClient(): ApiClient {
  current ??= { client: createApiClient(overrides), options: overrides }
  return current.client
}
