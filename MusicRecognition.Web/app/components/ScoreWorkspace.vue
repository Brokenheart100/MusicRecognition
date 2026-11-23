<script setup lang="ts">
// 编辑器主区域：页头（状态、保存、导出）、识别进度卡、多页导航，以及画布加检查器的布局。
// 它自己不保存乐谱数据，只从 store 读状态、把用户操作转给 store；
// 画布和检查器的快捷操作通过 ref 暴露的句柄互相调用。
// 可以把它看成编辑器外壳：把各零件摆好位置，并决定什么时候显示哪一块。

import type { Pitch } from '~~/shared/types/score'

type ScoreView = 'original' | 'musicxml'
// 画布点击漏音位置后回传的上下文；父组件用它打开音高选择框。
type AddNoteContext = { x: number; y: number; measure: number; part: number; staff: number; voice: number; system: number; pitch: Pitch }
// 画布暴露给本组件的方法清单：放置、取消、跳到下一个待处理或未标注音符、微调位置。
type CanvasHandle = {
  beginPlaceCurrent: () => void
  beginAddNote: () => void
  cancelInteraction: () => void
  nextPending: () => void
  nextUnannotated: () => void
  readonly unannotatedCount: number
  moveSelection: (dx: number, dy: number) => void
}

// 需要父页面帮忙做的事：打开文件选择框。
const emit = defineEmits<{ pick: [] }>()
// store 提供全部业务状态；下面六个 v-model 让父页面和本组件共享显示选项。
const store = useWorkspaceStore()
const { t } = useI18n()
const inspector = defineModel<boolean>('inspector', { required: true })
const zoom = defineModel<number>('zoom', { required: true })
const labels = defineModel<boolean>('labels', { required: true })
const staff = defineModel<string>('staff', { required: true })
const voice = defineModel<string>('voice', { required: true })
const scoreView = defineModel<ScoreView>('scoreView', { required: true })
// canvas 句柄用来调用画布内部方法；未标注数量由画布统计后回传给页头徽标。
const canvas = ref<CanvasHandle>()
// 画布尚未挂载时按 0 处理，避免页头出现 undefined。
const unannotatedCount = computed(() => canvas.value?.unannotatedCount ?? 0)
// 手工补录：画布进入点击模式，选中位置后打开音高选择框。
const addOpen = ref(false)
const addContext = ref<AddNoteContext | null>(null)
const addPosition = computed(() => addContext.value
  ? t('score.addNotePosition', { measure: addContext.value.measure, staff: addContext.value.staff })
  : '')
function beginAddNote() { addContext.value = null; canvas.value?.beginAddNote() }
function onAddNote(context: AddNoteContext) { addContext.value = context; addOpen.value = true }
// 保存成功才关闭弹窗；失败时保留用户选择的音高，方便重试。
async function confirmAdd(pitch: Pitch) {
  const context = addContext.value
  if (!context) return
  if (await store.addManualNote({ ...context, pitch })) { addOpen.value = false; addContext.value = null }
}

// 页面级的键盘快捷键通过这些方法操作画布（页面持有本组件的 ref）。
// 本组件不直接操作画布内部状态，只转交这些命令。
defineExpose({
  beginPlaceCurrent: () => canvas.value?.beginPlaceCurrent(),
  beginAddNote: () => canvas.value?.beginAddNote(),
  cancelInteraction: () => canvas.value?.cancelInteraction(),
  nextPending: () => canvas.value?.nextPending(),
  nextUnannotated: () => canvas.value?.nextUnannotated(),
  moveSelection: (dx: number, dy: number) => canvas.value?.moveSelection(dx, dy),
})

