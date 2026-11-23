<script setup lang="ts">
import { useDebounceFn, useResizeObserver } from '@vueuse/core'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import type { OpenSheetMusicDisplay } from 'opensheetmusicdisplay'
// 拼谱结果的 MusicXML 乐谱预览：把浏览器端排版引擎（OpenSheetMusicDisplay）画出的乐谱显示出来。
// 它不生成最终 PDF，只是让用户先看看拼起来的效果；正式导出仍由后台 Worker 完成。
// 文件名的 .client 表示只在浏览器运行，因为排版引擎依赖浏览器的 SVG 绘制能力。

// 从父页面接收缩放比例（百分比），用来放大或缩小预览。
const props = defineProps<{ zoom: number }>()
const store = useWorkspaceStore()
const { t } = useI18n()
// host 是画乐谱的容器，排版引擎会把 SVG 画在它里面。
const host = ref<HTMLElement>()
// 渲染失败时的提示文字；为空表示没有错误。
const renderError = ref('')
// 是否正在渲染，界面靠它显示"加载中"。
const rendering = ref(false)
// 取得全局网络缓存，组件销毁时要把本组件发起的请求清理掉。
const cache = useQueryClient()
// 每次挂载都生成一个新的查询 key，避免把上一份乐谱的预览留在缓存里。
const privateKey = ['musicxml-preview', crypto.randomUUID()]
// 只有任务状态是 Ready（识别完成）时才去请求 MusicXML；结果一直缓存、不自动过期。
const query = useQuery(computed(() => {
  const id = store.currentJobId
// 没有任务 id 或任务还没识别完时，enabled 为 false，请求不会发出。
  return { queryKey: [...privateKey, id], enabled: !!id && store.job?.state === 'Ready',
    queryFn: ({ signal }: { signal: AbortSignal }) => store.musicXml(id, signal), staleTime: Infinity, retry: false }
}))
// 排版引擎实例；为空表示还没创建或已经销毁。
let display: OpenSheetMusicDisplay | undefined
// 按当前缩放比例重新画一遍；容器还没就绪就跳过。
function render() {
// 容器宽度为 0 说明页面还没布局好，这时画出来会变形，直接返回。
  if (!display || !host.value?.clientWidth) return
// 缩放值以 100 为基准：传入 120 表示放大到 120%；成功一次就清掉旧错误。
  try { display.zoom = props.zoom / 100; display.render(); renderError.value = '' }
// 排版引擎报错通常说明 MusicXML 内容不合法，提示用户稍后重试。
  catch { renderError.value = t('musicxml.layoutError') }
}
// 拿到新的 MusicXML 文件后先清掉旧乐谱，再重新加载并渲染。
watch(query.data, async (blob, _previous, onCleanup) => {
// active 标记这次异步渲染是否还有效，防止用户在渲染途中切走乐谱后画到新页面。
  let active = true
  onCleanup(() => { active = false })
  display?.clear(); display = undefined
  renderError.value = ''
  if (!blob) return
  rendering.value = true
  try {
// 排版引擎体积较大，按需加载：只有真的要看预览时才下载。
    const { OpenSheetMusicDisplay } = await import('opensheetmusicdisplay')
    if (!active || !host.value) return
// 用 SVG 方式绘制（比 Canvas 清晰、可缩放）；autoResize 关掉，交给下面监听容器尺寸。
    const instance = new OpenSheetMusicDisplay(host.value, { backend: 'svg', autoResize: false, drawingParameters: 'default' })
// 加载 MusicXML；第二个参数是乐谱标题（去掉 .pdf 等后缀），会显示在纸面上。
    await instance.load(blob, store.active?.name.replace(/\.[^.]+$/, ''))
    if (!active) { instance.clear(); return }
    display = instance
    render()
  } catch { if (active) renderError.value = t('musicxml.renderError') }
  finally { if (active) rendering.value = false }
}, { immediate: true, flush: 'post' })
// 缩放变化时不用重新解析文件，只重画一次即可。
watch(() => props.zoom, render)
// 容器尺寸变化时也要重画；加 150ms 防抖，避免拖动窗口时疯狂重绘。
useResizeObserver(host, useDebounceFn(render, 150))
// 组件卸载时清理：清空画面、取消并删除本组件发起的查询，防止占着内存不放。
onBeforeUnmount(() => { display?.clear(); display = undefined; void cache.cancelQueries({ queryKey: privateKey }); cache.removeQueries({ queryKey: privateKey }) })
</script>

<template>
<!-- 预览区域：顶部一行说明，下面白色纸张区域里显示排版后的乐谱。 -->
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
/* 顶部说明条：浅色底、小字号，占满整行。 */
.musicxml-notice { padding: 14px 22px; margin: 0; background: var(--ui-bg-elevated); color: var(--ink-soft); font-size: 12px; line-height: 1.7; }
/* 纸张滚动区域：高度跟着屏幕视口（dvh）算，底部留 100px 方便滚动。 */
.musicxml-scroll { max-height: calc(100dvh - 320px); min-height: 400px; overflow: auto; padding: 24px 18px 100px; background: var(--ui-bg-muted); }
.musicxml-preview { min-width: 0; }
/* 模拟白纸：白底黑字，最窄 640px；屏幕太窄时横向滚动，而不是把乐谱压扁。 */
.musicxml-paper { background: white; color: black; width: 100%; min-width: 640px; box-shadow: 0 6px 30px #172b4d0c; }
.musicxml-paper :deep(svg) { display: block; max-width: none; }
/* 窄屏（700px 以下）时缩小内边距和字号，把空间让给乐谱。 */
@media (max-width: 700px) { .musicxml-scroll { max-height: calc(100dvh - 390px); padding: 12px 6px 100px; } .musicxml-notice { padding: 10px 14px; font-size: 11px; } }
</style>
