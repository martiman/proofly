import { describe, expect, it, vi } from 'vitest'
import { configureApi } from '../../../api/client/http'
import { staticTokenProvider } from '../../../api/client/auth'
import { decideResolution, getException } from '../../../api/services'
import { classifyFailure, decisionBody, allows, proposalKindLabel } from '../logic'

const P = 'https://proofly.example/problems/'
const prob = (status: number, slug: string, extra = {}) =>
  new Response(JSON.stringify({ type: P + slug, title: slug, status, ...extra }), { status, headers: { 'Content-Type': 'application/problem+json' } })

function setup(h: (r: Request) => Response) {
  const calls: Request[] = []
  configureApi({ baseUrl: 'http://api.test/api/v1', tokenProvider: staticTokenProvider('t'),
    fetch: vi.fn(async (i: RequestInfo | URL) => { const r = i as Request; calls.push(r.clone()); return h(r) }) as unknown as typeof fetch })
  return calls
}

describe('decide flow (mocked fetch)', () => {
  it('sends If-Match from the read ETag and the proposal version', async () => {
    const calls = setup((r) => r.method === 'GET'
      ? new Response('{"id":"F-1"}', { status: 200, headers: { 'Content-Type': 'application/json', ETag: '"v3"' } })
      : new Response('{"id":"F-1"}', { status: 200, headers: { 'Content-Type': 'application/json', ETag: '"v4"' } }))
    await getException('F-1')
    await decideResolution('F-1', 'AP-1', decisionBody('APPROVE', ' ok then ', 2))
    const post = calls[1]!
    expect(post.headers.get('If-Match')).toBe('"v3"')
    expect(post.headers.get('Idempotency-Key')).toBeTruthy()
    expect(await post.json()).toEqual({ decision: 'APPROVE', reason: 'ok then', proposalVersion: 2 })
  })
  it('maps stale proposal 409, SoD 403, validation 422', async () => {
    setup(() => prob(409, 'stale-proposal'))
    const e1 = await decideResolution('F-1', 'AP-1', decisionBody('APPROVE', 'abc', 1)).catch((e) => e)
    expect(classifyFailure(e1).kind).toBe('stale')
    setup(() => prob(403, 'separation-of-duties'))
    expect(classifyFailure(await decideResolution('F-1', 'AP-1', decisionBody('REJECT', 'abc', 1)).catch((e) => e)).kind).toBe('sod')
    setup(() => prob(422, 'validation', { errors: [{ pointer: '/reason', message: 'too short' }] }))
    const c = classifyFailure(await decideResolution('F-1', 'AP-1', decisionBody('REJECT', 'a', 1)).catch((e) => e))
    expect(c.kind).toBe('validation'); expect(c.fields.reason).toEqual(['too short'])
  })
  it('allowedActions drives visibility', () => {
    expect(allows(['ASSIGN'], 'ASSIGN')).toBe(true); expect(allows(['ASSIGN'], 'REOPEN')).toBe(false); expect(allows(undefined, 'ASSIGN')).toBe(false)
  })
})

describe('pending proposal kind', () => {
  it('labels the kind and is null for other approvals', () => {
    expect(proposalKindLabel('ACCEPTED_DIFFERENCE')).toBe('Accepted difference')
    expect(proposalKindLabel(null)).toBeNull()
  })
})

describe('proposalIdOf', () => {
  it('uses the proposal id (objectId), not the approval id', async () => {
    const { proposalIdOf } = await import('../logic')
    expect(proposalIdOf({ id: 'AP-1', objectId: 'RP-9' })).toBe('RP-9')
  })
})
