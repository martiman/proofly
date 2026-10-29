<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ArrowRightOutlined, RightOutlined } from '@ant-design/icons-vue'
import { getLiteToday, type LiteToday } from '../../api/lite'

const data = ref<LiteToday>()
const error = ref('')
onMounted(async () => { try { data.value = await getLiteToday() } catch (e) { error.value = (e as Error).message } })
</script>
<template>
  <a-alert v-if="error" type="error" show-icon :message="error" />
  <template v-else-if="data">
    <section class="verdict">
      <div class="vt"><span class="dot big" :class="data.ready ? 'bg-pass' : 'bg-fail'" /><h1>{{ data.title }}</h1></div>
      <p class="sum">{{ data.summary }}</p>
      <router-link v-if="data.problems.length" to="/lite/exceptions" class="btn primary">
        Review the {{ data.problems.length }} problems<ArrowRightOutlined />
      </router-link>
      <router-link v-else to="/lite/exceptions" class="btn primary">Open exceptions<ArrowRightOutlined /></router-link>
    </section>

    <section v-if="data.problems.length">
      <div class="lt-heading">Fix these first</div>
      <router-link v-for="(p, i) in data.problems" :key="p.key" :to="`/lite/exceptions/${p.ids[0]}`" class="problem row-link" :class="{ last: i === data.problems.length - 1 }">
        <span class="num">{{ i + 1 }}</span>
        <div class="text"><div class="ptitle">{{ p.title }}</div><div class="why">{{ p.why }}</div></div>
        <div class="owner">
          <span class="oname" :class="p.owner ? 'tone-ink' : 'tone-warn'">{{ p.ownerLabel ?? 'No owner' }}</span>
          <span class="due" :class="`tone-${p.due.tone}`">{{ p.due.long }}</span>
        </div>
        <RightOutlined class="chev" />
      </router-link>
    </section>

    <section>
      <div class="lt-heading">Everything else</div>
      <div class="checks">
        <div v-for="c in data.checks" :key="c.label" class="check">
          <div class="head"><span class="dot sm" :class="`bg-${c.tone}`" /><span class="label">{{ c.label }}</span></div>
          <div class="value">{{ c.value }}</div>
        </div>
      </div>
    </section>
  </template>
</template>
<style scoped>
.verdict { display: flex; flex-direction: column; align-items: flex-start; gap: 14px; }
.vt { display: flex; align-items: center; gap: 14px; }
.vt h1 { margin: 0; font-size: 44px; font-weight: 600; letter-spacing: -1.2px; line-height: 1.15; }
.dot.big { width: 14px; height: 14px; }
.dot.sm { width: 8px; height: 8px; }
.sum { margin: 0; font-size: 18px; color: var(--ink-2); }
.verdict .btn { padding: 11px 18px; }
section > .lt-heading { margin-bottom: 14px; }
section:has(.problem) > .lt-heading { margin-bottom: 0; }
.problem { display: flex; align-items: center; gap: 20px; padding: 22px 0; border-top: 1px solid var(--border); }
.problem.last { border-bottom: 1px solid var(--border); }
.num { width: 20px; flex: none; font: 14px var(--font-mono); color: var(--ink-3); }
.text { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 5px; }
.ptitle { font-size: 19px; font-weight: 600; letter-spacing: -0.2px; color: var(--ink); }
.why { font-size: 14px; line-height: 1.4; color: var(--ink-2); }
.owner { width: 150px; flex: none; display: flex; flex-direction: column; align-items: flex-end; gap: 2px; }
.oname { font-size: 14px; font-weight: 500; }
.due { font-size: 13px; }
.chev { color: var(--ink-3); font-size: 14px; }
.checks { display: flex; gap: 24px; }
.check { flex: 1; display: flex; flex-direction: column; gap: 4px; }
.head { display: flex; align-items: center; gap: 8px; }
.label { font-size: 14px; font-weight: 500; }
.value { font-size: 14px; color: var(--ink-2); }
</style>
