import { isApiError, isSeparationOfDuties, isStale, isValidation, fieldErrors } from '../../api/client'

export type SignOffAction = 'SUBMIT' | 'RETURN' | 'APPROVE'
export const can = (allowed: readonly string[] | undefined, a: SignOffAction): boolean => !!allowed?.includes(a)

export type CommandProblem =
  | { kind: 'stale'; message: string }
  | { kind: 'separation'; message: string }
  | { kind: 'validation'; message: string; fields: Record<string, string[]> }
  | { kind: 'forbidden'; message: string }
  | { kind: 'other'; message: string }

/** Map a failed sign-off command to what the UI shows. Stale offers a reload; separation of duties is shown verbatim. */
export function mapCommandError(e: unknown): CommandProblem {
  const text = isApiError(e) ? (e.detail ? `${e.title}: ${e.detail}` : e.title) + (e.requestId ? ` (request ${e.requestId})` : '') : e instanceof Error ? e.message : String(e)
  if (isStale(e)) return { kind: 'stale', message: text }
  if (isSeparationOfDuties(e)) return { kind: 'separation', message: text }
  if (isValidation(e)) return { kind: 'validation', message: text, fields: fieldErrors(e) }
  if (isApiError(e) && e.status === 403) return { kind: 'forbidden', message: text }
  return { kind: 'other', message: text }
}

export const REASON_MIN = 3
export const reasonError = (r: string): string | null => (r.trim().length < REASON_MIN ? `Reason is required (at least ${REASON_MIN} characters).` : null)
