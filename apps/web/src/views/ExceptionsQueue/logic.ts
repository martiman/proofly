import type { ApiQuery, Schema } from '../../api/services'
import { isApiError } from '../../api/client'

export type Severity = Schema<'Severity'>
export type ControlArea = Schema<'ControlArea'>
export type ExceptionStatus = Schema<'ExceptionStatus'>
export type BulkResultItem = Schema<'BulkResult'>['results'][number]

export const SEVERITIES: Severity[] = ['CRITICAL', 'HIGH', 'MEDIUM', 'LOW']
export const CONTROLS: ControlArea[] = ['data_completeness', 'matching', 'independent_ledger', 'transaction_lifecycle', 'safeguarding']
export const STATUSES: ExceptionStatus[] = ['OPEN', 'IN_INVESTIGATION', 'WAITING_FOR_DATA', 'PENDING_APPROVAL', 'RESOLVED', 'ACCEPTED_DIFFERENCE', 'FALSE_POSITIVE']
/** Sort keys accepted by the server (listExceptions `sort`). */
export const SORT_KEYS = ['severity', 'dueAt', 'createdAt', 'ageDays', 'businessDate', 'status', 'control', 'value', 'id'] as const
export const DEFAULT_SORT = '-severity,dueAt,id'

export interface QueueFilters {
  severity: Severity[]
  control: ControlArea[]
  status: ExceptionStatus[]
  ownerId?: string
  unassigned: boolean
  blocking: boolean
  currency?: string
  valueMin: string
  valueMax: string
  ageDaysMin: number | null
  ageDaysMax: number | null
  search: string
  savedViewId?: string
  sort: string
}

export const emptyFilters = (): QueueFilters => ({
  severity: [], control: [], status: [], unassigned: false, blocking: false,
  valueMin: '', valueMax: '', ageDaysMin: null, ageDaysMax: null, search: '', sort: DEFAULT_SORT,
})

const DECIMAL = /^-?\d+(\.\d{1,6})?$/

/** Client-side validation that only mirrors the contract (value range needs a currency, decimal strings). */
export function validateFilters(f: QueueFilters): Record<string, string> {
  const e: Record<string, string> = {}
  const hasValue = f.valueMin.trim() !== '' || f.valueMax.trim() !== ''
  if (hasValue && !f.currency) e.currency = 'Choose a currency to filter by value'
  if (f.valueMin.trim() && !DECIMAL.test(f.valueMin.trim())) e.valueMin = 'Enter a decimal number'
  if (f.valueMax.trim() && !DECIMAL.test(f.valueMax.trim())) e.valueMax = 'Enter a decimal number'
  return e
}

/** Filters to listExceptions query. Invalid combinations are not sent (the form shows the error). */
export function toListQuery(f: QueueFilters): ApiQuery<'listExceptions'> {
  const q: ApiQuery<'listExceptions'> = { sort: f.sort || DEFAULT_SORT }
  if (f.severity.length) q.severity = f.severity
  if (f.control.length) q.control = f.control
  if (f.status.length) q.status = f.status
  if (f.unassigned) q.unassigned = true
  else if (f.ownerId) q.ownerId = f.ownerId
  if (f.blocking) q.blocking = true
  if (f.currency) q.currency = f.currency
  const err = validateFilters(f)
  if (f.currency && f.valueMin.trim() && !err.valueMin) q.valueMin = f.valueMin.trim()
  if (f.currency && f.valueMax.trim() && !err.valueMax) q.valueMax = f.valueMax.trim()
  if (f.ageDaysMin != null) q.ageDaysMin = f.ageDaysMin
  if (f.ageDaysMax != null) q.ageDaysMax = f.ageDaysMax
  if (f.search.trim()) q.search = f.search.trim()
  if (f.savedViewId) q.savedViewId = f.savedViewId
  return q
}

