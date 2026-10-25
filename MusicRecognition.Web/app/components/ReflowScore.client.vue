<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import type { VerovioToolkit } from 'verovio/esm'

const props = defineProps<{ zoom: number; labels: boolean }>()
const emit = defineEmits<{ select: [id: string]; labels: [value: boolean] }>()
const store = useWorkspaceStore(), cache = useQueryClient()
const host = ref<HTMLElement>(), spacing = ref('spacious'), rendering = ref(false), error = ref(''), warnings = ref<string[]>([])
const report = shallowRef<ReturnType<typeof placeReflowLabels>>()
const importFailed = ref(false)
const privateKey = ['reflow', crypto.randomUUID()]
const query = useQuery(computed(() => {
  const id = store.currentJobId, version = store.revision?.version ?? 0
  return { queryKey: [...privateKey, id, version], enabled: !!id && store.revision?.state === 'Ready',
    queryFn: ({ signal }: { signal: AbortSignal }) => store.reflow(id, version, signal), staleTime: Infinity, retry: false }
}))
let toolkit: VerovioToolkit | undefined
let sequence = 0
async function render() {
  const ticket = ++sequence, source = query.data.value
  toolkit?.destroy(); toolkit = undefined
  report.value = undefined; error.value = ''; warnings.value = []; importFailed.value = false; rendering.value = false
  host.value?.replaceChildren()
  if (host.value) delete host.value.dataset.report
  if (!source || !host.value) return
  rendering.value = true
  try {
    const [{ default: createModule }, { VerovioToolkit, enableLogToBuffer }] = await Promise.all([import('verovio/wasm'), import('verovio/esm')])
    const module = await createModule({ printErr: line => { if (ticket === sequence && warnings.value.length < 8) warnings.value.push(line.slice(0, 300)) } })
    if (ticket !== sequence) return
    enableLogToBuffer(true, module)
    const instance = new VerovioToolkit(module)
    toolkit = instance
    const spacious = spacing.value === 'spacious'
    instance.setOptions({ inputFrom: 'musicxml', breaks: 'auto', pageWidth: 2100, pageHeight: 2970,
      scale: 40, adjustPageHeight: true, spacingStaff: spacious ? 24 : 12, spacingBraceGroup: spacious ? 24 : 12,
      spacingSystem: spacious ? 20 : 8, spacingLinear: spacious ? .5 : .25, spacingNonLinear: .6, svgViewBox: true })
    if (!instance.loadData(source.musicXml)) throw new Error('MusicXML import failed')
    const importLog = instance.getLog()
    const count = instance.getPageCount()
    if (count < 1 || count > 40) throw new Error('Reflow page limit exceeded')
    const svgs = Array.from({ length: count }, (_, index) => instance.renderToSVG(index + 1, {}))
    const log = importLog + '\n' + instance.getLog()
    warnings.value = log.split('\n').filter(line => line.trim()).slice(0, 8)
    importFailed.value = log.includes('[Error]')
    host.value.replaceChildren()
    for (const svg of svgs) {
      const page = document.createElement('div'); page.className = 'reflow-page'; page.innerHTML = svg; host.value.append(page)
    }
    report.value = placeReflowLabels(host.value, source.notes, props.labels)
    host.value.dataset.report = JSON.stringify({ ...report.value, sourceHeadCount: source.sourceHeadCount, mappedHeadCount: source.mappedHeadCount, omittedHeadCount: source.omittedHeadCount, pages: count, version: store.revision?.version, spacing: spacing.value, renderer: instance.getVersion(), importFailed: importFailed.value, eligible: !importFailed.value && !report.value.missingIds.length })
  } catch { if (ticket === sequence) error.value = '这页暂时无法重新排版，请回原谱校对或重试。' }
  finally { if (ticket === sequence) rendering.value = false }
}
function select(event: MouseEvent) {
  const id = (event.target as Element).closest<SVGElement>('[data-note-id]')?.dataset.noteId
  if (id) emit('select', id)
}
function download() {
  if (!host.value || !report.value || importFailed.value || report.value.missingIds.length) return
  const html = '<!doctype html><meta charset="utf-8"><title>重新排版辅助谱</title><style>body{margin:0}h1,p{font:14px sans-serif;margin:20px}.reflow-page{page-break-after:always}svg{width:100%;height:auto}</style><h1>重新排版辅助谱 · 保存修订 v' + store.revision?.version + '</h1><p>本文件为重新排版的识谱结果，请对照原谱校对。已标注 ' + report.value.placed + '，待放置 ' + report.value.pending + '，未进入 MusicXML ' + query.data.value?.omittedHeadCount + '。</p>' + host.value.innerHTML
  const url = URL.createObjectURL(new Blob([html], { type: 'text/html' }))
  const link = document.createElement('a'); link.href = url; link.download = `reflow-page-${store.pageNumber}-v${store.revision?.version}.html`; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000)
}
watch([query.data, spacing, () => props.labels], () => void render(), { immediate: true, flush: 'post' })
onBeforeUnmount(() => { ++sequence; toolkit?.destroy(); void cache.cancelQueries({ queryKey: privateKey }); cache.removeQueries({ queryKey: privateKey }) })
</script>

