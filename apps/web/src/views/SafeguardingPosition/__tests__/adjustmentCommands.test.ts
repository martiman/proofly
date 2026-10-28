import { describe, expect, it, vi } from 'vitest'
import { configureApi } from '../../../api/client'
import { staticTokenProvider } from '../../../api/client/auth'
import { useAdjustmentCommands, isDecimal } from '../adjustmentCommands'
import { describeCommandError } from '../commandError'

const adj: any = { id: 'ADJ-1', etag: '"e7"', approval: { proposalVersion: 3 } }
function setup(res: () => Response) {
  const calls: Request[] = []
  const f = vi.fn(async (i: RequestInfo | URL) => { calls.push((i as Request).clone()); return res() })
  configureApi({ baseUrl: 'http://api.test/api/v1', fetch: f as unknown as typeof fetch, tokenProvider: staticTokenProvider('t') })
  return calls
}
const ok = (s: number) => new Response('{}', { status: s, headers: { 'Content-Type': 'application/json', ETag: '"n"' } })
const prob = (s: number, slug: string) => new Response(JSON.stringify({ type: `https://proofly.example/problems/${slug}`, title: slug, status: s }), { status: s, headers: { 'Content-Type': 'application/problem+json' } })

describe('adjustment commands', () => {
  it('propose sends signed Money, evidenceRefs and expiresOn', async () => {
    const calls = setup(() => ok(201))
    await useAdjustmentCommands(() => 'R1').propose.run({ currency: 'EUR', amount: '-50.00', reason: 'r', evidenceRefs: ['E1'], expiresOn: '2026-11-30' })
    expect(new URL(calls[0]!.url).pathname).toBe('/api/v1/runs/R1/safeguarding/adjustments')
    expect(await calls[0]!.json()).toEqual({ currency: 'EUR', amount: { amount: '-50.00', currency: 'EUR' }, reason: 'r', evidenceRefs: ['E1'], expiresOn: '2026-11-30' })
  })
  it('decide uses the adjustment etag as If-Match and the proposal version', async () => {
    const calls = setup(() => ok(200))
    await useAdjustmentCommands(() => 'R1').decide.run({ adjustment: adj, decision: 'APPROVE', reason: 'ok' })
    expect(calls[0]!.headers.get('If-Match')).toBe('"e7"')
    expect(await calls[0]!.json()).toEqual({ decision: 'APPROVE', reason: 'ok', proposalVersion: 3 })
  })
  it('shows the separation-of-duties 403 and stale 412', async () => {
    setup(() => prob(403, 'separation-of-duties'))
    const e = await useAdjustmentCommands(() => 'R1').decide.run({ adjustment: adj, decision: 'APPROVE', reason: 'ok' }).catch((x: unknown) => x)
    expect(describeCommandError(e)).toMatchObject({ kind: 'forbidden' })
    setup(() => prob(412, 'precondition-failed'))
    const e2 = await useAdjustmentCommands(() => 'R1').decide.run({ adjustment: adj, decision: 'REJECT', reason: 'no' }).catch((x: unknown) => x)
    expect(describeCommandError(e2).kind).toBe('stale')
  })
  it('validates decimals', () => { expect(isDecimal('-1.50')).toBe(true); expect(isDecimal('1,5')).toBe(false) })
})
