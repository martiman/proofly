<script setup lang="ts">
import { useRoute } from 'vue-router'
import { theme } from './theme'
import LiteShell from './layouts/LiteShell.vue'
import AppSidebar from './components/AppSidebar.vue'
import ContextBar from './components/ContextBar.vue'

const route = useRoute()
</script>

<template>
  <a-config-provider :theme="theme">
    <LiteShell v-if="route.meta.layout === 'lite'" />
    <div v-else class="shell">
      <AppSidebar :counts="{ '/exceptions': 29 }" />
      <div class="main">
        <ContextBar />
        <main class="body"><router-view /></main>
      </div>
    </div>
  </a-config-provider>
</template>

<style scoped>
.shell { display: flex; min-height: 100vh; }
.main { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.body { flex: 1; padding: 22px 24px 24px; }
</style>
