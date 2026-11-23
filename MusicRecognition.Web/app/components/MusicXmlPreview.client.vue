<script setup lang="ts">
import { useDebounceFn, useResizeObserver } from '@vueuse/core'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import type { OpenSheetMusicDisplay } from 'opensheetmusicdisplay'

const props = defineProps<{ zoom: number }>()
const store = useWorkspaceStore()
const { t } = useI18n()
const host = ref<HTMLElement>()
const renderError = ref('')
const rendering = ref(false)
const cache = useQueryClient()
const privateKey = ['musicxml-preview', crypto.randomUUID()]
const query = useQuery(computed(() => {
  const id = store.currentJobId
  return { queryKey: [...privateKey, id], enabled: !!id && store.job?.state === 'Ready',
    queryFn: ({ signal }: { signal: AbortSignal }) => store.musicXml(id, signal), staleTime: Infinity, retry: false }
}))
let display: OpenSheetMusicDisplay | undefined
function render() {
  if (!display || !host.value?.clientWidth) return
  try { display.zoom = props.zoom / 100; display.render(); renderError.value = '' }
  catch { renderError.value = t('musicxml.layoutError') }
}
watch(query.data, async (blob, _previous, onCleanup) => {
  let active = true
  onCleanup(() => { active = false })
  display?.clear(); display = undefined
  renderError.value = ''
  if (!blob) return
  rendering.value = true
  try {
    const { OpenSheetMusicDisplay } = await import('opensheetmusicdisplay')
    if (!active || !host.value) return
    const instance = new OpenSheetMusicDisplay(host.value, { backend: 'svg', autoResize: false, drawingParameters: 'default' })
    await instance.load(blob, store.active?.name.replace(/\.[^.]+$/, ''))
    if (!active) { instance.clear(); return }
    display = instance
    render()
  } catch { if (active) renderError.value = t('musicxml.renderError') }
  finally { if (active) rendering.value = false }
}, { immediate: true, flush: 'post' })
watch(() => props.zoom, render)
useResizeObserver(host, useDebounceFn(render, 150))
onBeforeUnmount(() => { display?.clear(); display = undefined; void cache.cancelQueries({ queryKey: privateKey }); cache.removeQueries({ queryKey: privateKey }) })
</script>

<template>
  <div class="musicxml-preview">
    <p class="musicxml-notice">{{ $t('musicxml.notice') }}</p>
    <div v-if="query.isPending.value || rendering" class="preview-loading" role="status">{{ $t('musicxml.loading') }}</div>
    <div v-if="renderError || query.isError.value" class="preview-error" role="alert">
      {{ renderError || $t('musicxml.loadError') }}
      <UButton v-if="query.isError.value" :label="$t('musicxml.retry')" variant="ghost" color="neutral" @click="query.refetch()" />
    </div>
    <div class="musicxml-scroll"><div ref="host" class="musicxml-paper" :aria-label="$t('musicxml.aria')" :aria-busy="query.isPending.value || rendering" /></div>
  </div>
</template>

<style scoped>
.musicxml-notice { padding: 14px 22px; margin: 0; background: var(--ui-bg-elevated); color: var(--ink-soft); font-size: 12px; line-height: 1.7; }
.musicxml-scroll { max-height: calc(100dvh - 320px); min-height: 400px; overflow: auto; padding: 24px 18px 100px; background: var(--ui-bg-muted); }
.musicxml-preview { min-width: 0; }
.musicxml-paper { background: white; color: black; width: 100%; min-width: 640px; box-shadow: 0 6px 30px #172b4d0c; }
.musicxml-paper :deep(svg) { display: block; max-width: none; }
@media (max-width: 700px) { .musicxml-scroll { max-height: calc(100dvh - 390px); padding: 12px 6px 100px; } .musicxml-notice { padding: 10px 14px; font-size: 11px; } }
</style>
