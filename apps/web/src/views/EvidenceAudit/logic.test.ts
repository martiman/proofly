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
  const base = { id: 'EP', runId: 'R', generatedAt: null, status: 'GENERATED' as const, contents: ['a.csv'], sha256: null, sizeBytes: null }
  it('chain and manifest are not available when null', () => {
    const c = verificationChecklist({ ...base, manifest: null, auditChain: null })
    expect(c.find((x) => x.label.startsWith('Audit chain'))).toMatchObject({ state: 'na', note: 'Not available' })
    expect(c.find((x) => x.label.startsWith('Manifest'))).toMatchObject({ state: 'na' })
  })
  it('shows real manifest and chain results', () => {
    const manifest = { schemaVersion: '1', resultHash: 'abc', files: [{ path: 'a.csv', sha256: 'x', sizeBytes: 1 }], generatedBy: null, generatedAt: '2026-01-01T00:00:00Z' }
    const ok = verificationChecklist({ ...base, manifest, auditChain: { valid: true, eventsChecked: 12, firstBreak: null } })
    expect(ok.find((x) => x.label.startsWith('Audit chain'))).toMatchObject({ state: 'pass', note: expect.stringContaining('12') })
    expect(ok.find((x) => x.label.startsWith('Manifest'))).toMatchObject({ state: 'pass' })
    const bad = verificationChecklist({ ...base, manifest, auditChain: { valid: false, eventsChecked: 5, firstBreak: { chainId: 'C', chainSeq: 3, eventId: 7, problem: 'hash mismatch' } } })
    expect(bad.find((x) => x.label.startsWith('Audit chain'))).toMatchObject({ state: 'fail', note: expect.stringContaining('hash mismatch') })
  })
})