// 缩放档位固定为 50%~200%，避免用户拖到极端比例看不清内容。
// 50% 方便总览整页，200% 方便精确定位密集的数字。
const zoomLevels = [50, 75, 100, 125, 150, 175, 200] as const
// 下拉框需要 label 和 value 结构，这里把数字档位转成百分比文字。
const zoomOptions: { label: string; value: number }[] = zoomLevels.map(n => ({ label: `${n}%`, value: n }))
// 简谱数字和原文的切换项：开着 labels 时在原谱上叠简谱，关掉只看原文。
// 关闭后原谱上不再叠加数字，方便对照原文。
const labelItems = computed(() => [
  { label: t('score.labelsOriginal'), value: 'original' },
  { label: t('score.labelsNumbered'), value: 'numbered' },
])
// 把布尔值 labels 包装成下拉框用的字符串模型，set 时再转回布尔。
// 默认值来自页面上的 labels（true 表示显示简谱）。
const labelMode = computed({
  get: () => labels.value ? 'numbered' : 'original',
  set: (value: string | number) => { labels.value = value === 'numbered' },
})
// 原文视图和 MusicXML 预览两个页签。
// MusicXML 是识别后的结构化乐谱，用来检查转换结果。
const scoreViewItems = computed(() => [
  { label: t('score.viewOriginal'), value: 'original' },
  { label: t('score.viewMusicxml'), value: 'musicxml' },
])
// 页签的字符串模型，保证只可能是两个合法值。
const scoreViewMode = computed({
  get: () => scoreView.value,
  set: (value: string | number) => { scoreView.value = value === 'musicxml' ? 'musicxml' : 'original' },
})
// 谱表筛选项：从当前修订的音符里取出所有谱表号，去重排序后加全部。
// 多行五线谱（如钢琴大谱表）分别对应不同的谱表编号。
const staffOptions = computed(() => [{ label: t('score.staffAll'), value: 'all' }, ...[...new Set(store.notes.map(n => n.staff))].sort((a, b) => a - b).map(n => ({ label: t('score.staffN', { n }), value: String(n) }))])
// 声部筛选项同理；声部号 0 表示未分配，显示成未分配。
const voiceOptions = computed(() => [{ label: t('score.voiceAll'), value: 'all' }, ...[...new Set(store.notes.map(n => n.voice))].sort((a, b) => a - b).map(n => ({ label: n ? t('score.voiceN', { n }) : t('score.voiceUnassigned'), value: String(n) }))])
// 版本下拉：当前任务已经产生几个修订就列几项，最新的排最前面。
const versionItems = computed(() => Array.from({ length: store.job?.currentRevision ?? 0 }, (_, i) => ({ label: t('score.version', { n: i + 1 }), value: i + 1 })).reverse())
// 整份导出要求每一页都识别完成且排版完成，缺一页按钮就不可点。
// 合并导出要求识别和排版两个阶段都结束。
const allPagesReady = computed(() => store.multiPage && store.pages.length === store.documentJob?.pageCount && store.pages.every(p => p.state === 'Ready' && p.exportState === 'Ready'))
// 把单页的任务状态翻译成给人看的文字：失败、处理中、已取消、排版中、就绪、等待。
// 依据任务接口里的 state（识别状态）和 exportState（排版状态）两个字段。
function pageStatus(page: { state: string; exportState: string | null }) {
  if (page.state === 'Failed') return t('pageStatus.failed')
  if (page.state === 'Queued' || page.state === 'Processing') return t('pageStatus.processing')
  if (page.state === 'Cancelled') return t('pageStatus.cancelled')
  if (page.exportState === 'Queued' || page.exportState === 'Processing') return t('pageStatus.layout')
  return page.state === 'Ready' && page.exportState === 'Ready' ? t('pageStatus.ready') : t('pageStatus.waiting')
}
// 保存按钮的悬浮提示：根据当前情况解释为什么能存或不能存。
// 例如历史版本、导出中、没有改动，都会显示对应原因。
const saveHint = computed(() => {
  if (!store.revision) return t('saveHint.noRevision')
  if (store.exporting) return t('saveHint.exporting')
  if (store.historical) return t('saveHint.historical')
  if (store.saving) return t('saveHint.saving')
  if (!store.editable) return t('saveHint.notEditable')
  if (!store.dirty) return t('saveHint.unchanged')
  return t('saveHint.ready')
})
// 页头状态文字：按上传、识别、合并、编辑、保存、导出的优先级取最贴切的状态。
// 多页时优先显示页级进度，其次才是整份合并的状态。
const stateText = computed(() => {
  if (store.uploading) return t('state.uploading')
  if (store.multiPage && store.documentJob?.state === 'Processing' && !store.revision) return t('state.recognizingPages', { ready: store.pages.filter(p => p.state === 'Ready').length, total: store.documentJob.pageCount })
  if (store.multiPage && store.documentExport?.state === 'Processing') return t('state.merging')
  if (store.job?.state === 'Queued') return t('state.queued')
  if (store.job?.state === 'Processing') return t('state.processing')
  if (store.job?.state === 'Cancelling') return t('state.cancelling')
  if (store.job?.state === 'Cancelled') return t('state.cancelled')
  if (store.job?.state === 'Failed') return t('state.failed')
  if (store.revision?.state === 'Failed') return t('state.layoutFailed')
  if (store.exporting) return t('state.exporting')
  if (store.dirty) return t('state.dirty')
  if (store.annotations) return t('state.saved')
  return t('state.opening')
})
// 识别任务或排版修订任一失败，都按失败状态展示。
const failed = computed(() => store.job?.state === 'Failed' || store.revision?.state === 'Failed')
// 这两类错误重试也没用（页数超限、PDF 加密），界面应引导用户换文件。
const unsupportedInput = computed(() => ['unsupported-page-count', 'encrypted-pdf'].includes(store.job?.errorCode ?? ''))
// 这几类是乐谱本身无法可靠识别的确定性失败，同样不建议原地重试。
const deterministicFailure = computed(() => ['unsupported-notation', 'invalid-note-count', 'pitch-consistency-failed'].includes(store.job?.errorCode ?? ''))
// 把后端错误码翻译成具体的人话说明；未知错误走通用文案。
// 每个分支对应一种后端约定好的错误码，新增错误码时在这里补一行。
const failureDescription = computed(() => {
  if (store.job?.errorCode === 'unsupported-page-count') return t('failure.unsupportedPageCount')
  if (store.job?.errorCode === 'encrypted-pdf') return t('failure.encryptedPdf')
  if (store.job?.errorCode === 'pitch-consistency-failed') return t('failure.pitchConsistency')
  if (store.job?.errorCode === 'unsupported-notation') return t('failure.unsupportedNotation')
  if (store.job?.errorCode === 'invalid-note-count') return t('failure.invalidNoteCount')
  return t('failure.generic')
})
</script>

