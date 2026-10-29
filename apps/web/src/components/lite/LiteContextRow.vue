<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { SearchOutlined } from '@ant-design/icons-vue'
import { getLiteContext } from '../../api/lite'

const router = useRouter()
const text = ref('')
const q = ref('')
onMounted(async () => { text.value = (await getLiteContext()).text })
const go = () => router.push({ path: '/lite/exceptions', query: q.value.trim() ? { q: q.value.trim() } : {} })
</script>
<template>
  <div class="ctx">
    <span class="text">{{ text }}</span>
    <label class="search"><SearchOutlined class="ico" /><input v-model="q" placeholder="Search anything" @keydown.enter="go"></label>
  </div>
</template>
<style scoped>
.ctx { display: flex; align-items: center; gap: 16px; }
.text { flex: 1; color: var(--ink-3); font-size: 13px; }
.search { width: 220px; box-sizing: border-box; display: flex; align-items: center; gap: 8px; padding: 7px 10px; background: #fff; border: 1px solid var(--border); border-radius: 6px; }
.search:focus-within { border-color: var(--accent); }
.ico { color: var(--ink-3); font-size: 14px; }
input { flex: 1; min-width: 0; border: 0; outline: 0; background: transparent; font: 13px var(--font-sans); color: var(--ink); }
input::placeholder { color: var(--ink-3); }
</style>
