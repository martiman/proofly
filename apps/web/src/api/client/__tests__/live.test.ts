/**
 * Contract smoke test against the REAL API (scripts/dev-backend.sh). Skipped unless PROOFLY_LIVE_BASE_URL and
 * PROOFLY_LIVE_TOKEN are set:  PROOFLY_LIVE_BASE_URL=http://127.0.0.1:5080/api/v1 PROOFLY_LIVE_TOKEN=... npm run test:live
 */
import { beforeAll, describe, expect, it } from 'vitest'
import { configureApi, getApiClient } from '../http'
import { staticTokenProvider } from '../auth'
import { isNotFound } from '../errors'
import { iterateItems } from '../paging'
import * as svc from '../../services'

const base = process.env.PROOFLY_LIVE_BASE_URL
const token = process.env.PROOFLY_LIVE_TOKEN

describe.skipIf(!base || !token)('live API', () => {
  let runId = ''
  beforeAll(() => configureApi({ baseUrl: base, tokenProvider: staticTokenProvider(token!) }))

  it('listRuns + getRun (ETag) + getControlRoom', async () => {
    const runs = await svc.listRuns({ limit: 5, includeTotal: true })
    expect(runs.items.length).toBeGreaterThan(0)
    expect(runs.page.total).toBeGreaterThan(0)
    runId = runs.items[0]!.id
    const run = await svc.getRun(runId)
    expect(run.id).toBe(runId)
    expect(getApiClient().etags.get(`/runs/${runId}`)).toBeTruthy()
    const room = await svc.getControlRoom(runId)
    expect(room.controlAreas.length).toBeGreaterThan(0)
    // money stays a decimal string with currency
    const money = room.controlAreas.flatMap((a) => a.value)[0]
    if (money) expect(money.amount).toMatch(/^-?\d+(\.\d{1,6})?$/)
  })

  it('listExceptions pages by cursor', async () => {
    const p1 = await svc.listExceptions(runId, { limit: 10, includeTotal: true })
    expect(p1.items.length).toBe(10)
    expect(p1.page.hasMore).toBe(true)
    const p2 = await svc.listExceptions(runId, { limit: 10, cursor: p1.page.nextCursor! })
    expect(p2.items[0]!.id).not.toBe(p1.items[0]!.id)
    let n = 0
    for await (const _ of iterateItems((q, signal) => svc.listExceptions(runId, q, { signal }), { limit: 50 })) n++
    expect(n).toBe(p1.page.total)
  })

  it('getException ETag round trip: If-Match is sent from the cache', async () => {
    const list = await svc.listExceptions(runId, { limit: 1 })
    const id = list.items[0]!.id
    const e = await svc.getException(id)
    const etag = getApiClient().etags.get(`/exceptions/${id}`)
    expect(e.id).toBe(id); expect(etag).toBeTruthy()
    // a second read is revalidated; a stale If-Match is rejected with 412
    expect((await svc.getException(id)).id).toBe(id)
    const stale = await svc.addExceptionNote(id, { text: 'live test' } as never, { ifMatch: '"stale"' }).catch((x) => x)
    expect(stale).toMatchObject({ status: 412 })
  })

  it('unknown run is a problem+json 404', async () => {
    const e = await svc.getRun('RUN-DOES-NOT-EXIST').catch((x) => x)
    expect(isNotFound(e)).toBe(true)
    expect(e).toMatchObject({ status: 404, slug: 'not-found' })
    expect(e.requestId).toBeTruthy()
  })
})
