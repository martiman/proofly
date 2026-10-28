import { describe, expect, it } from 'vitest'
import { validateProposal } from '../proposeValidation'

describe('validateProposal', () => {
  it('accepts a complete proposal', () => {
    expect(validateProposal({ targetId: 'R-1', effectiveFrom: '2026-11-01', reason: 'tune' })).toEqual({})
  })
  it('rejects every missing or malformed field', () => {
    expect(Object.keys(validateProposal({ targetId: ' ', effectiveFrom: '1 Nov', reason: '' })).sort()).toEqual(['effectiveFrom', 'reason', 'targetId'])
  })
})
