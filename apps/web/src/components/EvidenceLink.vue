<script setup lang="ts">
import { LinkOutlined } from '@ant-design/icons-vue'
defineProps<{ label: string; to?: string | object; href?: string }>()
</script>
<template>
  <router-link v-if="to" :to="to" class="ev"><LinkOutlined /> {{ label }}</router-link>
  <a v-else :href="href" class="ev"><LinkOutlined /> {{ label }}</a>
</template>
<style scoped>
.ev { color: var(--accent); font-family: var(--font-mono); font-size: 12px; text-decoration: none; }
.ev:hover { text-decoration: underline; }
</style>
