import { describe, expect, it } from 'vitest'
import { findingsNote } from '../findingsNote'

describe('findingsNote', () => {
  it('is empty for short lists', () => expect(findingsNote({ findingIds: ['a', 'b'] })).toBe(''))
  it('uses the server total when larger than the list', () => expect(findingsNote({ findingIds: ['a', 'b', 'c', 'd', 'e', 'f'], findingCount: 120 })).toBe('showing 5 of 120'))
  it('falls back to list length', () => expect(findingsNote({ findingIds: Array(9).fill('x') })).toBe('showing 5 of 9'))
})
