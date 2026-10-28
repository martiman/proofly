<script setup lang="ts">
defineProps<{ title: string; subtitle?: string; eyebrow?: string }>()
</script>
<template>
  <div class="ph">
    <div>
      <div v-if="eyebrow" class="eyebrow">{{ eyebrow }}</div>
      <h1>{{ title }}</h1>
      <p v-if="subtitle">{{ subtitle }}</p>
    </div>
    <div class="actions"><slot name="actions" /></div>
  </div>
</template>
<style scoped>
.ph { display: flex; justify-content: space-between; align-items: center; margin-bottom: 16px; }
h1 { margin: 0; font-size: 28px; font-weight: 600; letter-spacing: -0.01em; }
.eyebrow { font-family: var(--font-mono, 'IBM Plex Mono', monospace); font-size: 10px; letter-spacing: 0.08em; text-transform: uppercase; color: var(--ink-3, var(--ink-2)); margin-bottom: 4px; }
p { margin: 2px 0 0; color: var(--ink-2); }
.actions { display: flex; gap: 8px; }
</style>