<template>
<!-- 工作区根节点：页头、识别状态、分页导航、编辑区依次纵向排列 -->
  <main class="score-workspace">
<!-- 页头：文件名、整体状态、音符和页数统计，以及保存和导出按钮 -->
    <div class="document-header">
      <div>
        <p class="eyebrow">YOUR SCORE, A LITTLE CLEARER.</p>
<!-- 文件名去掉扩展名，作为这份乐谱的标题 -->
        <h1>{{ store.active?.name.replace(/\.[^.]+$/, '') }}</h1>
<!-- 状态文字；旁边是音符总数和多页时的第几页、共几页 -->
        <p class="document-status" role="status"><span class="status-dot"
            :class="{ amber: store.dirty || failed, pulse: store.processing || store.exporting }" />{{ stateText
            }}<span v-if="store.annotations" class="note-count">{{ $t('score.noteCount', { count:
              store.notes.length }) }}</span><span v-if="store.multiPage" class="note-count">{{
                $t('score.pageOf', { page: store.pageNumber, total: store.documentJob?.pageCount }) }}</span></p>
<!-- 识别时用了图像增强（Adaptive）或 homr 引擎，额外标注一句 -->
        <p v-if="store.job?.preprocessing === 'Adaptive'" class="enhancement-status">{{
          $t('score.enhancementUsed') }}</p>
        <p v-if="store.job?.engine === 'Homr'" class="enhancement-status">{{ $t('score.homrUsed') }}</p>
      </div>
<!-- 右侧操作区：保存、导出当前页或整份 PDF、打开或关闭检查器 -->
      <div class="document-actions">
<!-- 保存：只有存在未保存改动且当前可编辑时才可点，快捷键 Ctrl/⌘+S -->
        <UButton v-if="store.revision" icon="i-lucide-check" :label="$t('score.save')" :loading="store.saving"
          :disabled="!store.dirty || !store.editable" :title="saveHint" aria-keyshortcuts="Control+S Meta+S"
          class="save-button" @click="store.save()" />
<!-- 导出当前页标注 PDF；修订没就绪或有未保存改动时禁用 -->
        <UButton icon="i-lucide-download"
          :label="store.multiPage ? $t('score.exportPage') : $t('score.exportPdf')" color="neutral" variant="outline"
          :disabled="store.revision?.state !== 'Ready' || store.dirty" @click="store.download()" />
