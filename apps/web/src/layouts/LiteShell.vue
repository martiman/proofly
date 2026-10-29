<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import LiteSidebar from '../components/lite/LiteSidebar.vue'
import LiteContextRow from '../components/lite/LiteContextRow.vue'
import { getLiteOpenCount } from '../api/lite'

const route = useRoute()
const count = ref<number>()
const refresh = async () => { count.value = await getLiteOpenCount() }
onMounted(refresh)
watch(() => route.fullPath, refresh)
</script>
<template>
  <div class="lite">
    <LiteSidebar :exception-count="count" />
    <main class="main">
      <div class="content">
        <LiteContextRow />
        <router-view />
      </div>
    </main>
  </div>
</template>
<style>
.lite { display: flex; min-height: 100vh; background: var(--bg); }
.lite .main { flex: 1; min-width: 0; padding: 28px 40px; display: flex; justify-content: center; }
.lite .content { width: 880px; max-width: 100%; display: flex; flex-direction: column; gap: 36px; }
.lite .lt-title { margin: 0; font-size: 34px; font-weight: 600; letter-spacing: -0.8px; color: var(--ink); line-height: 1.2; }
.lite .lt-summary { margin: 8px 0 0; font-size: 16px; color: var(--ink-2); }
.lite .lt-heading { font-size: 13px; font-weight: 600; letter-spacing: 0.2px; color: var(--ink-3); }
.lite .dot { display: inline-block; flex: none; border-radius: 50%; }
.lite .tone-pass { color: var(--pass); } .lite .tone-warn { color: var(--warn); }
.lite .tone-fail { color: var(--fail); } .lite .tone-muted { color: var(--ink-3); } .lite .tone-ink { color: var(--ink); }
.lite .bg-pass { background: var(--pass); } .lite .bg-warn { background: var(--warn); } .lite .bg-fail { background: var(--fail); }
.lite .mono { font-family: var(--font-mono); }
.lite .btn { display: inline-flex; align-items: center; justify-content: center; gap: 8px; padding: 10px 16px; border-radius: 6px; font: 500 14px var(--font-sans); cursor: pointer; border: 1px solid transparent; text-decoration: none; }
.lite .btn.primary { background: var(--ink); color: #fff; }
.lite .btn.primary:hover { background: #2a3140; }
.lite .btn.secondary { background: #fff; color: var(--ink); border-color: var(--border-strong); }
.lite .btn.secondary:hover { background: var(--surface-sunk); }
.lite .btn[disabled] { opacity: 0.5; cursor: not-allowed; }
.lite .link { color: var(--accent); font: 500 14px var(--font-sans); cursor: pointer; background: none; border: 0; padding: 0; text-decoration: none; }
.lite .link:hover { text-decoration: underline; }
.lite .row-link { cursor: pointer; text-decoration: none; color: inherit; }
.lite .row-link:hover { background: rgba(20, 24, 31, 0.03); }
</style>
