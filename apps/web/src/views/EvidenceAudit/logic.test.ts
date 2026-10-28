import { describe, expect, it, vi } from 'vitest'
import { configureApi, staticTokenProvider } from '../../api/client'
import { generateEvidencePack } from '../../api/services'
import { pollEvidencePack, verificationChecklist } from './logic'

describe('evidence pack', () => {
  it('202 then polls until GENERATED', async () => {
    const seq = ['GENERATING', 'GENERATING', 'GENERATED']
    const calls: string[] = []
    const f = vi.fn(async (i: RequestInfo | URL) => {
      const r = i as Request; calls.push(r.method)
      const status = r.method === 'POST' ? 'GENERATING' : seq.shift()
      return new Response(JSON.stringify({ id: 'EP-1', runId: 'R1', generatedAt: null, status, contents: [] }), { status: r.method === 'POST' ? 202 : 200, headers: { 'Content-Type': 'application/json' } })
    })
    configureApi({ baseUrl: 'http://api.test/api/v1', fetch: f as unknown as typeof fetch, tokenProvider: staticTokenProvider('t') })
    expect((await generateEvidencePack('R1')).status).toBe('GENERATING')
    const last = await pollEvidencePack('R1', { intervalMs: 0 })
    expect(last.status).toBe('GENERATED')
    expect(calls).toEqual(['POST', 'GET', 'GET', 'GET'])
  })
  it('chain verification is not available without contents', () => {
    const c = verificationChecklist({ id: 'EP', runId: 'R', generatedAt: null, status: 'GENERATED', contents: ['a.csv'], sha256: null, sizeBytes: null })
    expect(c.find((x) => x.label.startsWith('Audit chain'))).toMatchObject({ state: 'na', note: 'Not available' })
  })
})
