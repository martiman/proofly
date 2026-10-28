/**
 * Client-side id-pattern routing for the global search. Pure navigation: no lookups, no financial logic.
 * Identifier schemes: docs/integrations/api/README.md (Identifiers).
 */
export interface SearchTarget { path: string; query: Record<string, string> }

export function resolveSearch(input: string): SearchTarget | null {
  const q = input.trim()
  if (!q) return null
  if (/^F-\d+$/i.test(q)) return { path: `/exceptions/${q.toUpperCase()}`, query: {} }
  if (/^(TRF|DD|CRD|BNK|LED)-\d+$/i.test(q)) return { path: '/transactions', query: { id: q.toUpperCase() } }
  if (/^(rejected|[a-z][a-z0-9_]*):\d+$/i.test(q)) return { path: '/transactions', query: { recordId: q } }
  if (/^RUN-/i.test(q)) return { path: '/control-room', query: { run: q } }
  return { path: '/transactions', query: { q } }
}
