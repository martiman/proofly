<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { message } from 'ant-design-vue'
import { FolderOpenOutlined, SendOutlined } from '@ant-design/icons-vue'
import { getLiteSources, requestLiteEvidence, type LiteSources } from '../../api/lite'

const data = ref<LiteSources>()
const error = ref('')
const busy = ref(false)
onMounted(async () => { try { data.value = await getLiteSources() } catch (e) { error.value = (e as Error).message } })

async function requestCorrected() {
  if (!data.value?.attentionExceptionId) return
  busy.value = true
  try { await requestLiteEvidence(data.value.attentionExceptionId, 'Corrected bank file requested.'); message.success('Corrected bank file requested') }
  catch (e) { message.error((e as Error).message) } finally { busy.value = false }
}
</script>
<template>
  <a-alert v-if="error" type="error" show-icon :message="error" />
  <template v-else-if="data">
    <div><h1 class="lt-title">Sources</h1><p class="lt-summary">{{ data.summary }}</p></div>
    <div class="files">
      <div v-for="(f, i) in data.files" :key="f.key" class="file" :class="{ last: i === data.files.length - 1 }">
        <span class="dot" :class="f.attention ? 'bg-warn' : 'bg-pass'" />
        <span class="name" :class="{ bold: f.attention }">{{ f.name }}</span>
        <span class="from">
          <template v-if="f.provider"><span class="tag">{{ f.provider }}</span></template>
          <template v-else>{{ f.from }}</template>
        </span>
        <span class="arrived">{{ f.arrived }}</span>
        <span class="status"><span class="state" :class="f.attention ? 'tone-warn' : ''">{{ f.state }}</span><span v-if="f.note" class="note" :class="f.attention ? 'tone-warn' : ''">{{ f.note }}</span></span>
      </div>
    </div>
    <div v-if="data.attentionExceptionId"><button class="btn primary" :disabled="busy" @click="requestCorrected">Request a corrected bank file<SendOutlined /></button></div>
    <div class="note-box">
      <FolderOpenOutlined class="ico" />
      <span class="txt">Files arrive by SFTP in /inbound/le-uk-001/. Checked every 10 minutes, next check 07:30.</span>
      <router-link to="/sources" class="link sm">Details</router-link>
    </div>
  </template>
</template>
<style scoped>
.files { margin-top: -8px; }
.file { display: flex; align-items: center; gap: 16px; padding: 16px 0; border-top: 1px solid var(--border); }
.file.last { border-bottom: 1px solid var(--border); }
.dot { width: 8px; height: 8px; }
.name { flex: 1; font-size: 15px; font-weight: 500; color: var(--ink); }
.name.bold { font-weight: 600; }
.from { width: 150px; flex: none; font-size: 14px; color: var(--ink-2); display: flex; align-items: center; }
.tag { background: var(--accent-soft); color: var(--accent); border-radius: 3px; padding: 1px 6px; font: 600 11px var(--font-mono); }
.arrived { width: 48px; flex: none; font: 13px var(--font-mono); color: var(--ink-3); }
.status { width: 200px; flex: none; display: flex; flex-direction: column; gap: 1px; }
.state { font-size: 14px; font-weight: 500; color: var(--ink); }
.note { font-size: 12px; color: var(--ink-3); }
.note-box { display: flex; align-items: center; gap: 12px; padding: 14px 16px; background: #fff; border: 1px solid var(--border); border-radius: 8px; }
.ico { color: var(--ink-3); font-size: 16px; }
.txt { flex: 1; font-size: 13px; color: var(--ink-2); }
.link.sm { font-size: 13px; }
</style>
