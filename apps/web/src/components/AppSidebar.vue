<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import {
  DashboardOutlined, WarningOutlined, SwapOutlined, SafetyCertificateOutlined,
  EditOutlined, InboxOutlined, SettingOutlined, CheckOutlined,
} from '@ant-design/icons-vue'
import { navItems } from '../router/routes'

const props = defineProps<{ counts?: Record<string, number>; environment?: string; freshness?: string; user?: { name: string; role: string } }>()
const route = useRoute()
const icons: Record<string, unknown> = {
  '/control-room': DashboardOutlined, '/exceptions': WarningOutlined, '/transactions': SwapOutlined,
  '/safeguarding': SafetyCertificateOutlined, '/sign-off': EditOutlined, '/sources': InboxOutlined, '/settings': SettingOutlined,
}
const labels: Record<string, string> = { '/control-room': 'Control Room', '/settings': 'Configuration & Access' }
const groups = computed(() => (['Daily Work', 'Administration'] as const).map((title) => ({ title, items: navItems.filter((i) => i.group === title) })))
const initials = computed(() => (props.user?.name ?? 'Charlotte Hughes').split(' ').map((p) => p[0]).join('').slice(0, 2))
</script>
<template>
  <aside class="sb">
    <div class="brand"><span class="mark"><CheckOutlined /></span><span class="word">Proofly</span></div>
    <template v-for="g in groups" :key="g.title">
      <div class="section">{{ g.title.toUpperCase() }}</div>
      <router-link v-for="i in g.items" :key="i.path" :to="i.path" class="nav" :class="{ active: route.path === i.path }">
        <component :is="icons[i.path]" class="ico" />
        <span class="lbl">{{ labels[i.path] ?? i.title }}</span>
        <span v-if="counts?.[i.path]" class="badge">{{ counts[i.path] }}</span>
      </router-link>
    </template>
    <div class="spacer" />
    <div class="env">
      <div class="row"><i class="dot" /><span class="mono">{{ environment ?? 'PILOT · customer-hosted' }}</span></div>
      <div class="fresh">{{ freshness ?? 'Last evidence 28 Oct 06:58 GMT' }}</div>
    </div>
    <div class="user">
      <span class="avatar">{{ initials }}</span>
      <div><div class="uname">{{ user?.name ?? 'Charlotte Hughes' }}</div><div class="urole">{{ user?.role ?? 'Control owner · LE-UK-001' }}</div></div>
    </div>
  </aside>
</template>
<style scoped>
.sb { width: 232px; flex: none; background: var(--nav-bg); display: flex; flex-direction: column; gap: 4px; padding: 20px 12px; position: sticky; top: 0; height: 100vh; box-sizing: border-box; }
.brand { display: flex; align-items: center; gap: 8px; padding: 0 10px 20px; }
.mark { width: 22px; height: 22px; background: var(--accent); border-radius: 5px; color: #fff; display: flex; align-items: center; justify-content: center; font-size: 12px; }
.word { color: #fff; font-size: 17px; font-weight: 600; letter-spacing: -0.2px; }
.section { color: var(--nav-ink-dim); font-size: 11px; font-weight: 500; letter-spacing: 0.6px; padding: 14px 10px 6px; }
.nav { display: flex; align-items: center; gap: 10px; padding: 8px 10px; border-radius: 6px; color: var(--nav-ink); text-decoration: none; font-size: 13px; }
.nav .ico { color: var(--nav-ink-dim); font-size: 16px; }
.nav:hover { background: var(--nav-active); }
.nav.active { background: var(--nav-active); color: #fff; font-weight: 500; }
.nav.active .ico { color: #fff; }
.lbl { flex: 1; }
.badge { background: var(--nav-line); color: var(--nav-ink); font: 11px var(--font-mono); padding: 1px 6px; border-radius: 10px; }
.spacer { flex: 1; }
.env { border: 1px solid var(--nav-line); border-radius: 6px; padding: 10px; display: flex; flex-direction: column; gap: 4px; }
.row { display: flex; align-items: center; gap: 6px; color: var(--nav-ink); font-size: 11px; }
.dot { width: 6px; height: 6px; border-radius: 50%; background: #e0a43a; }
.fresh { color: var(--nav-ink-dim); font-size: 11px; }
.user { display: flex; align-items: center; gap: 10px; padding: 12px 10px 0; }
.avatar { width: 28px; height: 28px; border-radius: 50%; background: var(--nav-line); color: #fff; font-size: 11px; font-weight: 600; display: flex; align-items: center; justify-content: center; }
.uname { color: #fff; font-size: 12px; font-weight: 500; }
.urole { color: var(--nav-ink-dim); font-size: 11px; }
</style>
