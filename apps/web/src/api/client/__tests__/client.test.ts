import { describe, expect, it, vi } from 'vitest'
import { createApiClient, configureApi, getApiClient } from '../http'
import { ApiError, isNotFound, isSeparationOfDuties, isStale, isValidation, fieldErrors } from '../errors'
import { iteratePages } from '../paging'
import { formatMoney } from '../format'
import { staticTokenProvider } from '../auth'
import * as svc from '../../services'

const BASE = 'http://api.test/api/v1'
const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), { status: init.status ?? 200, headers: { 'Content-Type': 'application/json', ...(init.headers as object) } })
const problem = (status: number, slug: string, extra: object = {}) =>
  new Response(JSON.stringify({ type: `https://proofly.example/problems/${slug}`, title: slug, status, requestId: 'rid-1', ...extra }),
    { status, headers: { 'Content-Type': 'application/problem+json' } })

function setup(handler: (req: Request) => Response | Promise<Response>, extra: Parameters<typeof createApiClient>[0] = {}) {
  const calls: Request[] = []
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => { const r = input as Request; calls.push(r.clone()); return handler(r) })
  configureApi({ baseUrl: BASE, fetch: fetchMock as unknown as typeof fetch, tokenProvider: staticTokenProvider('tok'), ...extra })
  return { calls, fetchMock, client: getApiClient() }
}

describe('request basics', () => {
  it('sends bearer token and a uuid X-Request-Id on every request', async () => {
    const { calls } = setup(() => json({ id: 'u' }))
    await svc.getMe(); await svc.getMe()
    expect(calls[0]!.headers.get('Authorization')).toBe('Bearer tok')
    const ids = calls.map((c) => c.headers.get('X-Request-Id'))
    expect(ids[0]).toMatch(/^[0-9a-f-]{36}$/)
    expect(ids[0]).not.toBe(ids[1])
  })
  it('passes query parameters and the abort signal', async () => {
    const ctl = new AbortController()
    const { calls } = setup(() => json({ items: [], page: { hasMore: false, nextCursor: null } }))
    await svc.listExceptions('R1', { limit: 5, severity: ['HIGH'], includeTotal: true }, { signal: ctl.signal })
    const u = new URL(calls[0]!.url)
    expect(u.pathname).toBe('/api/v1/runs/R1/exceptions')
    expect(u.searchParams.get('limit')).toBe('5'); expect(u.searchParams.get('includeTotal')).toBe('true')
    ctl.abort()
    setup(async (r) => {
      await new Promise((_, rej) => { if (r.signal.aborted) rej(new DOMException('x', 'AbortError')); else r.signal.addEventListener('abort', () => rej(new DOMException('x', 'AbortError'))) })
      return json({})
    })
    const c2 = new AbortController()
    const p = svc.getMe({ signal: c2.signal }); c2.abort()
    await expect(p).rejects.toMatchObject({ name: 'AbortError' })
  })
})

describe('problem+json', () => {
  it('parses into ApiError with helpers', async () => {
    setup(() => problem(404, 'not-found', { detail: 'no such run' }))
    const e = await svc.getRun('x').catch((x) => x)
    expect(e).toBeInstanceOf(ApiError)
    expect(e).toMatchObject({ status: 404, title: 'not-found', detail: 'no such run', requestId: 'rid-1', slug: 'not-found' })
    expect(isNotFound(e)).toBe(true)
  })
  it('classifies separation of duties, stale and validation', async () => {
    setup(() => problem(403, 'separation-of-duties'))
    expect(isSeparationOfDuties(await svc.getMe().catch((x) => x))).toBe(true)
    setup(() => problem(412, 'precondition-failed'))
    expect(isStale(await svc.getMe().catch((x) => x))).toBe(true)
    setup(() => problem(409, 'stale-proposal'))
    expect(isStale(await svc.getMe().catch((x) => x))).toBe(true)
    setup(() => problem(422, 'validation', { errors: [{ pointer: '/reason', message: 'too short' }] }))
    const e = await svc.getMe().catch((x) => x)
    expect(isValidation(e)).toBe(true)
    expect(fieldErrors(e)).toEqual({ reason: ['too short'] })
  })
  it('turns non-problem bodies and network failures into ApiError', async () => {
    setup(() => new Response('<html>bad gateway</html>', { status: 502, headers: { 'X-Request-Id': 'p1' } }))
    expect(await svc.getMe().catch((x) => x)).toMatchObject({ status: 502, type: expect.stringContaining('unparseable'), requestId: 'p1' })
    setup(() => { throw new TypeError('failed to fetch') })
    expect(await svc.getMe().catch((x) => x)).toMatchObject({ status: 0, slug: 'network' })
  })
  it('calls onUnauthorized on 401 and retries once after a successful refresh', async () => {
    const onUnauthorized = vi.fn()
    setup(() => problem(401, 'unauthenticated'), { onUnauthorized })
    await expect(svc.getMe()).rejects.toMatchObject({ status: 401 })
    expect(onUnauthorized).toHaveBeenCalledOnce()

    let token = 'old'
    let n = 0
    const { calls } = setup((r) => (r.headers.get('Authorization') === 'Bearer new' ? json({ ok: 1 }) : (n++, problem(401, 'unauthenticated'))),
      { tokenProvider: { getToken: () => token, refresh: async () => { token = 'new'; return true } } })
    await svc.getMe()
    expect(calls.map((c) => c.headers.get('Authorization'))).toEqual(['Bearer old', 'Bearer new']); expect(n).toBe(1)
  })
})

