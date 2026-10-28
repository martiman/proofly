import { describe, expect, it } from 'vitest'
import { ApiError } from '../../api/client'
import { can, mapCommandError, reasonError } from './logic'
import { conclusion, showing, summaryLink } from '../ControlRoom/logic'

const err = (status: number, slug: string, errors: { pointer: string; message: string }[] = []) =>
  new ApiError({ status, type: `https://proofly.example/problems/${slug}`, title: slug, detail: 'd', errors })

describe('sign-off command errors', () => {
  it('maps stale, separation, validation, forbidden', () => {
    expect(mapCommandError(err(409, 'stale-signoff')).kind).toBe('stale')
    expect(mapCommandError(err(412, 'precondition-failed')).kind).toBe('stale')
    expect(mapCommandError(err(403, 'separation-of-duties')).kind).toBe('separation')
    const v = mapCommandError(err(422, 'validation', [{ pointer: '/reason', message: 'too short' }]))
    expect(v).toMatchObject({ kind: 'validation', fields: { reason: ['too short'] } })
    expect(mapCommandError(err(403, 'forbidden')).kind).toBe('forbidden')
    expect(mapCommandError(new Error('x')).kind).toBe('other')
  })
  it('allowedActions drive buttons', () => {
    expect(can(['RETURN', 'APPROVE'], 'APPROVE')).toBe(true)
    expect(can(['RETURN'], 'APPROVE')).toBe(false)
    expect(can(undefined, 'SUBMIT')).toBe(false)
  })
  it('requires a reason of 3+ characters', () => {
    expect(reasonError(' a ')).not.toBeNull()
    expect(reasonError('okay')).toBeNull()
  })
})

describe('control room helpers', () => {
  it('conclusion prefers server blockers', () => {
    expect(conclusion('REVIEW_REQUIRED', 11, 3).reason).toContain('11 blocking')
    expect(conclusion('REVIEW_REQUIRED', null, 3).reason).toContain('3+')
    expect(conclusion('READY_FOR_SIGN_OFF', 0, 0).blocked).toBe(false)
  })
  it('showing and links', () => {
    expect(showing(10, 84)).toBe('Showing 10 of 84')
    expect(showing(5, 5)).toBe('Showing 5')
    expect(summaryLink({ kind: 'exception', id: 'E1' }, 'R1')).toEqual({ path: '/exceptions/E1', query: { run: 'R1' } })
  })
})
