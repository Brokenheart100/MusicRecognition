<script setup lang="ts">
import { onKeyStroke, useActiveElement, useEventListener } from '@vueuse/core'
import type { RecentScore } from '~~/shared/types/score'
// 工作台主页：整个应用只有这一页，负责拼出侧边栏、顶栏、画布和检查器。
// 它集中管理界面开关、显示选项和全页键盘快捷键；乐谱数据与保存逻辑在 store 里。
// 可以把它当成前台总调度：用户按键或点按钮，它只把请求转给对应组件。
// 界面状态（缩放、筛选等）只在本页存在，刷新后回到默认值。

// 画布组件通过 ref 暴露的操作清单；键盘快捷键靠这些方法控制画布。
type CanvasHandle = {
  beginPlaceCurrent: () => void
  cancelInteraction: () => void
  nextPending: () => void
  moveSelection: (dx: number, dy: number) => void
}

// 页面本地的界面状态都住在这里；业务状态（音符、草稿、任务）统一由 store 提供。
const { t } = useI18n()
const store = useWorkspaceStore()
const activeElement = useActiveElement()
// 焦点是否落在输入框、下拉框、对话框等控件里：是的话页面快捷键要让位，
// 否则用户打字时按字母会误触发操作。
const editingFocus = computed(() => !!activeElement.value?.closest('input,textarea,select,[role="combobox"],[role="listbox"],[role="option"],[role="menu"],[role="dialog"],[contenteditable]:not([contenteditable="false"])'))
// 文件选择框藏起来，点导入时由代码代点；画布和侧栏也用 ref 拿句柄。
// 侧栏、折叠、检查器、帮助四个开关决定页面布局，不同屏幕宽度走不同分支。
const fileInput = ref<HTMLInputElement>()
const canvas = ref<CanvasHandle>()
const sidebar = ref(false)
const collapsed = ref(false)
const inspector = ref(true)
const help = ref(false)
// Keep the guide eager-free: load once on first open, then retain it for close transitions.
const guideMounted = ref(false)
const toast = useToast()
// 记住哪些页已经弹过节奏未解决提醒，避免每次刷新都重复打扰。
const promptedPages = new Set<string>()
// 画布显示选项，用 v-model 传给 ScoreWorkspace：缩放百分比、是否显示简谱数字、
// 只看某个谱表（staff）或某个声部（voice）；这些只在本地，不进服务器。
const zoom = ref(100)
const labels = ref(true)
const scoreView = ref<'original' | 'musicxml'>('original')
const staff = ref('all')
const voice = ref('all')

