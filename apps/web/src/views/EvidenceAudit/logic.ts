import { getEvidencePack } from '../../api/services'
import type { Schema } from '../../api/services'

type Pack = Schema<'EvidencePack'>
export const isPackFinal = (p: Pack) => p.status === 'GENERATED' || p.status === 'FAILED' || p.status === 'NOT_GENERATED'

/** Polls GET evidence-pack until the pack leaves GENERATING (GENERATED or FAILED). Stops after maxAttempts and returns the last state. */
export async function pollEvidencePack(runId: string, o: { intervalMs?: number; maxAttempts?: number; signal?: AbortSignal; onUpdate?: (p: Pack) => void } = {}): Promise<Pack> {
  const wait = (ms: number) => new Promise<void>((r) => setTimeout(r, ms))
  let last = await getEvidencePack(runId, { signal: o.signal })
  o.onUpdate?.(last)
  for (let i = 1; last.status === 'GENERATING' && i < (o.maxAttempts ?? 60); i++) {
    await wait(o.intervalMs ?? 2000)
    if (o.signal?.aborted) break
    last = await getEvidencePack(runId, { signal: o.signal })
    o.onUpdate?.(last)
  }
  return last
}

export const humanize = (s: string) => s.replace(/_/g, ' ').toLowerCase().replace(/^\w/, (c) => c.toUpperCase())
export const fmtBytes = (n: number | null | undefined) => (n == null ? '—' : n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1048576).toFixed(1)} MB`)

export interface CheckItem { label: string; state: 'pass' | 'fail' | 'na'; note: string }
/** Verification checklist from what the API returns. The audit chain result is only shown when the pack contents carry it. */
export function verificationChecklist(p: Pack | undefined): CheckItem[] {
  if (!p || p.status === 'NOT_GENERATED') return [{ label: 'Evidence pack generated', state: 'na', note: 'No pack exists for this run yet' }]
  const chain = p.contents.find((c) => /audit[-_ ]?chain|chain[-_ ]?verification/i.test(c))
  return [
    { label: 'Evidence pack generated', state: p.status === 'GENERATED' ? 'pass' : p.status === 'FAILED' ? 'fail' : 'na', note: p.status === 'FAILED' ? (p.failureReason ?? 'Generation failed') : p.status === 'GENERATING' ? 'Generating' : 'Generated' },
    { label: 'SHA-256 fingerprint recorded', state: p.sha256 ? 'pass' : 'na', note: p.sha256 ?? 'Not available' },
    { label: 'Contents listed', state: p.contents.length ? 'pass' : 'na', note: `${p.contents.length} item(s)` },
    { label: 'Audit chain verification', state: 'na', note: chain ? `See ${chain} in the pack` : 'Not available' },
  ]
}
