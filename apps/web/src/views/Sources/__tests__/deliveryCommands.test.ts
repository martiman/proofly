import { describe, expect, it, vi } from 'vitest'
import { configureApi } from '../../../api/client'
import { staticTokenProvider } from '../../../api/client/auth'
import { getSourceDelivery } from '../../../api/services'
import { useDeliveryCommands } from '../deliveryCommands'
import { describeCommandError } from '../commandError'

const BASE = 'http://api.test/api/v1'
const detail = { deliveryId: 'D1', allowedActions: ['RETRY_INGESTION'] }
const problem = (status: number, slug: string) =>
  new Response(JSON.stringify({ type: `https://proofly.example/problems/${slug}`, title: slug, status }), { status, headers: { 'Content-Type': 'application/problem+json' } })

function setup(post: () => Response) {
  const calls: Request[] = []
  const f = vi.fn(async (input: RequestInfo | URL) => {
    const r = (input as Request).clone(); calls.push(r)
    if (r.method === 'GET') return new Response(JSON.stringify(detail), { status: 200, headers: { 'Content-Type': 'application/json', ETag: '"v1"' } })
    return post()
  })
  configureApi({ baseUrl: BASE, fetch: f as unknown as typeof fetch, tokenProvider: staticTokenProvider('t') })
  return calls
}

describe('delivery commands', () => {
  it('retry sends If-Match from the read ETag, the reason and an idempotency key', async () => {
    const calls = setup(() => new Response(JSON.stringify(detail), { status: 202, headers: { 'Content-Type': 'application/json', ETag: '"v2"' } }))
    await getSourceDelivery('D1')
    const { retry } = useDeliveryCommands()
    await retry.run({ deliveryId: 'D1', reason: 'fixed file' })
    const post = calls.find((c) => c.method === 'POST')!
    expect(new URL(post.url).pathname).toBe('/api/v1/deliveries/D1/retry-ingestion')
    expect(post.headers.get('If-Match')).toBe('"v1"')
    expect(post.headers.get('Idempotency-Key')).toBeTruthy()
    expect(await post.json()).toEqual({ reason: 'fixed file' })
  })
  it('supersede sends the replacement and maps a 412 to a reload prompt', async () => {
    const calls = setup(() => problem(412, 'precondition-failed'))
    await getSourceDelivery('D1')
    const { supersede } = useDeliveryCommands()
    const e = await supersede.run({ deliveryId: 'D1', supersededByDeliveryId: 'D2', reason: 'corrected' }).catch((x) => x)
    expect(await calls.find((c) => c.method === 'POST')!.json()).toEqual({ supersededByDeliveryId: 'D2', reason: 'corrected' })
    expect(describeCommandError(e)).toMatchObject({ kind: 'stale' })
    expect(describeCommandError(e).message).toContain('Reload')
  })
  it('maps 422 field errors', async () => {
    setup(() => new Response(JSON.stringify({ type: 'https://proofly.example/problems/validation', title: 'validation', status: 422, errors: [{ pointer: '/reason', message: 'too short' }] }), { status: 422, headers: { 'Content-Type': 'application/problem+json' } }))
    await getSourceDelivery('D1')
    const e = await useDeliveryCommands().retry.run({ deliveryId: 'D1', reason: 'x' }).catch((x) => x)
    expect(describeCommandError(e)).toMatchObject({ kind: 'validation', fields: { reason: ['too short'] } })
  })
})
