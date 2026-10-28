import { describe, expect, it } from 'vitest'
import { rulesetLabel, showing } from './logic'

describe('ControlRoom logic', () => {
  it('shortens the ruleset label but keeps plain ids', () => {
    expect(rulesetLabel('v1+proofly-controls/1[a-1.0,b-1.0]')).toBe('v1+proofly-controls/1')
    expect(rulesetLabel('RS-2026.10.1')).toBe('RS-2026.10.1')
    expect(rulesetLabel(null)).toBe('—')
  })
  it('says showing N of M only when truncated', () => {
    expect(showing(10, 84)).toBe('Showing 10 of 84')
    expect(showing(3, 3)).toBe('Showing 3')
  })
})
