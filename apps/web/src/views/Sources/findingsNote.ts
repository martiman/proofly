export const FINDINGS_SHOWN = 5
/** "showing 5 of N" when more findings exist than are listed; empty otherwise. */
export function findingsNote(x: { findingIds: string[]; findingCount?: number }): string {
  const total = Math.max(x.findingCount ?? 0, x.findingIds.length)
  return total > FINDINGS_SHOWN ? `showing ${FINDINGS_SHOWN} of ${total}` : ''
}