<!-- 多页时才有导出整份按钮：所有页就绪才能合并，已就绪则变成下载 -->
        <UButton v-if="store.multiPage" icon="i-lucide-files"
          :label="store.documentExport?.state === 'Ready' ? $t('score.downloadDocument') : store.documentExport?.state === 'Failed' ? $t('score.retryDocumentExport') : $t('score.exportDocument')"
          color="primary"
          :loading="store.documentExport?.state === 'Queued' || store.documentExport?.state === 'Processing'"
          :disabled="!allPagesReady || store.dirty || store.documentExport?.state === 'Queued' || store.documentExport?.state === 'Processing'"
          @click="store.documentExport?.state === 'Ready' ? store.downloadDocument() : store.exportDocument()" />
<!-- 显示或隐藏右侧检查器 -->
        <UButton icon="i-lucide-sliders-horizontal" color="neutral" :variant="inspector ? 'soft' : 'ghost'"
          :aria-label="$t('score.toggleInspector')" @click="inspector = !inspector" />
      </div>
    </div>

<!-- 识别操作条：重新识别当前页，或用徽标跳到未标注的音符 -->
    <div v-if="store.job" class="recognition-actions" role="group"
      :aria-label="$t('score.recognitionActionsAria')">
<!-- 重新识别当前页：任务完成或存在未保存改动时会先弹确认 -->
      <UButton icon="i-lucide-refresh-cw" size="sm" color="neutral" variant="soft"
        :label="$t('score.rerecognizePage')" :loading="store.processing"
        :disabled="!store.currentJobId || store.processing || store.exporting"
        @click="store.rerecognizePage()" />
<!-- 选中音符后出现：只重识别这些音符所在区域，区域外保持不变 -->
      <UButton v-if="store.selection.length" icon="i-lucide-scan-search" size="sm"
        color="warning" variant="soft"
        :label="$t('score.rerecognizeSelection', { count: store.selection.length })"
        :disabled="store.processing || store.exporting || store.revision?.state !== 'Ready'"
        @click="store.rerecognizeSelection()" />
<!-- 有音符缺少简谱数字时出现，点击逐个跳转补标注 -->
      <UButton v-if="unannotatedCount" icon="i-lucide-list-checks" size="sm"
        color="warning" variant="soft"
        :label="$t('score.unannotatedAction', { count: unannotatedCount })"
        :disabled="store.processing || store.exporting" @click="canvas?.nextUnannotated()" />
<!-- 手工补录漏掉的音符：只在原文视图可用，点击后在画布上选位置 -->
      <UButton v-if="scoreView === 'original' && store.revision" icon="i-lucide-plus" size="sm"
        color="neutral" variant="soft" :label="$t('score.addNote')" :disabled="!store.editable"
        @click="beginAddNote" />
    </div>

<!-- 多页缩略导航：每页一个按钮，显示页码和该页的识别、排版状态 -->
<!-- 只有多页文档才显示，单页时整块隐藏 -->
    <nav v-if="store.multiPage" class="page-navigation glass" :aria-label="$t('score.pageNavAria')">
      <div class="page-navigation-heading"><strong>{{ $t('score.pageNavTitle') }}</strong><span>{{
        $t('score.pageNavDone', { done: store.pages.filter(p => p.state === 'Ready').length, total:
          store.documentJob?.pageCount }) }}</span></div>
      <div class="page-navigation-list">
<!-- 每页按钮的文案来自 pageStatus，点击切换到该页 -->
        <UButton v-for="page in store.pages" :key="page.id" color="neutral" variant="ghost" size="sm"
          class="page-navigation-item"
          :class="{ active: store.pageNumber === page.number, failed: page.state === 'Failed' }"
          :aria-current="store.pageNumber === page.number ? 'page' : undefined"
          :ui="{ base: 'justify-start font-normal hover:bg-transparent' }"
          @click="store.selectPage(page.number)">
          <span class="page-navigation-number">{{ String(page.number).padStart(2, '0') }}</span>
          <span class="page-navigation-state">{{ pageStatus(page) }}</span>
        </UButton>
      </div>
<!-- 整份导出失败时显示错误码；否则显示导出说明 -->
      <p v-if="store.documentExport?.state === 'Failed'" class="page-export-error" role="alert">{{
        $t('score.exportFailed', { code: store.documentExport.errorCode }) }}</p>
      <p v-else class="page-export-hint">{{ $t('score.exportHint') }}</p>
    </nav>

