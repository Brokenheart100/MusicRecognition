<script setup lang="ts">
import { onKeyStroke, useActiveElement, useEventListener } from '@vueuse/core'

const store = useWorkspaceStore()
const activeElement = useActiveElement()
const editingFocus = computed(() => !!activeElement.value?.closest('input,textarea,select,[role="combobox"],[role="listbox"],[role="option"],[role="menu"],[role="dialog"],[contenteditable]:not([contenteditable="false"])'))
const fileInput = ref<HTMLInputElement>()
const canvas = ref<{ beginPlaceCurrent: () => void; nextPending: () => void }>()
const sidebar = ref(false)
const collapsed = ref(false)
const inspector = ref(true)
const help = ref(false)
const dragOver = ref(false)
const zoom = ref(100)
const labels = ref(true)
const scoreView = ref('original')
const staff = ref('all')
const voice = ref('all')
const search = ref('')
const recent = computed(() => store.recent.filter(item => item.name.toLowerCase().includes(search.value.toLowerCase())))
const staffOptions = computed(() => [{ label: '全部谱表', value: 'all' }, ...[...new Set(store.notes.map(n => n.staff))].sort((a, b) => a - b).map(n => ({ label: `谱表 ${n}`, value: String(n) }))])
const voiceOptions = computed(() => [{ label: '全部声部', value: 'all' }, ...[...new Set(store.notes.map(n => n.voice))].sort((a, b) => a - b).map(n => ({ label: n ? `声部 ${n}` : '未分配声部', value: String(n) }))])
const allPagesReady = computed(() => store.multiPage && store.pages.length === store.documentJob?.pageCount && store.pages.every(p => p.state === 'Ready' && p.exportState === 'Ready'))
function pageStatus(page: { state: string; exportState: string | null }) {
  if (page.state === 'Failed') return '识别失败'
  if (page.state === 'Queued' || page.state === 'Processing') return '识别中'
  if (page.state === 'Cancelled') return '已取消'
  if (page.exportState === 'Queued' || page.exportState === 'Processing') return '排版中'
  return page.state === 'Ready' && page.exportState === 'Ready' ? '已就绪' : '等待处理'
}
const saveHint = computed(() => {
  if (!store.revision) return '尚未生成可编辑的修订'
  if (store.exporting) return '当前修订正在排版导出，完成后才能继续修改'
  if (store.historical) return '正在查看历史修订，不能直接保存；请切换到最新修订'
  if (store.saving) return '正在保存…'
  if (!store.editable) return '当前修订暂不可编辑'
  if (!store.dirty) return '没有未保存的修改——拖动或修改标注后此按钮才会点亮'
  return '把当前修改保存为新修订'
})
const stateText = computed(() => {
  if (store.uploading) return '正在上传原谱'
  if (store.multiPage && store.documentJob?.state === 'Processing' && !store.revision) return `正在识别 ${store.pages.filter(p => p.state === 'Ready').length} / ${store.documentJob.pageCount} 页`
  if (store.multiPage && store.documentExport?.state === 'Processing') return '正在合并整份标注乐谱'
  if (store.job?.state === 'Queued') return '已收好，等待识别'
  if (store.job?.state === 'Processing') return '正在细读每一个音符'
  if (store.job?.state === 'Cancelling') return '正在停止识别'
  if (store.job?.state === 'Cancelled') return '识别已取消'
  if (store.job?.state === 'Failed') return '这份乐谱需要再看一看'
  if (store.revision?.state === 'Failed') return '标注排版暂未完成'
  if (store.exporting) return '正在为标注寻找留白'
  if (store.dirty) return '有未保存的修改'
  if (store.annotations) return '已保存，可以从容练习'
  return '正在打开乐谱'
})
const failed = computed(() => store.job?.state === 'Failed' || store.revision?.state === 'Failed')
const unsupportedInput = computed(() => ['unsupported-page-count', 'encrypted-pdf'].includes(store.job?.errorCode ?? ''))
const deterministicFailure = computed(() => ['unsupported-notation', 'invalid-note-count', 'pitch-consistency-failed'].includes(store.job?.errorCode ?? ''))
const failureDescription = computed(() => {
  if (store.job?.errorCode === 'unsupported-page-count') return '这份 PDF 超过 30 页的处理上限。'
  if (store.job?.errorCode === 'encrypted-pdf') return '这份 PDF 已加密，请先导出未加密版本再上传。'
  if (store.job?.errorCode === 'pitch-consistency-failed') return '音符结果存在不一致，尚未生成标注。你可以重试，或换一份更清晰的原谱。'
  if (store.job?.errorCode === 'unsupported-notation') return '本页包含当前识谱器尚不能可靠处理的记谱（例如八度线），为避免标错音，本页未生成标注；其他页仍可继续校对，但整份导出需要每页都成功。'
  if (store.job?.errorCode === 'invalid-note-count') return '本页音符锚点不完整，为避免遗漏标注，系统没有生成结果。'
  return '原文件已保留。请重试识别，或重新导入这份乐谱。'
})
function pick() { fileInput.value?.click() }
async function choose(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]; if (file) { sidebar.value = false; await store.upload(file) }
  input.value = ''
}
async function drop(event: DragEvent) {
  dragOver.value = false
  const file = event.dataTransfer?.files[0]; if (file) await store.upload(file)
}
async function open(item: (typeof store.recent)[number]) { sidebar.value = false; staff.value = 'all'; voice.value = 'all'; await store.open(item) }
function moveSelected(dx: number, dy: number) {
  if (!store.selectedId || !store.editable) return
  const a = store.annotations?.annotations.find(n => n.noteId === store.selectedId), p = store.placement(store.selectedId)
  const x = p?.x ?? a?.labelBounds?.[0], y = p?.y ?? a?.labelBounds?.[1]
  if (x != null && y != null) store.position(store.selectedId, x + dx, y + dy)
}
function keyboard(event: KeyboardEvent) {
  if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') { event.preventDefault(); help.value = !help.value; return }
  if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 's' && !help.value) { event.preventDefault(); void store.save(); return }
  if (help.value || editingFocus.value) return
  if (scoreView.value === 'musicxml') return
  const mod = event.metaKey || event.ctrlKey
  if (mod && event.key.toLowerCase() === 'z') { event.preventDefault(); if (event.shiftKey) store.redo(); else store.undo(); return }
  if (mod && event.key.toLowerCase() === 'y') { event.preventDefault(); store.redo(); return }
  if (!store.revision) return
  const delta = event.shiftKey ? 10 : 1
  const direction: Record<string, [number, number]> = { ArrowLeft: [-delta, 0], ArrowRight: [delta, 0], ArrowUp: [0, -delta], ArrowDown: [0, delta] }
  if (store.selectedId && direction[event.key]) { event.preventDefault(); moveSelected(...direction[event.key]!); return }
  if (['j', 'k'].includes(event.key.toLowerCase())) {
    event.preventDefault()
    const notes = store.notes.filter(n => (staff.value === 'all' || String(n.staff) === staff.value) && (voice.value === 'all' || String(n.voice) === voice.value))
    const index = notes.findIndex(n => n.id === store.selectedId)
    store.selectedId = notes[(index + (event.key.toLowerCase() === 'j' ? 1 : -1) + notes.length) % notes.length]?.id ?? null
    inspector.value = true
  }
  if ((event.key === 'Delete' || event.key === 'Backspace') && store.selectedId) { event.preventDefault(); store.hide(store.selectedId) }
  if (event.key === 'Escape') store.selectedId = null
}
function beforeLeave(event: BeforeUnloadEvent) { if (store.dirty) { event.preventDefault(); event.returnValue = '' } }
onKeyStroke(event => !event.defaultPrevented && !event.isComposing, keyboard)
useEventListener('beforeunload', beforeLeave)
onMounted(() => { inspector.value = window.innerWidth >= 1020; void store.init() })
onBeforeUnmount(() => { store.dispose() })
watch(() => store.selectedId, value => { if (value) inspector.value = true })
</script>

