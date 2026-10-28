import { isApiError, isSeparationOfDuties, isStale, isValidation, fieldErrors } from '../../api/client'
import type { Schema } from '../../api/services'

type Candidate = Schema<'MatchCandidate'>

/** Candidates picked in the unmatched population become the left/right id lists of a manual match proposal. */
export function buildProposeBody(picked: Candidate[], reason: string) {
  return {
    leftTransactionIds: picked.filter((c) => c.side !== 'RIGHT').map((c) => c.transactionId),
    rightTransactionIds: picked.filter((c) => c.side === 'RIGHT').map((c) => c.transactionId),
    reason: reason.trim(),
  }
}

export interface CommandProblem {
  kind: 'separation' | 'stale' | 'used' | 'validation' | 'other'
  title: string
  detail?: string
  fields: Record<string, string[]>
}

/** Maps a failed propose/decide command to what the UI shows. The server text is always kept. */
export function describeCommandError(e: unknown): CommandProblem {
  const fields = fieldErrors(e)
  if (!isApiError(e)) return { kind: 'other', title: e instanceof Error ? e.message : String(e), fields }
  const base = { title: e.title, detail: e.detail, fields }
  if (isSeparationOfDuties(e)) return { kind: 'separation', ...base, title: 'You cannot approve your own proposal' }
  if (isStale(e)) return { kind: 'stale', ...base, title: 'This proposal changed since you opened it' }
  if (e.slug === 'record-already-used') return { kind: 'used', ...base, title: 'A record is already used by another match group' }
  if (isValidation(e)) return { kind: 'validation', ...base }
  return { kind: 'other', ...base }
}
