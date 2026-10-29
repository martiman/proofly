import { isApiError, isSeparationOfDuties, isStale, fieldErrors } from '../../api/client'

export type Action = 'ASSIGN' | 'ADD_NOTE' | 'REQUEST_EVIDENCE' | 'PROPOSE_RESOLUTION' | 'APPROVE_RESOLUTION' | 'REJECT_RESOLUTION' | 'REOPEN'

export interface CommandFailure { kind: 'stale' | 'sod' | 'validation' | 'other'; message: string; fields: Record<string, string[]> }

/** Map a thrown command error to what the UI shows (reload prompt, separation of duties, field errors). */
export function classifyFailure(e: unknown): CommandFailure {
  const fields = fieldErrors(e)
  const message = isApiError(e) ? (e.detail ? `${e.title}: ${e.detail}` : e.title) + (e.requestId ? ` (request ${e.requestId})` : '') : e instanceof Error ? e.message : String(e)
  if (isStale(e)) return { kind: 'stale', message, fields }
  if (isSeparationOfDuties(e)) return { kind: 'sod', message, fields }
  if (isApiError(e) && e.status === 422) return { kind: 'validation', message, fields }
  return { kind: 'other', message, fields }
}

/** Body of decideResolution: carries the proposal version the approver reviewed (stale -> 409). */
export function decisionBody(decision: 'APPROVE' | 'REJECT', reason: string, proposalVersion: number | undefined) {
  return { decision, reason: reason.trim(), ...(proposalVersion != null ? { proposalVersion } : {}) }
}

export const allows = (allowed: readonly string[] | undefined, a: Action) => !!allowed?.includes(a)

/** Label of the proposed resolution kind (Approval.kind); null for non-resolution approvals. */
export const proposalKindLabel = (kind: string | null | undefined) => (kind ? kind.toLowerCase().replace(/_/g, ' ').replace(/^\w/, (c) => c.toUpperCase()) : null)

/** The decision endpoint is keyed by the proposal id (`objectId` of the approval), never by the approval id (`id`). */
export const proposalIdOf = (approval: { id: string; objectId: string }): string => approval.objectId

interface RefNode { id: string; kind: string; label: string; sourceRowId?: string | null; recordId?: string | null }
/**
 * Options for the propose-resolution evidence picker: only real evidence records (nodes carrying a recordId; graph node
 * ids such as `dlv-EXC-…` are not evidence references). The value sent is the recordId, the label is human readable.
 */
export function evidenceRefOptions(nodes: readonly RefNode[]): { value: string; label: string }[] {
  const seen = new Set<string>()
  const out: { value: string; label: string }[] = []
  for (const n of nodes) {
    if (!n.recordId || seen.has(n.recordId)) continue
    seen.add(n.recordId)
    out.push({ value: n.recordId, label: `${n.recordId}${n.sourceRowId ? ` (row ${n.sourceRowId})` : ''} · ${n.label}` })
  }
  return out
}
