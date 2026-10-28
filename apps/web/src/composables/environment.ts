export type EnvKind = 'DEV' | 'TEST' | 'PILOT' | 'PRODUCTION'
export interface Environment { name: string; kind: EnvKind }

/** Sidebar label and badge from `getMe().environment`; nothing is invented while it is unknown. */
export function environmentView(env: Environment | null | undefined): { name: string; kind: EnvKind | null; production: boolean } {
  if (!env) return { name: '—', kind: null, production: false }
  return { name: env.name, kind: env.kind, production: env.kind === 'PRODUCTION' }
}