<!-- 任务运行、失败或取消时出现的状态卡：说明、取消、重试或换文件 -->
    <div v-if="store.processing || store.exporting || failed || store.job?.state === 'Cancelled'"
      class="processing-card glass" aria-live="polite">
      <div class="processing-symbol">
        <UIcon
          :name="failed ? 'i-lucide-circle-alert' : store.job?.state === 'Cancelled' ? 'i-lucide-pause' : 'i-lucide-loader-circle'"
          :class="{ spin: store.processing || store.exporting }" />
      </div>
      <div><strong>{{ stateText }}</strong>
        <p>{{ failed ? failureDescription : store.job?.state === 'Cancelled' ? $t('score.cancelledNote') :
          $t('score.processingNote')
        }}</p>
      </div>
<!-- 只有任务还在排队或处理中才给取消按钮 -->
      <UButton v-if="['Queued', 'Processing'].includes(store.job?.state ?? '')"
        :label="$t('score.cancelRecognition')" color="neutral" variant="ghost" @click="store.command('cancel')" />
<!-- 可重试的失败或取消状态下给重试；不可恢复的错误不显示 -->
      <UButton
        v-if="!unsupportedInput && !deterministicFailure && ['Failed', 'Cancelled'].includes(store.job?.state ?? '')"
        :label="$t('score.retryRecognition')" color="neutral" variant="soft" @click="store.command('retry')" />
<!-- 页数超限、加密这类问题，直接引导用户重新选文件 -->
      <UButton v-if="unsupportedInput" :label="$t('score.chooseAnother')" color="neutral" variant="soft"
        @click="emit('pick')" />
    </div>

<!-- 编辑区两栏：左边纸张画布，右边检查器（窄屏时检查器变成抽屉） -->
    <div class="editor-layout" :class="{ 'show-inspector': inspector && scoreView === 'original' }">
<!-- 左侧纸张面板：工具栏、页签、筛选器和画布 -->
      <section class="sheet-panel" :aria-label="$t('score.sheetAria')">
<!-- 纸张工具栏：当前视图名称、页码、修订版本号、历史版本提示 -->
        <div class="sheet-toolbar glass">
<!-- 左侧显示当前查看的是原文标注还是 MusicXML，以及修订版本号 -->
          <div class="sheet-toolbar-left"><span class="paper-icon">
              <UIcon name="i-lucide-file-music" />
            </span><span class="page-label">{{ scoreView ===
              'musicxml' ? 'MusicXML' : $t('score.overlay') }} <small>{{
                String(store.pageNumber).padStart(2, '0') }} / {{
                String(store.documentJob?.pageCount ?? 1).padStart(2, '0') }}</small></span><span
              v-if="store.revision" class="version-tag">v{{ store.revision.version }}</span>
<!-- 正在看历史版本时，给一个回到最新的快捷按钮 -->
            <UButton v-if="store.historical" size="xs" color="neutral" variant="soft"
              :label="$t('score.backToLatest')" @click="store.showVersion(store.job?.currentRevision ?? 1)" />
          </div>
<!-- 撤销、重做和版本下拉：撤销只针对本地草稿，版本下拉用于查看已保存修订 -->
          <div class="sheet-toolbar-right">
<!-- 撤销和重做按钮：能不能点由本地草稿历史决定 -->
            <UTooltip :text="$t('score.undoTitle')">
              <UButton icon="i-lucide-undo-2" color="neutral" variant="ghost" size="sm" :aria-label="$t('score.undo')"
                :disabled="!store.canUndo || !store.editable" @click="store.undo()" />
            </UTooltip>
            <UTooltip :text="$t('score.redoTitle')">
              <UButton icon="i-lucide-redo-2" color="neutral" variant="ghost" size="sm" :aria-label="$t('score.redo')"
                :disabled="!store.canRedo || !store.editable" @click="store.redo()" />
            </UTooltip><span class="toolbar-divider" />
<!-- 版本下拉：选中旧版本会进入只读的历史查看模式 -->
            <USelect v-if="store.job?.currentRevision" :model-value="store.revision?.version" :items="versionItems"
              size="sm" :aria-label="$t('score.versionAria')" class="version-select"
              @update:model-value="store.showVersion(Number($event))" />
          </div>
        </div>
<!-- 历史版本提示条：说明当前看到的是旧版本，不能编辑 -->
        <div v-if="store.historical" class="history-notice">
          <UIcon name="i-lucide-history" /> {{ $t('score.historyNotice') }}
        </div>
