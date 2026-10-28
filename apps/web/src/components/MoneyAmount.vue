<script setup lang="ts">
import { computed } from 'vue'
const props = defineProps<{ amount: number | string; currency: string; showCurrency?: boolean }>()
const text = computed(() => {
  const n = Number(props.amount)
  const f = new Intl.NumberFormat('en-GB', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(Math.abs(n))
  return (n < 0 ? '-' : '') + f
})
</script>
<template>
  <span class="money tabular" :class="{ neg: Number(amount) < 0 }">{{ text }}<span v-if="showCurrency !== false" class="ccy"> {{ currency }}</span></span>
</template>
<style scoped>
.money { white-space: nowrap; text-align: right; }
.neg { color: var(--fail); }
.ccy { color: var(--ink-3); font-size: 0.9em; }
</style>