<template>
  <div class="reflow-view">
    <div class="reflow-controls">
      <USelect v-model="spacing" :items="[{ label: '宽松间距', value: 'spacious' }, { label: '标准间距', value: 'standard' }]" aria-label="重排间距" />
      <UCheckbox :model-value="props.labels" label="显示简谱标注" @update:model-value="emit('labels', $event === true)" />
      <UButton label="下载本页重排谱" color="neutral" variant="soft" :disabled="!report || rendering || importFailed || !!report.missingIds.length" @click="download" />
    </div>
    <p class="reflow-notice">重新排版辅助谱 · 保存修订 v{{ store.revision?.version }} · 分页与原谱不同。点击音符可回原谱校对。</p>
    <p v-if="store.dirty" class="reflow-notice">有未保存修改；保存后这里才会更新。</p>
    <p v-if="query.data.value?.omittedHeadCount" class="reflow-warning">{{ query.data.value.omittedHeadCount }} 个符头未进入 MusicXML，请在原谱视图检查。</p>
    <p v-if="query.data.value?.warnings.includes('ottava-expanded-to-sounding-pitch')" class="reflow-warning">八度线已展开为实际音高，写法与原谱不同。</p>
    <p v-if="query.data.value?.warnings.includes('incomplete-key-signature')" class="reflow-warning">调号识别不完整，请重点检查转调处。</p>
    <p v-if="importFailed || report?.missingIds.length" role="alert" class="reflow-warning">本页重排不完整，下方仅供对照诊断，下载已禁用。请使用原谱标注视图。</p>
    <div v-if="error || query.isError.value" role="alert" class="preview-error">{{ error || '无法准备这份修订的重排数据。' }} <UButton label="重试重排" @click="query.refetch(); render()" /></div>
    <div v-if="rendering || query.isPending.value" role="status" class="reflow-notice">正在生成重排辅助谱…</div>
    <p v-if="report" class="reflow-notice" role="status">已关联 {{ report.mappedHeads }} / {{ query.data.value?.mappedHeadCount }} 个音符 · 已放置 {{ report.placed }} · 待放置 {{ report.pending }}<span v-if="report.missingIds.length"> · {{ report.missingIds.length }} 个渲染 ID 未找到</span><span v-if="warnings.length"> · 排版器有提示，请对照原谱</span></p>
    <div class="reflow-scroll"><div ref="host" class="reflow-host" aria-label="重新排版乐谱" :style="{ width: `${Math.max(640, 840 * props.zoom / 100)}px` }" @click="select" /></div>
  </div>
</template>

<style scoped>
.reflow-view { min-width: 0; }
.reflow-controls { display:flex;gap:12px;padding:14px 20px;flex-wrap:wrap; }
.reflow-notice,.reflow-warning { margin:0;padding:8px 20px;font-size:12px;line-height:1.7;color:#667085; }
.reflow-warning { color:#896429;background:#fff7e8; }
.reflow-scroll { background:#e8ebf0;padding:24px 16px 100px;overflow:auto;max-height:calc(100dvh - 350px); }
.reflow-host { margin:auto; }
.reflow-host :deep(.reflow-page) { background:white;margin-bottom:24px;box-shadow:0 6px 25px #172b4d10; }
.reflow-host :deep(svg) { width:100%;height:auto;display:block; }
.reflow-host :deep(.note),.reflow-host :deep(.reflow-label) { cursor:pointer; }
.reflow-host :deep(.reflow-label text) { white-space:pre; }
</style>
