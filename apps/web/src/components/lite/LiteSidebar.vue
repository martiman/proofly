<script setup lang="ts">
import { useRoute } from 'vue-router'
import { HomeOutlined, WarningOutlined, SafetyCertificateOutlined, InboxOutlined, SettingOutlined, CheckOutlined } from '@ant-design/icons-vue'
import { CURRENT_USER, CURRENT_USER_ROLE } from '../../api/lite'

defineProps<{ exceptionCount?: number }>()
const route = useRoute()
const items = [
  { to: '/lite', label: 'Today', icon: HomeOutlined, match: (p: string) => p === '/lite' },
  { to: '/lite/exceptions', label: 'Exceptions', icon: WarningOutlined, match: (p: string) => p.startsWith('/lite/exceptions'), count: true },
  { to: '/lite/safeguarding', label: 'Safeguarding', icon: SafetyCertificateOutlined, match: (p: string) => p.startsWith('/lite/safeguarding') },
  { to: '/lite/sources', label: 'Sources', icon: InboxOutlined, match: (p: string) => p.startsWith('/lite/sources') },
  // No Lite design for settings yet: opens the full app.
  { to: '/settings', label: 'Settings', icon: SettingOutlined, match: () => false },
]
</script>
<template>
  <aside class="ls">
    <div class="brand"><span class="mark"><CheckOutlined /></span><span class="word">Proofly</span></div>
    <router-link v-for="i in items" :key="i.to" :to="i.to" class="nav" :class="{ active: i.match(route.path) }">
      <component :is="i.icon" class="ico" />
      <span class="lbl">{{ i.label }}</span>
      <span v-if="i.count && exceptionCount" class="count">{{ exceptionCount }}</span>
    </router-link>
    <div class="spacer" />
    <div class="user">
      <div class="name">{{ CURRENT_USER }}</div>
      <div class="role">{{ CURRENT_USER_ROLE }}</div>
    </div>
  </aside>
</template>
<style scoped>
.ls { width: 200px; flex: none; background: #fff; border-right: 1px solid var(--border); display: flex; flex-direction: column; gap: 4px; padding: 24px 12px; box-sizing: border-box; position: sticky; top: 0; height: 100vh; }
.brand { display: flex; align-items: center; gap: 8px; padding: 0 12px 24px; }
.mark { width: 22px; height: 22px; background: var(--accent); border-radius: 5px; color: #fff; display: flex; align-items: center; justify-content: center; font-size: 12px; }
.word { color: var(--ink); font-size: 17px; font-weight: 600; letter-spacing: -0.2px; }
.nav { display: flex; align-items: center; gap: 10px; padding: 9px 12px; border-radius: 6px; color: var(--ink-2); text-decoration: none; font-size: 14px; }
.nav .ico { color: var(--ink-3); font-size: 16px; }
.nav:hover { background: var(--surface-sunk); }
.nav.active { background: var(--surface-sunk); color: var(--ink); font-weight: 600; }
.nav.active .ico { color: var(--ink); }
.lbl { flex: 1; }
.count { color: var(--ink-3); font: 600 12px var(--font-mono); }
.spacer { flex: 1; }
.user { padding: 0 12px; display: flex; flex-direction: column; gap: 2px; }
.name { color: var(--ink); font-size: 13px; font-weight: 500; }
.role { color: var(--ink-3); font-size: 12px; }
</style>
