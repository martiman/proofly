import { proposeAdjustment, decideAdjustment } from '../../api/services'
import type { Schema } from '../../api/services'
import { useCommand } from '../../api/composables'

type Adjustment = Schema<'SafeguardingAdjustment'>

/** Propose and decide commands. `decide` sends the adjustment's own `etag` as If-Match and the reviewed proposalVersion. */
export function useAdjustmentCommands(runId: () => string | null) {
  const propose = useCommand((a: { currency: string; amount: string; reason: string; evidenceRefs: string[]; expiresOn?: string | null }, o) =>
    proposeAdjustment(runId()!, { currency: a.currency, amount: { amount: a.amount, currency: a.currency }, reason: a.reason, evidenceRefs: a.evidenceRefs, expiresOn: a.expiresOn || null }, o))
  const decide = useCommand((a: { adjustment: Adjustment; decision: 'APPROVE' | 'REJECT'; reason: string }, o) =>
    decideAdjustment(a.adjustment.id, { decision: a.decision, reason: a.reason, proposalVersion: a.adjustment.approval?.proposalVersion }, { ...o, ifMatch: a.adjustment.etag }))
  return { propose, decide }
}

/** Shape check only (the server validates authoritatively): signed decimal string. */
export const isDecimal = (s: string) => /^-?\d+(\.\d{1,6})?$/.test(s.trim())
