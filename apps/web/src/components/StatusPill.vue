<script setup lang="ts">
import { computed } from 'vue'
export type Status = 'pass' | 'warn' | 'fail' | 'neutral' | 'info'
const props = defineProps<{ status?: Status; label: string }>()
const color = computed(() => {
  const s = props.status ?? 'neutral'
  return s === 'neutral' ? { fg: 'var(--ink-2)', bg: 'var(--neutral-soft)' }
    : s === 'info' ? { fg: 'var(--accent)', bg: 'var(--accent-soft)' }
    : { fg: `var(--${s})`, bg: `var(--${s}-soft)` }
})
</script>
<template>
  <span class="pill" :style="{ color: color.fg, background: color.bg }">
    <i class="dot" :style="{ background: color.fg }" />{{ label }}
  </span>
</template>
<style scoped>
.pill { display: inline-flex; align-items: center; gap: 6px; padding: 3px 8px; border-radius: 4px; font-size: 12px; font-weight: 500; line-height: 1.2; }
.dot { width: 6px; height: 6px; border-radius: 50%; display: inline-block; }
</style>
