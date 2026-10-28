<script setup lang="ts">
import { ref } from 'vue'
import { setDevToken } from '../api/client'

defineProps<{ open: boolean }>()
const emit = defineEmits<{ close: [] }>()
const token = ref('')
function save() {
  setDevToken(token.value.trim() || null)
  window.location.reload()
}
</script>
<template>
  <a-modal :open="open" title="Paste a dev token" ok-text="Use token" :ok-button-props="{ disabled: !token.trim() }" @ok="save" @cancel="emit('close')">
    <p>Development only. The token is kept in sessionStorage for this tab and never leaves the browser except as the API bearer token.</p>
    <a-textarea v-model:value="token" :rows="4" placeholder="eyJ…" class="mono" />
  </a-modal>
</template>
