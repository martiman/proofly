<script setup lang="ts">
import { computed, ref } from 'vue'
import { theme } from './theme'
import AppSidebar from './components/AppSidebar.vue'
import ContextBar from './components/ContextBar.vue'
import DevTokenDialog from './components/DevTokenDialog.vue'
import { useRunContext } from './composables/useRunContext'
import { unauthenticated } from './composables/useAuthState'
import { isApiError, isNetworkError, isForbidden, isUnauthenticated } from './api/client'

const { error, noRun, runsError, entity, reload, loading } = useRunContext()
const devOpen = ref(false)
const dev = import.meta.env.DEV

/** Shell-level state that replaces the page: sign-in, offline, forbidden. */
const blocked = computed<null | 'signin' | 'offline' | 'forbidden' | 'error'>(() => {
  const e = error.value ?? runsError.value
  if (unauthenticated.value || isUnauthenticated(e)) return 'signin'
  if (isNetworkError(e)) return 'offline'
  if (isForbidden(e)) return 'forbidden'
  if (e) return 'error'
  return null
})
const message = computed(() => { const e = error.value ?? runsError.value; return isApiError(e) ? e.detail ?? e.title : e instanceof Error ? e.message : undefined })
</script>

<template>
  <a-config-provider :theme="theme">
    <div class="shell">
      <AppSidebar />
      <div class="main">
        <ContextBar />
        <main class="body">
          <a-result v-if="blocked === 'signin'" status="warning" title="Sign in required" sub-title="Your session is missing or has expired.">
            <template #extra>
              <a-button v-if="dev" type="primary" @click="devOpen = true">Paste dev token</a-button>
              <a-button @click="reload">Retry</a-button>
            </template>
          </a-result>
          <a-result v-else-if="blocked === 'offline'" status="error" title="Cannot reach the server" sub-title="Check the connection; nothing was changed.">
            <template #extra><a-button type="primary" :loading="loading" @click="reload">Retry</a-button></template>
          </a-result>
          <a-result v-else-if="blocked === 'forbidden'" status="403" title="No access" :sub-title="message ?? 'Your account has no permission for this area.'" />
          <a-result v-else-if="blocked === 'error'" status="error" title="Could not load the application context" :sub-title="message">
            <template #extra><a-button type="primary" @click="reload">Retry</a-button></template>
          </a-result>
          <a-result v-else-if="noRun" status="info" title="No runs yet" :sub-title="`No control run exists for ${entity?.id ?? 'this legal entity'}. Runs appear after the first evidence delivery.`" />
          <router-view v-else />
        </main>
      </div>
    </div>
    <DevTokenDialog v-if="dev" :open="devOpen" @close="devOpen = false" />
  </a-config-provider>
</template>

<style scoped>
.shell { display: flex; min-height: 100vh; }
.main { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.body { flex: 1; padding: 22px 24px 24px; }
</style>
