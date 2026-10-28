<script setup lang="ts">
const props = defineProps<{ state: 'loading' | 'empty' | 'error' | 'forbidden'; message?: string }>()
const defaults = {
  loading: 'Loading…', empty: 'Nothing to show.', error: 'Something went wrong.',
  forbidden: 'You do not have permission to view this.',
}
</script>
<template>
  <div class="card ph">
    <a-spin v-if="state === 'loading'" />
    <a-result v-else-if="state === 'forbidden'" status="403" :sub-title="message ?? defaults.forbidden" />
    <a-result v-else-if="state === 'error'" status="error" :sub-title="message ?? defaults.error"><template #extra><slot name="action" /></template></a-result>
    <a-empty v-else :description="message ?? defaults.empty"><slot name="action" /></a-empty>
  </div>
</template>
<style scoped>.ph { display: flex; justify-content: center; padding: 40px 16px; }</style>