/** The summary operation only declares a subset of the list filters; the rest is reported via `ignoredBySummary`. */
export function toSummaryQuery(f: QueueFilters): ApiQuery<'getExceptionsSummary'> {
  const q: ApiQuery<'getExceptionsSummary'> = {}
  if (f.severity.length) q.severity = f.severity
  if (f.control.length) q.control = f.control
  if (f.status.length) q.status = f.status
  if (f.unassigned) q.unassigned = true
  else if (f.ownerId) q.ownerId = f.ownerId
  if (f.currency) q.currency = f.currency
  if (f.search.trim()) q.search = f.search.trim()
  if (f.savedViewId) q.savedViewId = f.savedViewId
  return q
}

export function ignoredBySummary(f: QueueFilters): string[] {
  const out: string[] = []
  if (f.blocking) out.push('blocking')
  if (f.valueMin.trim() || f.valueMax.trim()) out.push('value range')
  if (f.ageDaysMin != null || f.ageDaysMax != null) out.push('age')
  return out
}

/** Saved-view `filters` payload: the same keys as the list query (without paging and sort). */
export function toViewFilters(f: QueueFilters): Record<string, unknown> {
  const { sort: _s, savedViewId: _v, ...rest } = toListQuery(f) as Record<string, unknown>
  return rest
}

export function fromViewFilters(filters: Record<string, unknown>, sort?: string): QueueFilters {
  const f = emptyFilters()
  const arr = (v: unknown) => (Array.isArray(v) ? v.map(String) : typeof v === 'string' && v ? v.split(',') : [])
  f.severity = arr(filters.severity) as Severity[]
  f.control = arr(filters.control) as ControlArea[]
  f.status = arr(filters.status) as ExceptionStatus[]
  if (typeof filters.ownerId === 'string') f.ownerId = filters.ownerId
  f.unassigned = filters.unassigned === true
  f.blocking = filters.blocking === true
  if (typeof filters.currency === 'string') f.currency = filters.currency
  if (typeof filters.valueMin === 'string') f.valueMin = filters.valueMin
  if (typeof filters.valueMax === 'string') f.valueMax = filters.valueMax
  if (typeof filters.ageDaysMin === 'number') f.ageDaysMin = filters.ageDaysMin
  if (typeof filters.ageDaysMax === 'number') f.ageDaysMax = filters.ageDaysMax
  if (typeof filters.search === 'string') f.search = filters.search
  if (sort) f.sort = sort
  return f
}

/** Ant Design table sorter -> server sort string (single column, id as tie-break). */
export function sortFromTable(field: string | undefined, order: 'ascend' | 'descend' | null | undefined): string {
  if (!field || !order || !(SORT_KEYS as readonly string[]).includes(field)) return DEFAULT_SORT
  return `${order === 'descend' ? '-' : ''}${field},id`
}
export function sortOrderFor(sort: string, field: string): 'ascend' | 'descend' | null {
  const first = sort.split(',')[0] ?? ''
  if (first === field) return 'ascend'
  if (first === `-${field}`) return 'descend'
  return null
}

export interface BulkOutcome { ok: BulkResultItem[]; failed: BulkResultItem[] }
export function splitBulk(results: BulkResultItem[]): BulkOutcome {
  return { ok: results.filter((r) => r.status >= 200 && r.status < 300), failed: results.filter((r) => r.status < 200 || r.status >= 300) }
}

/** Users that can take exception work: roles that carry exception.investigate in the documented role mapping. */
export function investigators<U extends { id: string; name: string; roles: string[] }>(users: U[]): U[] {
  return users.filter((u) => u.roles.some((r) => r === 'ANALYST' || r === 'APPROVER'))
}

export function describeError(e: unknown): string {
  if (isApiError(e)) return (e.detail ? `${e.title}: ${e.detail}` : e.title) + (e.requestId ? ` (request ${e.requestId})` : '')
  return e instanceof Error ? e.message : String(e)
}
