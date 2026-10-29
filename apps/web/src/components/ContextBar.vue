<script setup lang="ts">
import { ref } from 'vue'
import { DownOutlined, SearchOutlined, BellOutlined } from '@ant-design/icons-vue'

withDefaults(defineProps<{ entity?: string; businessDate?: string; currency?: string; run?: string; unread?: boolean }>(), {
  entity: 'LE-UK-001 · Proofly Payments Ltd', businessDate: '27 Oct 2026', currency: 'All', run: 'v1', unread: true,
})
const q = ref('')
</script>
<template>
  <header class="cb">
    <button class="sel"><span class="k">Entity</span><span class="v">{{ entity }}</span><DownOutlined class="ch" /></button>
    <button class="sel"><span class="k">Business date</span><span class="v">{{ businessDate }}</span><DownOutlined class="ch" /></button>
    <button class="sel"><span class="k">Currency</span><span class="v">{{ currency }}</span><DownOutlined class="ch" /></button>
    <button class="sel"><span class="k">Run</span><span class="v mono">{{ run }}</span><DownOutlined class="ch" /></button>
    <div class="gap" />
    <label class="search"><SearchOutlined class="ch" /><input v-model="q" placeholder="Search transactions, accounts, exceptions…" /><span class="mono sc">⌘K</span></label>
    <button class="bell"><BellOutlined /><i v-if="unread" class="unread" /></button>
  </header>
</template>
<style scoped>
.cb { display: flex; align-items: center; gap: 8px; padding: 12px 32px; background: var(--surface); border-bottom: 1px solid var(--border); }
.sel { display: flex; align-items: center; gap: 8px; padding: 6px 10px; border: 1px solid var(--border); border-radius: 6px; background: none; cursor: pointer; font-family: inherit; }
.k { color: var(--ink-3); font-size: 11px; } .v { color: var(--ink); font-size: 12px; font-weight: 500; }
.ch { color: var(--ink-3); font-size: 11px; }
.gap { flex: 1; }
.search { width: 340px; display: flex; align-items: center; gap: 8px; padding: 7px 10px; background: var(--surface-sunk); border-radius: 6px; }
.search input { flex: 1; border: 0; background: none; outline: 0; font: 12px var(--font-sans); color: var(--ink); }
.sc { color: var(--ink-3); font-size: 11px; }
.bell { position: relative; width: 32px; height: 32px; border: 0; background: none; border-radius: 6px; color: var(--ink-2); font-size: 16px; cursor: pointer; }
.unread { position: absolute; top: 5px; right: 5px; width: 7px; height: 7px; border-radius: 50%; background: var(--fail); }
</style>
