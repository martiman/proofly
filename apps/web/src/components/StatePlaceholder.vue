<script setup lang="ts">
import { ExclamationCircleOutlined, InboxOutlined, LockOutlined } from '@ant-design/icons-vue'
const props = defineProps<{ state: 'loading' | 'empty' | 'error' | 'forbidden'; message?: string }>()
const defaults = {
  loading: 'Loading…', empty: 'Nothing to show.', error: 'Something went wrong.',
  forbidden: 'You do not have permission to view this.',
}
</script>
<template>
  <div class="card ph" :data-state="props.state">
    <a-spin v-if="state === 'loading'" />
    <template v-else>
      <component :is="state === 'forbidden' ? LockOutlined : state === 'error' ? ExclamationCircleOutlined : InboxOutlined" class="ic" :class="state" />
      <span class="msg">{{ message ?? defaults[state] }}</span>
      <slot name="action" />
    </template>
  </div>
</template>
<style scoped>
.ph { display: flex; align-items: center; justify-content: center; gap: 12px; padding: 24px 16px; flex-wrap: wrap; }
.ic { font-size: 48px; color: var(--ink-3); }
.ic.error { color: var(--fail); }
.msg { font-size: 13px; color: var(--ink-2); }
</style>