<!-- 原文和 MusicXML 预览切换页签 -->
        <div v-if="store.revision" class="flex flex-wrap gap-2 px-4 py-3" role="group"
          :aria-label="$t('score.viewsAria')">
          <UTabs v-model="scoreViewMode" :items="scoreViewItems" :content="false" size="xs" class="shrink-0"
            :ui="{ trigger: 'text-[11px] px-3' }" />
        </div>
<!-- 谱表、声部筛选，以及待手动放置数量按钮 -->
        <div v-if="store.revision && scoreView === 'original'" class="score-filters">
          <USelect v-model="staff" :items="staffOptions" size="sm" :aria-label="$t('score.selectStaff')" />
          <USelect v-model="voice" :items="voiceOptions" size="sm" :aria-label="$t('score.selectVoice')" />
<!-- 有自动排版失败、需要手动放置的数字时出现 -->
          <UButton v-if="store.pending" color="warning" variant="soft" size="xs"
            class="ms-auto shrink-0 whitespace-nowrap" :ui="{ base: 'text-[10px]' }"
            @click="canvas?.nextPending()">
            <span class="status-dot amber" /> {{ $t('score.pendingChip', { count: store.pending }) }}
          </UButton>
        </div>
<!-- 画布依赖浏览器 API（PDF.js、DOM 测量），所以只在客户端渲染 -->
        <ClientOnly>
<!-- 服务端渲染阶段显示 fallback 里的加载文案 -->
<!-- MusicXML 预览：用重新排版后的谱面展示，和原文画布互斥 -->
          <MusicXmlPreview v-if="scoreView === 'musicxml' && store.revision" :key="store.currentJobId"
            :zoom="zoom" />
<!-- 原谱画布；传入缩放、是否显示简谱、谱表与声部筛选 -->
          <ScoreCanvas v-else ref="canvas" :zoom="zoom" :labels="labels" :staff="staff" :voice="voice" @add-note="onAddNote" /><template
            #fallback>
            <div class="preview-loading">{{ $t('score.loading') }}</div>
          </template>
        </ClientOnly>
<!-- 底部视图工具条：简谱和原文切换、缩放按钮、整页适配 -->
        <div class="view-dock glass">
          <UTabs v-if="scoreView === 'original'" v-model="labelMode" :items="labelItems" :content="false" size="xs"
            class="shrink-0" :ui="{ trigger: 'text-[10px]' }" /><span class="toolbar-divider" />
<!-- 缩放只影响画布显示比例，不改动标注坐标 -->
<!-- 缩小、放大每次 25%，整页适配恢复 100% -->
          <UButton icon="i-lucide-minus" color="neutral" variant="ghost" size="sm" :aria-label="$t('score.zoomOut')"
            :disabled="zoom <= 50" @click="zoom = Math.max(50, zoom - 25)" />
          <USelect v-model.number="zoom" :items="zoomOptions" size="sm" variant="ghost" class="zoom-select"
            :aria-label="$t('score.zoomAria')"
            :ui="{ base: 'w-[78px] justify-center ps-1.5 pe-6 text-[10px]/4 text-[var(--ink-soft)] hover:bg-transparent focus:bg-transparent max-[700px]:text-[9px]', value: 'text-center text-[10px] text-[var(--ink-soft)]', trailing: 'pe-1.5' }" />
          <UButton icon="i-lucide-plus" color="neutral" variant="ghost" size="sm" :aria-label="$t('score.zoomIn')"
            :disabled="zoom >= 200" @click="zoom = Math.min(200, zoom + 25)" />
          <UButton icon="i-lucide-maximize" color="neutral" variant="ghost" size="sm" :aria-label="$t('score.fitPage')"
            @click="zoom = 100" />
        </div>
      </section>
<!-- 右侧检查器：改标注样式、换算简谱、修正音高、查看校对提醒 -->
      <ScoreInspector v-if="store.revision && scoreView === 'original'" @place="canvas?.beginPlaceCurrent()"
        @pending="canvas?.nextPending()" @close="inspector = false" />
    </div>
<!-- 手工补录的音高选择框：确认后立即保存为新修订 -->
    <AddNoteDialog v-model:open="addOpen" :initial-pitch="addContext?.pitch ?? null" :position="addPosition"
      :loading="store.saving" @confirm="confirmAdd" />
  </main>
</template>
