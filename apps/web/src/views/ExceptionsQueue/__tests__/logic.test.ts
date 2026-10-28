import { describe, expect, it } from 'vitest'
import { emptyFilters, fromViewFilters, ignoredBySummary, investigators, sortFromTable, splitBulk, toListQuery, toSummaryQuery, toViewFilters, validateFilters } from '../logic'

describe('queue filters', () => {
  it('value range requires currency and is not sent without it', () => {
    const f = { ...emptyFilters(), valueMin: '10.5' }
    expect(validateFilters(f).currency).toBeTruthy()
    expect(toListQuery(f).valueMin).toBeUndefined()
    expect(toListQuery({ ...f, currency: 'GBP' })).toMatchObject({ currency: 'GBP', valueMin: '10.5' })
  })
  it('rejects non-decimal values', () => { expect(validateFilters({ ...emptyFilters(), currency: 'EUR', valueMax: '1e3' }).valueMax).toBeTruthy() })
  it('summary query only has declared keys and reports ignored filters', () => {
    const f = { ...emptyFilters(), blocking: true, ageDaysMin: 2, severity: ['HIGH' as const] }
    expect(toSummaryQuery(f)).toEqual({ severity: ['HIGH'] })
    expect(ignoredBySummary(f)).toEqual(['blocking', 'age'])
  })
  it('unassigned wins over owner; view round trip', () => {
    const f = { ...emptyFilters(), unassigned: true, ownerId: 'u1', status: ['OPEN' as const] }
    expect(toListQuery(f).ownerId).toBeUndefined()
    const back = fromViewFilters(toViewFilters(f))
    expect(back.status).toEqual(['OPEN']); expect(back.unassigned).toBe(true)
  })
  it('table sort maps to server sort with id tie-break; unknown resets', () => {
    expect(sortFromTable('value', 'descend')).toBe('-value,id')
    expect(sortFromTable('dueAt', 'ascend')).toBe('dueAt,id')
    expect(sortFromTable('x', 'ascend')).toBe('-severity,dueAt,id')
  })
  it('splits 207 results and filters investigators', () => {
    const o = splitBulk([{ exceptionId: 'a', status: 200 }, { exceptionId: 'b', status: 412 }])
    expect(o.ok).toHaveLength(1); expect(o.failed[0]!.exceptionId).toBe('b')
    expect(investigators([{ id: '1', name: 'a', roles: ['VIEWER'] }, { id: '2', name: 'b', roles: ['ANALYST'] }]).map((u) => u.id)).toEqual(['2'])
  })
})
