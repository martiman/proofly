/** Client-side required-field checks for a config change proposal; the server stays authoritative. */
export function validateProposal(p: { targetId: string; effectiveFrom: string; reason: string }): Record<string, string[]> {
  const e: Record<string, string[]> = {}
  if (!p.targetId.trim()) e.targetId = ['Target id is required.']
  if (!/^\d{4}-\d{2}-\d{2}$/.test(p.effectiveFrom.trim())) e.effectiveFrom = ['Enter a date as YYYY-MM-DD.']
  if (!p.reason.trim()) e.reason = ['A reason is required.']
  return e
}
