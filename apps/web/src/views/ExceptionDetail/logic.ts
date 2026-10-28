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