describe('ETag and If-Match', () => {
  const exc = { id: 'F-1', status: 'OPEN' }
  it('captures ETag on GET and sends it as If-Match on a mutation, then stores the new one', async () => {
    const { calls, client } = setup((r) => {
      if (r.method === 'GET') return json(exc, { headers: { ETag: '"v1"' } })
      return json({ ...exc, status: 'ASSIGNED' }, { headers: { ETag: '"v2"' } })
    })
    await svc.getException('F-1')
    expect(client.etags.get('/exceptions/F-1')).toBe('"v1"')
    await svc.assignException('F-1', { ownerId: 'u1' })
    expect(calls[1]!.headers.get('If-Match')).toBe('"v1"')
    expect(client.etags.get('/exceptions/F-1')).toBe('"v2"')
    await svc.addExceptionNote('F-1', { text: 'x' } as never)
    expect(calls[2]!.headers.get('If-Match')).toBe('"v2"')
  })
  it('explicit ifMatch wins; unknown ETag omits the header so the server answers 428', async () => {
    const { calls } = setup(() => json(exc))
    await svc.assignException('F-9', { ownerId: null }, { ifMatch: '"zz"' })
    await svc.assignException('F-8', { ownerId: null })
    expect(calls[0]!.headers.get('If-Match')).toBe('"zz"')
    expect(calls[1]!.headers.has('If-Match')).toBe(false)
  })
  it('revalidates with If-None-Match and serves the cached body on 304', async () => {
    const { calls } = setup((r) => (r.headers.get('If-None-Match') === '"v1"' ? new Response(null, { status: 304 }) : json(exc, { headers: { ETag: '"v1"' } })))
    expect(await svc.getException('F-1')).toEqual(exc)
    expect(await svc.getException('F-1')).toEqual(exc)
    expect(calls[1]!.headers.get('If-None-Match')).toBe('"v1"')
  })
  it('remembers item etags from list pages for inline commands', async () => {
    const { calls } = setup((r) => (r.method === 'GET'
      ? json({ items: [{ id: 'F-2', etag: '"e2"' }], page: { hasMore: false, nextCursor: null } }) : json(exc)))
    await svc.listExceptions('R1')
    await svc.assignException('F-2', { ownerId: 'u' })
    expect(calls[1]!.headers.get('If-Match')).toBe('"e2"')
  })
})

describe('Idempotency-Key', () => {
  it('is a fresh uuid per command and reusable on explicit retry', async () => {
    const { calls } = setup(() => json({ id: 'F-1' }))
    await svc.assignException('F-1', { ownerId: 'a' }, { ifMatch: '"1"' })
    await svc.assignException('F-1', { ownerId: 'a' }, { ifMatch: '"1"' })
    const [a, b] = calls.map((c) => c.headers.get('Idempotency-Key'))
    expect(a).toMatch(/^[0-9a-f-]{36}$/); expect(a).not.toBe(b)
    await svc.assignException('F-1', { ownerId: 'a' }, { ifMatch: '"1"', idempotencyKey: a! })
    expect(calls[2]!.headers.get('Idempotency-Key')).toBe(a)
  })
})

describe('pagination', () => {
  it('follows nextCursor and requests includeTotal only on the first page', async () => {
    const pages: Record<string, unknown> = {
      '': { items: [1, 2], page: { hasMore: true, nextCursor: 'c1', total: 5 } },
      c1: { items: [3, 4], page: { hasMore: true, nextCursor: 'c2' } },
      c2: { items: [5], page: { hasMore: false, nextCursor: null } },
    }
    const { calls } = setup((r) => json(pages[new URL(r.url).searchParams.get('cursor') ?? '']))
    const seen: number[] = []
    let total: number | undefined
    for await (const p of iteratePages((q, signal) => svc.listRuns({ ...q }, { signal }) as never, { limit: 2, includeTotal: true })) {
      seen.push(...(p.items as number[])); total ??= p.page.total
    }
    expect(seen).toEqual([1, 2, 3, 4, 5]); expect(total).toBe(5)
    expect(calls.map((c) => new URL(c.url).searchParams.get('includeTotal'))).toEqual(['true', null, null])
  })
})

describe('formatMoney (display only)', () => {
  it('formats decimal strings without float conversion and keeps precision', () => {
    expect(formatMoney('1234.50', 'EUR', 'en-GB')).toBe('€1,234.50')
    expect(formatMoney('-50.00', 'GBP', 'en-GB')).toBe('-£50.00')
    expect(formatMoney('9007199254740993.01', 'USD', 'en-US')).toBe('$9,007,199,254,740,993.01')
    expect(formatMoney('0.123456', 'EUR', 'en-GB')).toBe('€0.123456')
    expect(formatMoney('1234', 'JPY', 'en-GB')).toBe('JP¥1,234')
  })
  it('rejects invalid input instead of repairing it', () => {
    expect(() => formatMoney('1e3', 'EUR')).toThrow(RangeError)
    expect(() => formatMoney('12.50', 'eur')).toThrow(RangeError)
  })
})
