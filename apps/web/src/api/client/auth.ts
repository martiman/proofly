/**
 * Bearer token source for the API client.
 *
 * SEAM (production sign-in is a later task): the production app must install an OIDC-backed provider with
 * `configureApi({ tokenProvider })` at startup (authorization-code + PKCE against the customer IdP, token held in memory,
 * `refresh()` doing a silent renew) and an `onUnauthorized` hook that redirects to the IdP login. Nothing in this repo may
 * contain a real token. The dev provider below is active only when `import.meta.env.DEV` is true.
 */
export interface TokenProvider {
  /** Current access token, or null when signed out (request is sent without Authorization). */
  getToken(): string | null | Promise<string | null>
  /** Optional silent renew after a 401. Resolve true when a new token is available so the request is retried once. */
  refresh?(): Promise<boolean>
}

export const DEV_TOKEN_STORAGE_KEY = 'proofly.devToken'

const storage = (): Storage | undefined => {
  try { return typeof sessionStorage === 'undefined' ? undefined : sessionStorage } catch { return undefined }
}

export function setDevToken(token: string | null): void {
  const s = storage()
  if (!s) return
  if (token) s.setItem(DEV_TOKEN_STORAGE_KEY, token)
  else s.removeItem(DEV_TOKEN_STORAGE_KEY)
}

/** Dev only: a token pasted into sessionStorage (`setDevToken`) wins over `VITE_DEV_TOKEN`. Returns null outside dev builds. */
export function devTokenProvider(env: Record<string, unknown> | undefined = import.meta.env): TokenProvider {
  return {
    getToken() {
      if (!env?.DEV) return null
      return storage()?.getItem(DEV_TOKEN_STORAGE_KEY) || (typeof env.VITE_DEV_TOKEN === 'string' && env.VITE_DEV_TOKEN) || null
    },
  }
}

export const staticTokenProvider = (token: string | null): TokenProvider => ({ getToken: () => token })
