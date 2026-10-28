import type { Schema } from '../../api/services'
import type { Status } from '../../components/StatusPill.vue'

export type ControlState = Schema<'ControlState'>
export type DeliveryState = Schema<'DeliveryState'>
export type Severity = Schema<'Severity'>
export type SafeguardingState = Schema<'SafeguardingState'>

export const human = (s: string): string => s.replace(/_/g, ' ').toLowerCase().replace(/^\w/, (c) => c.toUpperCase())
export const fmtDate = (d: string | null | undefined): string => (d ? d.replace('T', ' ').slice(0, 16) : '—')

export const ctrlStatus = (s: ControlState): Status => (s === 'PASSED' ? 'pass' : s === 'FAILED' || s === 'BLOCKED' ? 'fail' : 'warn')
export const delStatus = (s: DeliveryState): Status =>
  s === 'RECEIVED' ? 'pass' : s === 'MISSING' || s === 'REJECTED' ? 'fail' : s === 'SUPERSEDED' ? 'neutral' : 'warn'
export const sevStatus = (s: Severity): Status => (s === 'CRITICAL' || s === 'HIGH' ? 'fail' : s === 'MEDIUM' ? 'warn' : 'neutral')
export const sgStatus = (s: SafeguardingState): Status =>
  s === 'COVERED' || s === 'SURPLUS' || s === 'NO_OBLIGATION' ? 'pass' : s === 'SHORTFALL' ? 'fail' : 'warn'

/** Sign-off impact is a label for the control state, decided by the server's state (never computed from amounts). */
export const impact = (s: ControlState): [string, 'none' | 'fail' | 'warn'] =>
  s === 'PASSED' ? ['None', 'none'] : s === 'FAILED' || s === 'BLOCKED' ? ['Blocks', 'fail'] : ['Needs approval', 'warn']

/** Route to an exception; keeps the run in the query so the detail opens in the same run. */
export const exceptionLink = (id: string, runId: string | null) => ({ path: `/exceptions/${id}`, query: runId ? { run: runId } : {} })

export function summaryLink(l: { kind: string; id: string }, runId: string | null) {
  const query = runId ? { run: runId } : {}
  if (l.kind === 'exception') return exceptionLink(l.id, runId)
  if (l.kind === 'source') return { path: '/sources', query }
  return { path: '/exceptions', query: { ...query, control: l.id } }
}

export interface Conclusion { blocked: boolean; headline: string; reason: string }
/**
 * Banner text from the server's sign-off blockers (count) when known, else from blocking items of the capped attention queue.
 * `blockerCount` null = sign-off pack not available to this caller.
 */
export function conclusion(runState: string, blockerCount: number | null, queueBlocking: number): Conclusion {
  const n = blockerCount ?? queueBlocking
  if (n > 0) return { blocked: true, headline: 'Sign-off blocked', reason: `${n}${blockerCount === null ? '+' : ''} blocking issue${n === 1 ? '' : 's'} must be resolved before this run can be signed off.` }
  return { blocked: false, headline: human(runState), reason: 'No blocking issues.' }
}

/** "Showing N of M" text; M unknown when null. */
export const showing = (n: number, total: number | null | undefined): string => (total != null && total > n ? `Showing ${n} of ${total}` : `Showing ${n}`)