// 点导入按钮时，替用户点一下隐藏的文件选择框。
function pick() { fileInput.value?.click() }
// 文件选择回调：处理完要把 input 清空，否则同一个文件第二次选不会触发 change。
// 选中文件后先收起手机侧栏，再交给 store 上传。
async function choose(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]; if (file) { sidebar.value = false; await store.upload(file) }
  input.value = ''
}
// 从最近打开列表选一份乐谱：重置声部筛选，再交给 store 切换文档。
async function open(item: RecentScore) { sidebar.value = false; staff.value = 'all'; voice.value = 'all'; await store.open(item) }
// 方向键微调选中标注的位置（边界限制和草稿提交在画布和 store 里）。
function moveSelected(dx: number, dy: number) {
  canvas.value?.moveSelection(dx, dy)
}
// 全页键盘快捷键的总入口，按优先级依次处理：
// 帮助和保存 → 对话框和输入框让路 → Ctrl 组合键 → 单键操作。
// 每个分支都先看 event.repeat，按住不放不会连续触发。
function keyboard(event: KeyboardEvent) {
  const mod = event.metaKey || event.ctrlKey, key = event.key.toLowerCase()
  // Saving/help remain available from fields. Text editing retains native
  // select-all and undo, and other dialogs/popovers keep their own keyboard flow.
// Ctrl/⌘+K 任何时候都能开关帮助面板。
  if (mod && !event.altKey && !event.shiftKey && key === 'k') {
    event.preventDefault(); if (!event.repeat) help.value = !help.value; return
  }
// 有弹窗或下拉菜单打开时，键盘交给它们自己处理，页面不再抢。
  if (help.value || activeElement.value?.closest('[role="dialog"],[role="listbox"],[role="menu"]')) return
// Ctrl/⌘+S 保存；输入框里也允许，方便随时保存。
  if (mod && !event.altKey && !event.shiftKey && key === 's') { event.preventDefault(); if (!event.repeat) void store.save(); return }
// 光标在输入控件里或按着 Alt 时不处理单键，避免和输入法、浏览器冲突。
  if (editingFocus.value || event.altKey) return
// Ctrl/⌘ 组合键只认打开文件、导出、撤销重做、全选，其余交给浏览器。
  if (mod) {
    if (key === 'o' && !event.shiftKey) { event.preventDefault(); if (!event.repeat && !store.uploading) pick(); return }
// Ctrl/⌘+Shift+E 导出当前页标注 PDF，只在原文视图且已有修订时可用。
    if (key === 'e' && event.shiftKey && scoreView.value === 'original' && store.revision) {
      event.preventDefault(); if (!event.repeat) void store.download(); return
    }
// 撤销、重做和全选只在原文视图且已有修订时生效。
    if (scoreView.value !== 'original' || !store.revision) return
    if (key === 'z') { event.preventDefault(); if (!event.repeat) { if (event.shiftKey) store.redo(); else store.undo() }; return }
    if (key === 'y' && !event.shiftKey) { event.preventDefault(); if (!event.repeat) store.redo(); return }
// Ctrl+A 选中当前筛选下的全部音符。
    if (key === 'a' && !event.shiftKey) { event.preventDefault(); if (!event.repeat) store.select(filteredNotes().map(n => n.id)); return }
    return // Never interpret browser shortcuts such as Ctrl+J as note navigation.
  }
// 单独按 ? 打开帮助面板。
  if (key === '?' && !event.repeat) { event.preventDefault(); help.value = true; return }
// 没有打开任何乐谱时，剩下的快捷键都不生效。
  if (!store.active) return
// Esc 取消画布上的拖拽、框选或放置，并清空选中。
  if (event.key === 'Escape') { canvas.value?.cancelInteraction(); store.clearSelection(); return }
// 以下单键只在没有输入焦点时生效：B 侧栏、I 检查器、L 简谱显隐、[ ] 翻页。
  if (!event.repeat) {
    if (key === 'b') { event.preventDefault(); if (window.innerWidth >= 1020) collapsed.value = !collapsed.value; else sidebar.value = !sidebar.value; return }
    if (key === 'i' && store.revision && scoreView.value === 'original') { event.preventDefault(); inspector.value = !inspector.value; return }
    if (key === 'l' && store.revision && scoreView.value !== 'musicxml') { event.preventDefault(); labels.value = !labels.value; return }
    if ((key === '[' || key === ']') && store.multiPage) { event.preventDefault(); void store.selectPage(store.pageNumber + (key === ']' ? 1 : -1)); return }
  }
// 没有修订（服务器还没生成谱面数据）时，视图相关快捷键不生效。
  if (!store.revision) return
// 加号、减号以 25% 为步长缩放，限制在 50%~200%；0 恢复 100%。
  if (['+', '=', '-', '0'].includes(key)) {
    event.preventDefault(); zoom.value = key === '0' ? 100 : Math.max(50, Math.min(200, zoom.value + (key === '-' ? -25 : 25))); return
  }
// 以下是原文视图专属的标注编辑快捷键。
  if (scoreView.value !== 'original') return
// 按住 Shift 时方向键一次移动 10 像素，否则 1 像素。
  const delta = event.shiftKey ? 10 : 1
  const direction: Record<string, [number, number]> = { ArrowLeft: [-delta, 0], ArrowRight: [delta, 0], ArrowUp: [0, -delta], ArrowDown: [0, delta] }
// 有选中音符时，方向键移动它的简谱数字位置。
  if (store.selectedId && direction[event.key]) { event.preventDefault(); moveSelected(...direction[event.key]!); return }
// J/K 在可见音符之间向下或向上跳，循环选择并自动打开检查器。
  if (['j', 'k'].includes(key) && !event.repeat) {
    event.preventDefault()
    const notes = filteredNotes()
    const index = notes.findIndex(n => n.id === store.selectedId)
    const next = index < 0 ? (key === 'j' ? 0 : notes.length - 1) : (index + (key === 'j' ? 1 : -1) + notes.length) % notes.length
    store.select(notes[next] ? [notes[next]!.id] : [])
    inspector.value = true
  }
// Delete 或 Backspace 把选中的简谱数字隐藏（音符本身不删除，可以恢复）。
  if ((event.key === 'Delete' || event.key === 'Backspace') && store.selectedId) {
    event.preventDefault(); if (!event.repeat) store.hideMany(store.selection.length ? store.selection : [store.selectedId])
  }
// N 跳到下一个需要手动放置的标注；P 进入点选放置模式。
  if (key === 'n' && !event.repeat) { event.preventDefault(); canvas.value?.nextPending() }
  if (key === 'p' && store.selectedId && store.editable && !event.repeat) { event.preventDefault(); canvas.value?.beginPlaceCurrent() }
}
// 根据当前谱表、声部筛选出看得见的音符，快捷键只在它们之间跳。
function filteredNotes() { return store.notes.filter(n => (staff.value === 'all' || String(n.staff) === staff.value) && (voice.value === 'all' || String(n.voice) === voice.value)) }
// 有未保存草稿时，关闭或刷新页面前让浏览器弹确认框。
function beforeLeave(event: BeforeUnloadEvent) { if (store.dirty) { event.preventDefault(); event.returnValue = '' } }
// 注册全局键盘监听：已被处理过的按键和输入法组合键不再重复处理。
onKeyStroke(event => !event.defaultPrevented && !event.isComposing, keyboard)
useEventListener('beforeunload', beforeLeave)
// 挂载时：宽屏默认打开检查器，并初始化 store（恢复上次打开的乐谱）。
onMounted(() => { inspector.value = window.innerWidth >= 1020; void store.init() })
// 卸载时通知 store 停掉轮询等后台工作。
onBeforeUnmount(() => { store.dispose() })
// 选中任一音符就自动展开检查器，方便连续校对。
watch(() => store.selectedId, value => { if (value) inspector.value = true })
// 帮助面板第一次打开才挂载，之后保留，关闭动画才完整。
watch(help, value => { if (value) guideMounted.value = true })
// Prompt once per recognized page; refetches and draft saves must not repeat it.
// 每页识别完成后，若存在节奏无法确定的警告，只提醒一次，
// 并在提示条上提供跳转检查器的按钮，避免反复打扰。
watch(
  () => [store.currentJobId, store.pageNumber, store.revision] as const,
  ([jobId, page, revision]) => {
    if (!jobId || !revision) return
// 警告文本以 unresolved-rhythm: 开头，按前缀统计这一类问题的数量。
    const count = revision.score.warnings.filter(warning => warning.startsWith('unresolved-rhythm:')).length
    const key = `${jobId}:${page}`
    if (!count || promptedPages.has(key)) return
    promptedPages.add(key)
    toast.add({
      title: t('inspector.unresolvedToastTitle', { count }),
      description: t('inspector.unresolvedToastHint'),
      color: 'warning',
      icon: 'i-lucide-triangle-alert',
      duration: 10000,
      actions: [{ label: t('inspector.unresolvedToastAction'), onClick: () => { inspector.value = true } }],
    })
  },
  { immediate: true },
)
</script>

