<script setup lang="ts">
import { computed } from 'vue'
import { formatMoney } from '../api/client/format'

// Display only: the decimal string goes through formatMoney (Intl, exact); never converted to a JS number.
const props = defineProps<{ amount: string; currency: string; showCurrency?: boolean }>()
const text = computed(() => formatMoney(props.amount, props.currency))
const negative = computed(() => props.amount.startsWith('-') && /[1-9]/.test(props.amount))
</script>
<template>
  <span class="money tabular" :class="{ neg: negative }">{{ text }}</span>
</template>
<style scoped>
.money { white-space: nowrap; text-align: right; }
.neg { color: var(--fail); }
</style>