<template>
  <div class="workspace" :data-ready="store.ready"
    :class="{ 'has-score': store.active, 'sidebar-collapsed': collapsed, 'sidebar-open': sidebar, 'inspector-open': inspector && store.active }">
    <input ref="fileInput" class="sr-only" type="file" accept=".pdf,.jpg,.jpeg,.png" aria-label="选择钢琴谱文件"
      @change="choose">
    <button v-if="sidebar" class="sidebar-scrim" aria-label="关闭侧栏" @click="sidebar = false" />
    <aside class="library-sidebar glass" aria-label="乐谱库">
      <button class="brand" aria-label="谱间，返回工作台" @click="store.home()"><img src="/favicon.svg" alt="" width="38"
          height="38"><span>谱间<small>PIANO NOTES</small></span></button>
      <UButton icon="i-lucide-plus" label="导入乐谱" size="lg" class="new-score" :loading="store.uploading" @click="pick" />
      <button class="sidebar-home" :class="{ active: !store.active }" @click="store.home(); sidebar = false">
        <UIcon name="i-lucide-layout-dashboard" /><span>工作台</span>
        <UIcon name="i-lucide-chevron-right" class="tiny" />
      </button>
      <div class="library-heading"><span>我的乐谱</span><span class="library-count">{{
        store.recent.length.toString().padStart(2, '0') }}</span></div>
      <UInput v-if="store.recent.length > 4" v-model="search" icon="i-lucide-search" placeholder="寻找一段旋律"
        aria-label="搜索本机乐谱" class="library-search" />
      <div class="library-list">
        <div v-if="!store.recent.length" class="library-empty">
          <UIcon name="i-lucide-music-2" />
          <p>这里，留给你的旋律</p><span>导入的乐谱会收在这里</span>
        </div>
        <div v-for="item in recent" :key="item.id" class="library-item"
          :class="{ active: store.active?.id === item.id }">
          <button class="library-open" :aria-label="`打开 ${item.name}`" @click="open(item)"><span class="file-mini">
              <UIcon name="i-lucide-music-2" />
            </span><span class="file-text"><strong>{{ item.name.replace(/\.[^.]+$/, '') }}</strong><small>钢琴 ·
                钢琴原谱</small></span></button>
          <UButton icon="i-lucide-x" class="forget-score" size="xs" color="neutral" variant="ghost"
            :aria-label="`移除本机记录 ${item.name}`" @click="store.forget(item.id)" />
        </div>
      </div>
      <div class="sidebar-bottom">
        <div class="quiet-note"><span class="quiet-lines">♩</span>
          <p>从读谱，到奏响。<small>每一次练习，都值得从容。</small></p>
        </div><button class="help-link" @click="help = true">
          <UIcon name="i-lucide-circle-help" /><span>使用指南与快捷键</span><span>⌘ K</span>
        </button>
        <p class="local-note">
          <UIcon name="i-lucide-lock-keyhole" /> 乐谱记录保存在本机浏览器
        </p>
      </div>
    </aside>

    <div class="main-shell">
      <header class="topbar">
        <div class="topbar-left">
          <UButton icon="i-lucide-panel-left" class="desktop-collapse" color="neutral" variant="ghost"
            aria-label="切换乐谱侧栏" @click="collapsed = !collapsed" />
          <UButton icon="i-lucide-panel-left" class="mobile-sidebar" color="neutral" variant="ghost" aria-label="打开乐谱库"
            @click="sidebar = true" />
          <div class="breadcrumb"><button @click="store.home()">工作台</button><template v-if="store.active">
              <UIcon name="i-lucide-chevron-right" /><span>{{ store.active.name.replace(/\.[^.]+$/, '') }}</span>
            </template>
          </div>
        </div>
        <div class="topbar-actions"><span class="workspace-tag"><span class="status-dot" /> 为专注练习而作</span>
          <UTooltip text="使用指南">
            <UButton icon="i-lucide-book-open" color="neutral" variant="ghost" aria-label="打开使用指南"
              @click="help = true" />
          </UTooltip><span class="avatar" aria-label="本机工作空间">谱</span>
        </div>
      </header>

      <div v-if="store.error" class="error-banner" role="alert">
        <UIcon name="i-lucide-circle-alert" />
        <p>{{ store.error }}</p>
        <UButton color="neutral" variant="ghost" size="xs" label="重试连接" @click="store.poll()" />
        <UButton icon="i-lucide-x" color="neutral" variant="ghost" size="xs" aria-label="关闭提示"
          @click="store.error = ''" />
      </div>

      <main v-if="!store.active" class="welcome" @dragover.prevent="dragOver = true"
        @dragleave.prevent="dragOver = false" @drop.prevent="drop">
        <div class="welcome-heading">
          <div>
            <p class="eyebrow"><span /> A LITTLE CLARITY. A LITTLE MUSIC.</p>
            <h1>让读谱，<em>更从容。</em></h1>
            <p class="welcome-description">在五线谱的留白处，添一点熟悉的音符。<br>保留原谱的美，也让每一次练习，轻松一点。</p>
          </div><span class="welcome-edition">YOUR QUIET<br>MUSIC SPACE <i>01 —</i></span>
        </div>
        <div class="welcome-grid">
          <section class="import-card glass" :class="{ 'is-dragging': dragOver }" aria-label="导入钢琴谱">
            <span class="card-eyebrow">从一份乐谱开始</span>
            <div class="upload-symbol">
              <UIcon name="i-lucide-file-music" /><span>
                <UIcon name="i-lucide-plus" />
              </span>
            </div>
            <h2>把旋律带进来</h2>
            <p>将钢琴谱拖到这里<br>或从你的设备中选择文件</p>
            <UButton icon="i-lucide-upload" label="选择乐谱" size="xl" class="upload-cta" :loading="store.uploading"
              @click="pick" />
            <span class="upload-formats">PDF · JPG · PNG <i /> PDF 最多 30 页，最大 20 MB</span>
            <label class="scan-enhancement"><input v-model="store.enhanceScan" type="checkbox"
                :disabled="store.uploading"><span>光照增强 <em>实验</em><small>适合光照不均的扫描谱；保留原件</small></span></label>
            <div class="sample-divider"><span /> 或者，先轻轻试一试 <span /></div>
            <button class="sample-button" :disabled="store.uploading" @click="store.example()"><span
                class="sample-icon">
                <UIcon name="i-lucide-play" />
              </span><span><strong>打开《小步舞曲》</strong><small>用一份真实钢琴谱，体验完整流程</small></span>
              <UIcon name="i-lucide-arrow-up-right" />
            </button>
          </section>

          <section class="preview-scene" aria-label="真实谱面预览">
            <div class="scene-glow" />
            <div class="scene-orbit orbit-one" />
            <div class="scene-orbit orbit-two" />
            <div class="floating-note note-one">♩</div>
            <div class="floating-note note-two">♫</div>
            <div class="sample-paper">
              <div class="sample-paper-header"><span>PIANO COLLECTION</span>
                <UIcon name="i-lucide-music-2" />
              </div>
              <h3>Minuet</h3>
              <p>一首小步舞曲，一次从容的开始。</p><img src="/samples/minuet-preview.png" alt="小步舞曲真实原谱与黑色简谱标注局部">
              <div class="sample-paper-footer"><span>ORIGINAL SCORE, WITH A LITTLE HELP.</span><span>01</span></div>
            </div>
            <div class="floating-badge glass"><span class="check-orb">
                <UIcon name="i-lucide-check" />
              </span><span><strong>原谱的美，完整保留</strong><small>在留白处，找到音符的归属</small></span></div>
            <span class="scene-caption">A FAMILIAR NOTE. A NEW BEGINNING.</span>
          </section>
        </div>
        <section class="feature-strip" aria-label="使用流程">
          <div><span class="feature-icon">
              <UIcon name="i-lucide-scan-line" />
            </span>
            <p><strong>原谱，依然是原谱</strong><small>不重排，不改变熟悉的谱面</small></p><span class="feature-number">01</span>
          </div>
          <div><span class="feature-icon">
              <UIcon name="i-lucide-pencil-line" />
            </span>
            <p><strong>每个音符，都能校对</strong><small>调整音高与位置，读懂再奏响</small></p><span class="feature-number">02</span>
          </div>
          <div><span class="feature-icon">
              <UIcon name="i-lucide-printer" />
            </span>
            <p><strong>带着清晰，开始练习</strong><small>自选字号与颜色，保存或打印</small></p><span class="feature-number">03</span>
          </div>
        </section>
        <footer class="welcome-footer"><span>谱间 · 给音乐一点留白</span><span>识别结果需人工校对，简谱标注并非指法。</span></footer>
      </main>

      <main v-else class="score-workspace">
        <div class="document-header">
          <div>
            <p class="eyebrow">YOUR SCORE, A LITTLE CLEARER.</p>
            <h1>{{ store.active.name.replace(/\.[^.]+$/, '') }}</h1>
            <p class="document-status" role="status"><span class="status-dot"
                :class="{ amber: store.dirty || failed, pulse: store.processing || store.exporting }" />{{ stateText
                }}<span v-if="store.annotations" class="note-count">{{ store.notes.length }} 个音符</span><span
                v-if="store.multiPage" class="note-count">第 {{ store.pageNumber }} / {{ store.documentJob?.pageCount }}
                页</span></p>
            <p v-if="store.job?.preprocessing === 'Adaptive'" class="enhancement-status">本次使用光照增强（实验），原件已保留。</p>
          </div>
          <div class="document-actions">
            <UButton v-if="store.revision" icon="i-lucide-check" label="保存修改" :loading="store.saving"
              :disabled="!store.dirty || !store.editable" :title="saveHint" class="save-button" @click="store.save()" />
            <UButton icon="i-lucide-download" :label="store.multiPage ? '导出本页' : '导出 PDF'" color="neutral"
              variant="outline" :disabled="store.revision?.state !== 'Ready' || store.dirty"
              @click="store.download()" />
            <UButton v-if="store.multiPage" icon="i-lucide-files"
              :label="store.documentExport?.state === 'Ready' ? '下载整份 PDF' : store.documentExport?.state === 'Failed' ? '重试整份导出' : '导出整份 PDF'"
              color="primary"
              :loading="store.documentExport?.state === 'Queued' || store.documentExport?.state === 'Processing'"
              :disabled="!allPagesReady || store.dirty || store.documentExport?.state === 'Queued' || store.documentExport?.state === 'Processing'"
              @click="store.documentExport?.state === 'Ready' ? store.downloadDocument() : store.exportDocument()" />
            <UButton icon="i-lucide-sliders-horizontal" color="neutral" :variant="inspector ? 'soft' : 'ghost'"
              aria-label="切换校对面板" @click="inspector = !inspector" />
          </div>
        </div>

        <nav v-if="store.multiPage" class="page-navigation glass" aria-label="PDF 页码导航">
          <div class="page-navigation-heading"><strong>逐页识别与校对</strong><span>{{store.pages.filter(p => p.state ===
            'Ready').length}} / {{ store.documentJob?.pageCount }} 页识别完成</span></div>
          <div class="page-navigation-list">
            <button v-for="page in store.pages" :key="page.id" type="button" class="page-navigation-item"
              :class="{ active: store.pageNumber === page.number, failed: page.state === 'Failed' }"
              :aria-current="store.pageNumber === page.number ? 'page' : undefined"
              @click="store.selectPage(page.number)">
              <span class="page-navigation-number">{{ String(page.number).padStart(2, '0') }}</span>
              <span class="page-navigation-state">{{ pageStatus(page) }}</span>
            </button>
          </div>
          <p v-if="store.documentExport?.state === 'Failed'" class="page-export-error" role="alert">整份 PDF 合并失败（{{
            store.documentExport.errorCode }}）。逐页修订仍保留，可再次发起导出。</p>
          <p v-else class="page-export-hint">整份导出按原页序合并；全部页面识别并排版完成后即可生成。</p>
        </nav>

        <div v-if="store.processing || store.exporting || failed || store.job?.state === 'Cancelled'"
          class="processing-card glass" aria-live="polite">
          <div class="processing-symbol">
            <UIcon
              :name="failed ? 'i-lucide-circle-alert' : store.job?.state === 'Cancelled' ? 'i-lucide-pause' : 'i-lucide-loader-circle'"
              :class="{ spin: store.processing || store.exporting }" />
          </div>
          <div><strong>{{ stateText }}</strong>
            <p>{{ failed ? failureDescription : store.job?.state === 'Cancelled' ? '原文件和记录仍然保留，可以随时重新识别。' :
              '原谱已经妥善保留，完成后将在这里显示标注。'
            }}</p>
          </div>
          <UButton v-if="['Queued', 'Processing'].includes(store.job?.state ?? '')" label="取消识别" color="neutral"
            variant="ghost" @click="store.command('cancel')" />
          <UButton
            v-if="!unsupportedInput && !deterministicFailure && ['Failed', 'Cancelled'].includes(store.job?.state ?? '')"
            label="重新识别" color="neutral" variant="soft" @click="store.command('retry')" />
          <UButton v-if="unsupportedInput" label="重新选择乐谱" color="neutral" variant="soft" @click="pick" />
        </div>

        <div class="editor-layout" :class="{ 'show-inspector': inspector && scoreView === 'original' }">
          <section class="sheet-panel" aria-label="原谱工作区">
            <div class="sheet-toolbar glass">
              <div class="sheet-toolbar-left"><span class="paper-icon">
                  <UIcon name="i-lucide-file-music" />
                </span><span class="page-label">{{ scoreView === 'musicxml' ? 'MusicXML' : '原谱叠加' }} <small>{{ String(store.pageNumber).padStart(2, '0') }} / {{
                  String(store.documentJob?.pageCount ?? 1).padStart(2, '0') }}</small></span><span
                  v-if="store.revision" class="version-tag">v{{ store.revision.version }}</span>
                <UButton v-if="store.historical" size="xs" color="neutral" variant="soft" label="回到最新版"
                  @click="store.showVersion(store.job!.currentRevision)" />
              </div>
              <div class="sheet-toolbar-right">
                <UTooltip text="撤销 · ⌘ Z">
                  <UButton icon="i-lucide-undo-2" color="neutral" variant="ghost" size="sm" aria-label="撤销"
                    :disabled="!store.canUndo || !store.editable" @click="store.undo()" />
                </UTooltip>
                <UTooltip text="重做 · ⌘ ⇧ Z">
                  <UButton icon="i-lucide-redo-2" color="neutral" variant="ghost" size="sm" aria-label="重做"
                    :disabled="!store.canRedo || !store.editable" @click="store.redo()" />
                </UTooltip><span class="toolbar-divider" />
                <USelect v-if="store.job?.currentRevision" :model-value="store.revision?.version"
                  :items="Array.from({ length: store.job.currentRevision }, (_, i) => ({ label: `版本 ${i + 1}`, value: i + 1 })).reverse()"
                  size="sm" aria-label="修订版本" class="version-select"
                  @update:model-value="store.showVersion(Number($event))" />
              </div>
            </div>
            <div v-if="store.historical" class="history-notice">
              <UIcon name="i-lucide-history" /> 正在查看已保存的历史版本，回到最新版后可继续编辑。
            </div>
            <div v-if="store.revision" class="flex flex-wrap gap-2 px-4 py-3" aria-label="乐谱视图">
              <UButton label="原谱标注" :variant="scoreView === 'original' ? 'soft' : 'ghost'" color="neutral" :aria-pressed="scoreView === 'original'" @click="scoreView = 'original'" />
              <UButton label="MusicXML 预览" :variant="scoreView === 'musicxml' ? 'soft' : 'ghost'" color="neutral" :aria-pressed="scoreView === 'musicxml'" @click="scoreView = 'musicxml'" />
            </div>
            <div v-if="store.revision && scoreView === 'original'" class="score-filters">
              <USelect v-model="staff" :items="staffOptions" size="sm" aria-label="选择谱表" />
              <USelect v-model="voice" :items="voiceOptions" size="sm" aria-label="选择声部" /><button v-if="store.pending"
                class="pending-chip" @click="canvas?.nextPending()"><span class="status-dot amber" /> {{ store.pending
                }}
                个待放置</button>
            </div>
            <ClientOnly>
              <MusicXmlPreview v-if="scoreView === 'musicxml' && store.revision" :key="store.currentJobId" :zoom="zoom" />
              <ScoreCanvas v-else ref="canvas" :zoom="zoom" :labels="labels" :staff="staff" :voice="voice" /><template
                #fallback>
                <div class="preview-loading">正在铺开乐谱</div>
              </template>
            </ClientOnly>
            <div class="view-dock glass">
              <div v-if="scoreView === 'original'" class="label-switch"><button :class="{ active: !labels }" :aria-pressed="!labels"
                  @click="labels = false">原谱</button><button :class="{ active: labels }" :aria-pressed="labels"
                  @click="labels = true">简谱标注</button></div><span class="toolbar-divider" />
              <UButton icon="i-lucide-minus" color="neutral" variant="ghost" size="sm" aria-label="缩小"
                :disabled="zoom <= 50" @click="zoom = Math.max(50, zoom - 25)" /><select v-model.number="zoom"
                aria-label="缩放比例">
                <option v-for="n in [50, 75, 100, 125, 150, 175, 200]" :key="n" :value="n">{{ n }}%</option>
              </select>
              <UButton icon="i-lucide-plus" color="neutral" variant="ghost" size="sm" aria-label="放大"
                :disabled="zoom >= 200" @click="zoom = Math.min(200, zoom + 25)" />
              <UButton icon="i-lucide-maximize" color="neutral" variant="ghost" size="sm" aria-label="适合页面"
                @click="zoom = 100" />
            </div>
          </section>
          <ScoreInspector v-if="store.revision && scoreView === 'original'" @place="canvas?.beginPlaceCurrent()" @pending="canvas?.nextPending()"
            @close="inspector = false" />
        </div>
      </main>
    </div>

    <UModal v-model:open="help" title="给音乐一点留白" description="在原谱上读懂音符，从容地开始练习。" :ui="{ content: 'guide-modal' }">
      <template #body>
        <div class="guide-body">
          <ol>
            <li><strong>导入钢琴谱</strong>
              <p>支持 20 MB 以内、最多 30 页的 PDF，以及 JPG 和 PNG。PDF 会逐页识别，图片作为单页处理。</p>
            </li>
            <li><strong>在原谱上，慢慢校对</strong>
              <p>点击符头修改音高，拖动数字调整位置。可在校对面板选择标注字号与颜色；无法避让原谱的标注会保留为待放置，保存后再确认。</p>
            </li>
            <li><strong>保存修订，再带走旋律</strong>
              <p>每次保存都有独立版本，导出与已保存的版本一致。本机记录包含访问凭据，清理浏览器数据后请重新上传。</p>
            </li>
          </ol>
          <div class="shortcut-grid"><span>下一个 / 上一个音符</span><kbd>J /
              K</kbd><span>微调标注位置</span><kbd>方向键</kbd><span>保存修改</span><kbd>⌘ / Ctrl S</kbd><span>撤销 / 重做</span><kbd>⌘ Z
              / ⇧ ⌘ Z</kbd><span>隐藏 / 显示标注</span><kbd>Delete</kbd></div>
          <p class="guide-note">首调需确认主音；自然小调的主音记为 6。小节编号从本页第一物理小节开始，含弱起小节。自动识别不等于人工校对，数字表示音高，不表示指法。</p><a
            href="/samples/README.md" target="_blank" rel="noopener" class="sample-credit">示例乐谱来源与许可
            <UIcon name="i-lucide-arrow-up-right" />
          </a>
        </div>
      </template>
    </UModal>
  </div>
</template>
