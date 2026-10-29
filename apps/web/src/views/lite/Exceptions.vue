<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { RightOutlined } from '@ant-design/icons-vue'
import { formatMoney } from '../../api/money'
import { getLiteExceptions, type LiteExceptions, type LiteItem } from '../../api/lite'

const route = useRoute()
const data = ref<LiteExceptions>()
const error = ref('')
const view = ref<'all' | 'mine' | 'none'>('all')
const showAll = ref(false)
const LIMIT = 8
onMounted(async () => { try { data.value = await getLiteExceptions() } catch (e) { error.value = (e as Error).message } })

const q = computed(() => String(route.query.q ?? '').trim().toLowerCase())
const keep = (i: LiteItem) =>
  (view.value === 'all' || (view.value === 'mine' ? i.owner === data.value?.user : !i.owner)) &&
  (!q.value || `${i.title} ${i.ref}`.toLowerCase().includes(q.value))
const blocking = computed(() => data.value?.blocking.filter(keep) ?? [])
const reviewAll = computed(() => data.value?.review.filter(keep) ?? [])
const review = computed(() => (showAll.value ? reviewAll.value : reviewAll.value.slice(0, LIMIT)))
const tabs = computed(() => data.value ? [
  { key: 'all' as const, label: `All ${data.value.open}` },
  { key: 'mine' as const, label: `Mine ${data.value.mine}` },
  { key: 'none' as const, label: `No owner ${data.value.noOwner}` },
] : [])
</script>
<template>
  <a-alert v-if="error" type="error" show-icon :message="error" />
  <template v-else-if="data">
    <div>
      <h1 class="lt-title">Exceptions</h1>
      <p class="lt-summary">{{ data.open }} open. {{ data.blockingCount }} block sign-off.</p>
    </div>
    <div class="views">
      <button v-for="t in tabs" :key="t.key" class="tab" :class="{ on: view === t.key }" @click="view = t.key">{{ t.label }}</button>
    </div>
    <a-empty v-if="!blocking.length && !reviewAll.length" description="Nothing matches" />

    <section v-if="blocking.length">
      <div class="group"><span class="dot bg-fail" />BLOCKS SIGN-OFF · {{ blocking.length }}</div>
      <router-link v-for="(i, n) in blocking" :key="i.key" :to="`/lite/exceptions/${i.ids[0]}`" class="row row-link" :class="{ last: n === blocking.length - 1 && !review.length }">
        <div class="text"><div class="t">{{ i.title }}</div><div class="id">{{ i.ref }}</div></div>
        <span class="amount" :class="i.amount && i.amount.amount.startsWith('-') ? 'tone-fail' : ''">{{ i.amount ? formatMoney(i.amount) : '—' }}</span>
        <div class="owner"><span :class="i.owner ? 'tone-ink' : 'tone-warn'" class="n">{{ i.ownerLabel ?? 'No owner' }}</span><span class="d" :class="`tone-${i.due.tone}`">{{ i.due.short }}</span></div>
        <RightOutlined class="chev" />
      </router-link>
    </section>

    <section v-if="review.length">
      <div class="group"><span class="dot bg-warn" />NEEDS REVIEW · {{ reviewAll.length }}</div>
      <router-link v-for="i in review" :key="i.key" :to="`/lite/exceptions/${i.ids[0]}`" class="row row-link">
        <div class="text"><div class="t">{{ i.title }}</div><div class="id">{{ i.ref }}</div></div>
        <span class="amount">{{ i.amount ? formatMoney(i.amount) : '—' }}</span>
        <div class="owner"><span :class="i.owner ? 'tone-ink' : 'tone-warn'" class="n">{{ i.ownerLabel ?? 'No owner' }}</span><span class="d" :class="`tone-${i.due.tone}`">{{ i.due.short }}</span></div>
        <RightOutlined class="chev" />
      </router-link>
      <div v-if="!showAll && reviewAll.length > LIMIT" class="more"><button class="link" @click="showAll = true">Show {{ reviewAll.length - LIMIT }} more</button></div>
      <div v-else class="end" />
    </section>
  </template>
</template>
<style scoped>
.views { display: flex; gap: 20px; margin-top: -8px; }
.tab { background: none; border: 0; border-bottom: 2px solid transparent; padding: 0 0 8px; font: 14px var(--font-sans); color: var(--ink-3); cursor: pointer; }
.tab.on { color: var(--ink); font-weight: 600; border-bottom-color: var(--ink); }
.group { display: flex; align-items: center; gap: 8px; padding-bottom: 10px; font-size: 13px; font-weight: 600; color: var(--ink-2); }
.dot { width: 8px; height: 8px; }
.row { display: flex; align-items: center; gap: 16px; padding: 14px 0; border-top: 1px solid var(--border); }
.row.last { border-bottom: 1px solid var(--border); }
.text { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 3px; }
.t { font-size: 15px; font-weight: 500; color: var(--ink); }
.id { font: 12px var(--font-mono); color: var(--ink-3); }
.amount { width: 130px; flex: none; text-align: right; font: 14px var(--font-mono); color: var(--ink); }
.owner { width: 110px; flex: none; display: flex; flex-direction: column; gap: 2px; }
.n { font-size: 13px; font-weight: 500; } .d { font-size: 12px; }
.chev { color: var(--ink-3); font-size: 13px; }
.more, .end { border-top: 1px solid var(--border); padding: 14px 0; }
.end { padding: 0; }
</style>
