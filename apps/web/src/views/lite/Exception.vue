<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { message } from 'ant-design-vue'
import { ArrowLeftOutlined, DownOutlined, RightOutlined, SendOutlined, UserAddOutlined } from '@ant-design/icons-vue'
import {
  assignLiteException, getLiteException, listAssignableUsers, proposeLiteResolution, requestLiteEvidence, type LiteExceptionView,
} from '../../api/lite'
import type { ResolutionKind } from '../../api/types'

const route = useRoute()
const id = computed(() => String(route.params.id))
const data = ref<LiteExceptionView>()
const error = ref('')
const busy = ref(false)
const open = reactive({ calc: false, history: false, sources: false })
const assign = reactive({ open: false, owner: undefined as string | undefined })
const propose = reactive({ open: false, kind: 'RESOLVED' as ResolutionKind, reason: '' })
const users = listAssignableUsers()

async function load() {
  error.value = ''
  try { data.value = await getLiteException(id.value) } catch (e) { error.value = (e as Error).message }
}
watch(id, load, { immediate: true })

async function run(action: () => Promise<unknown>, ok: string) {
  busy.value = true
  try { await action(); message.success(ok); await load() }
  catch (e) { message.error((e as Error).message) } finally { busy.value = false }
}
const doAssign = async () => {
  if (!assign.owner) return message.warning('Choose an owner')
  await run(() => assignLiteException(id.value, assign.owner!), `Assigned to ${assign.owner}`)
  assign.open = false
}
const doEvidence = () => run(() => requestLiteEvidence(id.value, `${data.value?.evidenceLabel ?? 'Evidence requested'}.`), 'Evidence requested')
const doPropose = async () => {
  if (!propose.reason.trim()) return message.warning('A reason is required')
  await run(() => proposeLiteResolution(id.value, propose.kind, propose.reason.trim()), 'Resolution proposed for approval')
  propose.open = false; propose.reason = ''
}
const when = (iso: string) => new Date(iso).toLocaleString('en-GB', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', timeZone: 'UTC' })
</script>
<template>
  <a-alert v-if="error" type="error" show-icon :message="error" />
  <template v-else-if="data">
    <router-link to="/lite/exceptions" class="back link"><ArrowLeftOutlined />Exceptions</router-link>
    <div class="heading">
      <h1>{{ data.title }}</h1>
      <div class="status">
        <span class="dot" :class="data.tone === 'muted' ? 'bg-pass' : `bg-${data.tone}`" />
        <span class="stext">{{ data.statusText }}</span><span class="sid">{{ data.id }}</span>
      </div>
    </div>

    <div class="cols">
      <div class="left">
        <section class="block">
          <div class="lt-heading">What happened</div>
          <div v-if="data.happened.numbers" class="numbers">
            <template v-for="(n, i) in data.happened.numbers" :key="i">
              <span v-if="n.op" class="op">{{ n.op }}</span>
              <div class="num"><div class="v" :class="`tone-${n.tone}`">{{ n.value }}</div><div class="cap">{{ n.caption }}</div></div>
            </template>
          </div>
          <p v-else class="happened">{{ data.happened.text }}</p>
        </section>

        <section v-if="data.notCounted.length">
          <div class="lt-heading">Not counted as cover</div>
          <div v-for="l in data.notCounted" :key="l.account" class="line">
            <div class="ltext"><div class="acct">{{ l.account }}</div><div class="reason">{{ l.reason }}</div></div>
            <span class="lamt" :class="`tone-${l.tone}`">{{ l.amount }}</span>
          </div>
        </section>

        <section v-if="data.outlook" class="fix">
          <div class="lt-heading">What would fix it</div>
          <p>{{ data.outlook }}</p>
        </section>
      </div>

      <aside class="right">
        <div class="lt-heading">What do you want to do?</div>
        <button class="btn primary full" :disabled="busy" @click="assign.open = true">Assign an owner<UserAddOutlined /></button>
        <button class="btn secondary full" :disabled="busy" @click="doEvidence">{{ data.evidenceLabel }}<SendOutlined /></button>
        <button class="link left-link" :disabled="busy" @click="propose.open = true">Propose a resolution…</button>
        <p class="rnote">{{ data.note }}</p>
        <div class="details">
          <button class="exp" @click="open.calc = !open.calc">Show calculation<DownOutlined v-if="open.calc" /><RightOutlined v-else /></button>
          <dl v-if="open.calc" class="kv"><template v-for="c in data.calculation" :key="c.label"><dt>{{ c.label }}</dt><dd>{{ c.value }}</dd></template></dl>
          <button class="exp" @click="open.history = !open.history">Show history ({{ data.history.length }})<DownOutlined v-if="open.history" /><RightOutlined v-else /></button>
          <ul v-if="open.history" class="hist"><li v-for="(h, i) in data.history" :key="i"><span class="when">{{ when(h.at) }}</span> {{ h.text }}<span class="who"> — {{ h.actor }}</span></li></ul>
          <button class="exp" @click="open.sources = !open.sources">Show source records<DownOutlined v-if="open.sources" /><RightOutlined v-else /></button>
          <ul v-if="open.sources" class="hist"><li v-for="(s, i) in data.sources" :key="i">{{ s.label }}<span class="who"> — {{ s.ref }}<template v-if="s.amount"> · {{ s.amount }}</template></span></li></ul>
        </div>
      </aside>
    </div>

    <a-modal v-model:open="assign.open" title="Assign an owner" ok-text="Assign" :confirm-loading="busy" @ok="doAssign">
      <a-select v-model:value="assign.owner" placeholder="Choose an owner" style="width: 100%" :options="users.map((u) => ({ value: u, label: u }))" />
    </a-modal>
    <a-modal v-model:open="propose.open" title="Propose a resolution" ok-text="Propose" :confirm-loading="busy" @ok="doPropose">
      <a-radio-group v-model:value="propose.kind" style="margin-bottom: 12px">
        <a-radio value="RESOLVED">Resolved</a-radio><a-radio value="ACCEPTED_DIFFERENCE">Accept difference</a-radio><a-radio value="FALSE_POSITIVE">False positive</a-radio>
      </a-radio-group>
      <a-textarea v-model:value="propose.reason" :rows="4" placeholder="Why is this the right resolution? (required)" />
    </a-modal>
  </template>
</template>
<style scoped>
.back { display: inline-flex; align-items: center; gap: 6px; align-self: flex-start; margin-bottom: -12px; }
.heading { display: flex; flex-direction: column; gap: 10px; }
h1 { margin: 0; font-size: 36px; font-weight: 600; letter-spacing: -1px; line-height: 1.2; }
.status { display: flex; align-items: center; gap: 10px; }
.dot { width: 8px; height: 8px; }
.stext { font-size: 14px; color: var(--ink-2); } .sid { font: 13px var(--font-mono); color: var(--ink-3); }
.cols { display: flex; flex-wrap: wrap; gap: 36px 48px; align-items: flex-start; }
.left { flex: 1 1 340px; min-width: 0; display: flex; flex-direction: column; gap: 36px; }
.block { display: flex; flex-direction: column; gap: 14px; }
.numbers { display: flex; align-items: center; gap: 16px; }
.num { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 4px; }
.v { font: 500 28px var(--font-mono); letter-spacing: -0.8px; }
.cap { font-size: 13px; color: var(--ink-3); }
.op { font: 24px var(--font-mono); color: var(--ink-3); order: 0; }
.happened { margin: 0; font-size: 15px; line-height: 1.5; color: var(--ink); }
.line { display: flex; align-items: center; gap: 16px; padding: 13px 0; border-top: 1px solid var(--border); }
.ltext { flex: 1; display: flex; flex-direction: column; gap: 2px; }
.acct { font: 500 13px var(--font-mono); color: var(--ink); } .reason { font-size: 13px; color: var(--ink-2); }
.lamt { font: 14px var(--font-mono); }
.fix { background: #fff; border: 1px solid var(--border); border-radius: 8px; padding: 16px 18px; display: flex; flex-direction: column; gap: 8px; }
.fix p { margin: 0; font-size: 15px; line-height: 1.45; }
.right { width: 260px; flex: none; display: flex; flex-direction: column; gap: 12px; }
.full { width: 100%; box-sizing: border-box; }
.left-link { text-align: left; }
.rnote { margin: 0; font-size: 12px; line-height: 1.45; color: var(--ink-3); }
.details { border-top: 1px solid var(--border); padding-top: 16px; display: flex; flex-direction: column; gap: 8px; }
.exp { display: flex; align-items: center; gap: 6px; background: none; border: 0; padding: 0; font: 13px var(--font-sans); color: var(--ink-2); cursor: pointer; }
.exp :deep(.anticon) { font-size: 11px; color: var(--ink-3); }
.kv { margin: 0 0 6px; display: grid; grid-template-columns: auto 1fr; gap: 4px 10px; font-size: 12px; }
.kv dt { color: var(--ink-3); } .kv dd { margin: 0; color: var(--ink-2); word-break: break-word; }
.hist { margin: 0 0 6px; padding-left: 16px; font-size: 12px; color: var(--ink-2); display: flex; flex-direction: column; gap: 4px; }
.when { font-family: var(--font-mono); color: var(--ink-3); } .who { color: var(--ink-3); }
</style>