<template>
  <div class="workspace" :data-ready="store.ready"
    :class="{ 'has-score': store.active, 'sidebar-collapsed': collapsed, 'sidebar-open': sidebar, 'inspector-open': inspector && store.active }">
<!-- 根容器：用 class 告诉 CSS 当前是否有乐谱、侧栏和检查器是否展开 -->
<!-- 隐藏的原生文件选择框；accept 限定 PDF 和常见扫描图片 -->
    <input ref="fileInput" class="sr-only" type="file" accept=".pdf,.jpg,.jpeg,.png" :aria-label="$t('shell.chooseFileAria')"
      @change="choose">
<!-- 手机侧栏打开时的半透明遮罩，点它关闭侧栏 -->
    <button v-if="sidebar" class="sidebar-scrim" :aria-label="$t('shell.closeSidebar')" @click="sidebar = false" />
<!-- 左侧栏：最近打开的乐谱、导入入口和帮助按钮 -->
    <WorkspaceSidebar @request-import="pick" @select="open" @help="help = true" @close="sidebar = false" />

<!-- 右侧主区域：顶栏、错误条、欢迎页或编辑器 -->
    <div class="main-shell">
<!-- 顶栏：折叠或打开侧栏、打开帮助 -->
      <WorkspaceTopbar @toggle-collapse="collapsed = !collapsed" @open-sidebar="sidebar = true"
        @open-help="help = true" />

<!-- 全局错误条：显示上传、保存、连接错误，重试连接会重新轮询服务器 -->
      <UAlert v-if="store.error" class="error-banner" color="error" variant="soft" icon="i-lucide-circle-alert"
        :description="store.error" orientation="horizontal" close role="alert"
        :actions="[{ label: $t('shell.retryConnection'), color: 'neutral', variant: 'ghost', onClick: () => store.poll() }]"
        :ui="{ description: 'text-[11px] opacity-100', close: 'p-1' }" @update:open="store.error = ''" />

<!-- 还没打开乐谱时显示欢迎页，引导用户导入第一份谱子 -->
      <WelcomePanel v-if="!store.active" @request-import="pick" />
<!-- 已打开乐谱：编辑器（画布加检查器），所有 v-model 都绑定回本页状态 -->
      <LazyScoreWorkspace v-else ref="canvas" v-model:inspector="inspector" v-model:zoom="zoom" v-model:labels="labels"
        v-model:staff="staff" v-model:voice="voice" v-model:score-view="scoreView" @pick="pick" />
    </div>

<!-- 快捷键帮助面板：按需加载，第一次打开后保留 -->
    <LazyKeyboardGuide v-if="guideMounted" v-model="help" />
  </div>
</template>
