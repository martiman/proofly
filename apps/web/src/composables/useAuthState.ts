import { ref } from 'vue'

/** Set by the API client's `onUnauthorized` hook (SEAM: production redirects to the IdP login instead). */
export const unauthenticated = ref(false)

export function markUnauthenticated(): void {
  unauthenticated.value = true
}
export function clearUnauthenticated(): void {
  unauthenticated.value = false
}
