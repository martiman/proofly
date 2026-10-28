import { describe, expect, it, vi } from 'vitest'
import { configureApi } from '../../api/client'
import { staticTokenProvider } from '../../api/client'
import { proposeManualMatch, decideManualMatch } from '../../api/services'
import { buildProposeBody, describeCommandError } from './logic'

const BASE = 'http://api.test/api/v1'
const problem = (status: number, slug: string) => new Response(JSON.stringify({ type: `https://proofly.example/problems/${slug}`, title: slug, status }), { status, headers: { 'Content-Type': 'application/problem+json' } })
function setup(h: (r: Request) => Response) {
  const calls: Request[] = []
  configureApi({ baseUrl: BASE, fetch: vi.fn(async (i: RequestInfo | URL) => { const r = i as Request; calls.push(r.clone()); return h(r) }) as unknown as typeof fetch, tokenProvider: staticTokenProvider('t') })
  return calls
}
describe('manual match commands', () => {
  it('builds propose body by side and posts with idempotency key', async () => {
    const calls = setup(() => new Response(JSON.stringify({ id: 'MG-1' }), { status: 201, headers: { 'Content-Type': 'application/json', ETag: '"1"' } }))
    const body = buildProposeBody([{ transactionId: 'TRF-1', side: 'LEFT' }, { transactionId: 'BNK-1', side: 'RIGHT' }] as never, ' because ')
    expect(body).toEqual({ leftTransactionIds: ['TRF-1'], rightTransactionIds: ['BNK-1'], reason: 'because' })
    await proposeManualMatch('R1', body)
    expect(calls[0]!.headers.get('Idempotency-Key')).toBeTruthy()
    expect(new URL(calls[0]!.url).pathname).toBe('/api/v1/runs/R1/match-groups')
  })
  it('decide sends the group etag in If-Match and maps errors', async () => {
    const calls = setup(() => problem(403, 'separation-of-duties'))
    await expect(decideManualMatch('R1', 'MG-1', { decision: 'APPROVE', reason: 'ok', proposalVersion: 1 }, { ifMatch: '"7"' })).rejects.toMatchObject({ status: 403 })
    expect(calls[0]!.headers.get('If-Match')).toBe('"7"')
    const err = await decideManualMatch('R1', 'MG-1', { decision: 'APPROVE', reason: 'ok' }, { ifMatch: '"7"' }).catch((e) => e)
    expect(describeCommandError(err).kind).toBe('separation')
    setup(() => problem(409, 'record-already-used'))
    expect(describeCommandError(await proposeManualMatch('R1', { leftTransactionIds: ['TRF-1'], rightTransactionIds: [], reason: 'x' }).catch((e) => e)).kind).toBe('used')
    setup(() => problem(412, 'precondition-failed'))
    expect(describeCommandError(await decideManualMatch('R1', 'MG-1', { decision: 'REJECT', reason: 'x' }, { ifMatch: '"1"' }).catch((e) => e)).kind).toBe('stale')
  })
})
