<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { message } from 'ant-design-vue'
import { RightOutlined, DownOutlined, SendOutlined } from '@ant-design/icons-vue'
import { getLiteSafeguarding, requestLiteEvidence, type LiteSafeguarding } from '../../api/lite'

const data = ref<LiteSafeguarding>()
const error = ref('')
const open = reactive<Record<string, boolean>>({})
const busy = ref(false)
onMounted(async () => { try { data.value = await getLiteSafeguarding() } catch (e) { error.value = (e as Error).message } })

async function requestEvidence() {
  busy.value = true
  try { await requestLiteEvidence('F-019', 'Bank evidence requested for balances not counted as cover.'); message.success('Bank evidence requested') }
  catch (e) { message.error((e as Error).message) } finally { busy.value = false }
}
</script>
<template>
  <a-alert v-if="error" type="error" show-icon :message="error" />
  <template v-else-if="data">
    <div><h1 class="lt-title">Safeguarding</h1><p class="lt-summary">{{ data.summary }}</p></div>
    <div class="list">
      <template v-for="c in data.currencies" :key="c.currency">
        <section v-if="c.short" class="short">
          <div class="head">
            <span class="dot bg-fail" /><span class="ccy">{{ c.currency }}</span>
            <span class="status">{{ c.statusText }}</span><span class="pos tone-fail">{{ c.position }}</span>
          </div>
          <div class="bars">
            <div class="bar"><div class="top"><span>Owed to clients</span><b>{{ c.owed }}</b></div><div class="track"><i class="ink" :style="{ width: `${c.owedPct}%` }" /></div></div>
            <div class="bar"><div class="top"><span>Covered by bank balances</span><b>{{ c.covered }}</b></div><div class="track"><i class="fail" :style="{ width: `${c.coveredPct}%` }" /></div></div>
          </div>
          <div class="actions">
            <button class="btn primary" :disabled="busy" @click="requestEvidence">Request bank evidence<SendOutlined /></button>
            <router-link to="/lite/exceptions/F-019" class="link">{{ c.notCounted }} bank balances were not counted. See why →</router-link>
          </div>
        </section>
        <template v-else>
          <div class="compact row-link" @click="open[c.currency] = !open[c.currency]">
            <span class="dot bg-pass" /><span class="ccy">{{ c.currency }}</span>
            <div class="mid"><span class="status">{{ c.statusText }}</span><span class="detail">{{ c.detail }}</span></div>
            <span class="cpos">{{ c.position }}</span>
            <DownOutlined v-if="open[c.currency]" class="chev" /><RightOutlined v-else class="chev" />
          </div>
          <div v-if="open[c.currency]" class="bars inset">
            <div class="bar"><div class="top"><span>Owed to clients</span><b>{{ c.owed }}</b></div><div class="track"><i class="ink" :style="{ width: `${c.owedPct}%` }" /></div></div>
            <div class="bar"><div class="top"><span>Covered by bank balances</span><b>{{ c.covered }}</b></div><div class="track"><i class="pass" :style="{ width: `${c.coveredPct}%` }" /></div></div>
          </div>
        </template>
      </template>
    </div>
  </template>
</template>
<style scoped>
.list { border-bottom: 1px solid var(--border); }
.short { border-top: 1px solid var(--border); padding: 24px 0; display: flex; flex-direction: column; gap: 22px; }
.head { display: flex; align-items: center; gap: 14px; }
.dot { width: 10px; height: 10px; }
.ccy { font: 600 20px var(--font-mono); color: var(--ink); }
.status { flex: 1; font-size: 16px; color: var(--ink-2); }
.head .status { flex: 1; }
.pos { font: 500 32px var(--font-mono); letter-spacing: -1px; }
.bars { display: flex; flex-direction: column; gap: 12px; }
.bars.inset { padding: 0 0 22px 24px; }
.bar { display: flex; flex-direction: column; gap: 6px; }
.top { display: flex; justify-content: space-between; font-size: 14px; color: var(--ink-2); }
.top b { font: 500 14px var(--font-mono); color: var(--ink); }
.track { height: 10px; border-radius: 5px; background: var(--surface-sunk); overflow: hidden; }
.track i { display: block; height: 100%; border-radius: 5px; }
.ink { background: var(--ink); } .fail { background: var(--fail); } .pass { background: var(--pass); }
.actions { display: flex; align-items: center; gap: 20px; }
.compact { display: flex; align-items: center; gap: 14px; padding: 22px 0; border-top: 1px solid var(--border); cursor: pointer; }
.mid { flex: 1; display: flex; flex-direction: column; gap: 2px; }
.mid .status { flex: none; }
.detail { font: 12px var(--font-mono); color: var(--ink-3); }
.cpos { font: 500 24px var(--font-mono); letter-spacing: -0.5px; color: var(--ink); }
.chev { color: var(--ink-3); font-size: 14px; }
</style>
