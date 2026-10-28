import { fieldErrors, isApiError, isForbidden, isSeparationOfDuties, isStale, isValidation } from '../../api/client'

export type CommandErrorKind = 'stale' | 'forbidden' | 'validation' | 'other'
export interface CommandErrorView { kind: CommandErrorKind; message: string; fields: Record<string, string[]> }

/** Maps a thrown command error to what the UI shows: stale => reload action, field errors on the form, the server problem text otherwise. */
export function describeCommandError(e: unknown): CommandErrorView {
  if (!isApiError(e)) return { kind: 'other', message: e instanceof Error ? e.message : String(e), fields: {} }
  const text = (e.detail ? `${e.title}: ${e.detail}` : e.title) + (e.requestId ? ` (request ${e.requestId})` : '')
  const kind: CommandErrorKind = isStale(e) ? 'stale' : isForbidden(e) || isSeparationOfDuties(e) ? 'forbidden' : isValidation(e) ? 'validation' : 'other'
  return {
    kind,
    message: kind === 'stale' ? `The record changed since you opened it. Reload and try again. ${text}` : text,
    fields: fieldErrors(e),
  }
}
