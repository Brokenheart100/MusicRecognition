<script setup lang="ts">
type ScoreView = 'original' | 'musicxml'
type CanvasHandle = {
  beginPlaceCurrent: () => void
  cancelInteraction: () => void
  nextPending: () => void
  nextUnannotated: () => void
  readonly unannotatedCount: number
  moveSelection: (dx: number, dy: number) => void
}

const emit = defineEmits<{ pick: [] }>()
const store = useWorkspaceStore()
const { t } = useI18n()
const inspector = defineModel<boolean>('inspector', { required: true })
const zoom = defineModel<number>('zoom', { required: true })
const labels = defineModel<boolean>('labels', { required: true })
const staff = defineModel<string>('staff', { required: true })
const voice = defineModel<string>('voice', { required: true })
const scoreView = defineModel<ScoreView>('scoreView', { required: true })
const canvas = ref<CanvasHandle>()
const unannotatedCount = computed(() => canvas.value?.unannotatedCount ?? 0)

defineExpose({
  beginPlaceCurrent: () => canvas.value?.beginPlaceCurrent(),
  cancelInteraction: () => canvas.value?.cancelInteraction(),
  nextPending: () => canvas.value?.nextPending(),
  nextUnannotated: () => canvas.value?.nextUnannotated(),
  moveSelection: (dx: number, dy: number) => canvas.value?.moveSelection(dx, dy),
})

const zoomLevels = [50, 75, 100, 125, 150, 175, 200] as const
const zoomOptions: { label: string; value: number }[] = zoomLevels.map(n => ({ label: `${n}%`, value: n }))
const labelItems = computed(() => [
  { label: t('score.labelsOriginal'), value: 'original' },
  { label: t('score.labelsNumbered'), value: 'numbered' },
])
const labelMode = computed({
  get: () => labels.value ? 'numbered' : 'original',
  set: (value: string | number) => { labels.value = value === 'numbered' },
})
const scoreViewItems = computed(() => [
  { label: t('score.viewOriginal'), value: 'original' },
  { label: t('score.viewMusicxml'), value: 'musicxml' },
])
const scoreViewMode = computed({
  get: () => scoreView.value,
  set: (value: string | number) => { scoreView.value = value === 'musicxml' ? 'musicxml' : 'original' },
})
const staffOptions = computed(() => [{ label: t('score.staffAll'), value: 'all' }, ...[...new Set(store.notes.map(n => n.staff))].sort((a, b) => a - b).map(n => ({ label: t('score.staffN', { n }), value: String(n) }))])
const voiceOptions = computed(() => [{ label: t('score.voiceAll'), value: 'all' }, ...[...new Set(store.notes.map(n => n.voice))].sort((a, b) => a - b).map(n => ({ label: n ? t('score.voiceN', { n }) : t('score.voiceUnassigned'), value: String(n) }))])
const versionItems = computed(() => Array.from({ length: store.job?.currentRevision ?? 0 }, (_, i) => ({ label: t('score.version', { n: i + 1 }), value: i + 1 })).reverse())
const allPagesReady = computed(() => store.multiPage && store.pages.length === store.documentJob?.pageCount && store.pages.every(p => p.state === 'Ready' && p.exportState === 'Ready'))
function pageStatus(page: { state: string; exportState: string | null }) {
  if (page.state === 'Failed') return t('pageStatus.failed')
  if (page.state === 'Queued' || page.state === 'Processing') return t('pageStatus.processing')
  if (page.state === 'Cancelled') return t('pageStatus.cancelled')
  if (page.exportState === 'Queued' || page.exportState === 'Processing') return t('pageStatus.layout')
  return page.state === 'Ready' && page.exportState === 'Ready' ? t('pageStatus.ready') : t('pageStatus.waiting')
}
const saveHint = computed(() => {
  if (!store.revision) return t('saveHint.noRevision')
  if (store.exporting) return t('saveHint.exporting')
  if (store.historical) return t('saveHint.historical')
  if (store.saving) return t('saveHint.saving')
  if (!store.editable) return t('saveHint.notEditable')
  if (!store.dirty) return t('saveHint.unchanged')
  return t('saveHint.ready')
})
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
const failed = computed(() => store.job?.state === 'Failed' || store.revision?.state === 'Failed')
const unsupportedInput = computed(() => ['unsupported-page-count', 'encrypted-pdf'].includes(store.job?.errorCode ?? ''))
const deterministicFailure = computed(() => ['unsupported-notation', 'invalid-note-count', 'pitch-consistency-failed'].includes(store.job?.errorCode ?? ''))
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
  <main class="score-workspace">
    <div class="document-header">
      <div>
        <p class="eyebrow">YOUR SCORE, A LITTLE CLEARER.</p>
        <h1>{{ store.active?.name.replace(/\.[^.]+$/, '') }}</h1>
        <p class="document-status" role="status"><span class="status-dot"
            :class="{ amber: store.dirty || failed, pulse: store.processing || store.exporting }" />{{ stateText
            }}<span v-if="store.annotations" class="note-count">{{ $t('score.noteCount', { count:
              store.notes.length }) }}</span><span v-if="store.multiPage" class="note-count">{{
                $t('score.pageOf', { page: store.pageNumber, total: store.documentJob?.pageCount }) }}</span></p>
        <p v-if="store.job?.preprocessing === 'Adaptive'" class="enhancement-status">{{
          $t('score.enhancementUsed') }}</p>
        <p v-if="store.job?.engine === 'Homr'" class="enhancement-status">{{ $t('score.homrUsed') }}</p>
      </div>
      <div class="document-actions">
        <UButton v-if="store.revision" icon="i-lucide-check" :label="$t('score.save')" :loading="store.saving"
          :disabled="!store.dirty || !store.editable" :title="saveHint" aria-keyshortcuts="Control+S Meta+S"
          class="save-button" @click="store.save()" />
        <UButton icon="i-lucide-download"
          :label="store.multiPage ? $t('score.exportPage') : $t('score.exportPdf')" color="neutral" variant="outline"
          :disabled="store.revision?.state !== 'Ready' || store.dirty" @click="store.download()" />
        <UButton v-if="store.multiPage" icon="i-lucide-files"
          :label="store.documentExport?.state === 'Ready' ? $t('score.downloadDocument') : store.documentExport?.state === 'Failed' ? $t('score.retryDocumentExport') : $t('score.exportDocument')"
          color="primary"
          :loading="store.documentExport?.state === 'Queued' || store.documentExport?.state === 'Processing'"
          :disabled="!allPagesReady || store.dirty || store.documentExport?.state === 'Queued' || store.documentExport?.state === 'Processing'"
          @click="store.documentExport?.state === 'Ready' ? store.downloadDocument() : store.exportDocument()" />
        <UButton icon="i-lucide-sliders-horizontal" color="neutral" :variant="inspector ? 'soft' : 'ghost'"
          :aria-label="$t('score.toggleInspector')" @click="inspector = !inspector" />
      </div>
    </div>

    <div v-if="store.job" class="recognition-actions" role="group"
      :aria-label="$t('score.recognitionActionsAria')">
      <UButton icon="i-lucide-refresh-cw" size="sm" color="neutral" variant="soft"
        :label="$t('score.rerecognizePage')" :loading="store.processing"
        :disabled="!store.currentJobId || store.processing || store.exporting"
        @click="store.rerecognizePage()" />
      <UButton v-if="unannotatedCount" icon="i-lucide-list-checks" size="sm"
        color="warning" variant="soft"
        :label="$t('score.unannotatedAction', { count: unannotatedCount })"
        :disabled="store.processing || store.exporting" @click="canvas?.nextUnannotated()" />
    </div>

    <nav v-if="store.multiPage" class="page-navigation glass" :aria-label="$t('score.pageNavAria')">
      <div class="page-navigation-heading"><strong>{{ $t('score.pageNavTitle') }}</strong><span>{{
        $t('score.pageNavDone', { done: store.pages.filter(p => p.state === 'Ready').length, total:
          store.documentJob?.pageCount }) }}</span></div>
      <div class="page-navigation-list">
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
      <p v-if="store.documentExport?.state === 'Failed'" class="page-export-error" role="alert">{{
        $t('score.exportFailed', { code: store.documentExport.errorCode }) }}</p>
      <p v-else class="page-export-hint">{{ $t('score.exportHint') }}</p>
    </nav>

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
      <UButton v-if="['Queued', 'Processing'].includes(store.job?.state ?? '')"
        :label="$t('score.cancelRecognition')" color="neutral" variant="ghost" @click="store.command('cancel')" />
      <UButton
        v-if="!unsupportedInput && !deterministicFailure && ['Failed', 'Cancelled'].includes(store.job?.state ?? '')"
        :label="$t('score.retryRecognition')" color="neutral" variant="soft" @click="store.command('retry')" />
      <UButton v-if="unsupportedInput" :label="$t('score.chooseAnother')" color="neutral" variant="soft"
        @click="emit('pick')" />
    </div>

    <div class="editor-layout" :class="{ 'show-inspector': inspector && scoreView === 'original' }">
      <section class="sheet-panel" :aria-label="$t('score.sheetAria')">
        <div class="sheet-toolbar glass">
          <div class="sheet-toolbar-left"><span class="paper-icon">
              <UIcon name="i-lucide-file-music" />
            </span><span class="page-label">{{ scoreView ===
              'musicxml' ? 'MusicXML' : $t('score.overlay') }} <small>{{
                String(store.pageNumber).padStart(2, '0') }} / {{
                String(store.documentJob?.pageCount ?? 1).padStart(2, '0') }}</small></span><span
              v-if="store.revision" class="version-tag">v{{ store.revision.version }}</span>
            <UButton v-if="store.historical" size="xs" color="neutral" variant="soft"
              :label="$t('score.backToLatest')" @click="store.showVersion(store.job?.currentRevision ?? 1)" />
          </div>
          <div class="sheet-toolbar-right">
            <UTooltip :text="$t('score.undoTitle')">
              <UButton icon="i-lucide-undo-2" color="neutral" variant="ghost" size="sm" :aria-label="$t('score.undo')"
                :disabled="!store.canUndo || !store.editable" @click="store.undo()" />
            </UTooltip>
            <UTooltip :text="$t('score.redoTitle')">
              <UButton icon="i-lucide-redo-2" color="neutral" variant="ghost" size="sm" :aria-label="$t('score.redo')"
                :disabled="!store.canRedo || !store.editable" @click="store.redo()" />
            </UTooltip><span class="toolbar-divider" />
            <USelect v-if="store.job?.currentRevision" :model-value="store.revision?.version" :items="versionItems"
              size="sm" :aria-label="$t('score.versionAria')" class="version-select"
              @update:model-value="store.showVersion(Number($event))" />
          </div>
        </div>
        <div v-if="store.historical" class="history-notice">
          <UIcon name="i-lucide-history" /> {{ $t('score.historyNotice') }}
        </div>
        <div v-if="store.revision" class="flex flex-wrap gap-2 px-4 py-3" role="group"
          :aria-label="$t('score.viewsAria')">
          <UTabs v-model="scoreViewMode" :items="scoreViewItems" :content="false" size="xs" class="shrink-0"
            :ui="{ trigger: 'text-[11px] px-3' }" />
        </div>
        <div v-if="store.revision && scoreView === 'original'" class="score-filters">
          <USelect v-model="staff" :items="staffOptions" size="sm" :aria-label="$t('score.selectStaff')" />
          <USelect v-model="voice" :items="voiceOptions" size="sm" :aria-label="$t('score.selectVoice')" />
          <UButton v-if="store.pending" color="warning" variant="soft" size="xs"
            class="ms-auto shrink-0 whitespace-nowrap" :ui="{ base: 'text-[10px]' }"
            @click="canvas?.nextPending()">
            <span class="status-dot amber" /> {{ $t('score.pendingChip', { count: store.pending }) }}
          </UButton>
        </div>
        <ClientOnly>
          <MusicXmlPreview v-if="scoreView === 'musicxml' && store.revision" :key="store.currentJobId"
            :zoom="zoom" />
          <ScoreCanvas v-else ref="canvas" :zoom="zoom" :labels="labels" :staff="staff" :voice="voice" /><template
            #fallback>
            <div class="preview-loading">{{ $t('score.loading') }}</div>
          </template>
        </ClientOnly>
        <div class="view-dock glass">
          <UTabs v-if="scoreView === 'original'" v-model="labelMode" :items="labelItems" :content="false" size="xs"
            class="shrink-0" :ui="{ trigger: 'text-[10px]' }" /><span class="toolbar-divider" />
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
      <ScoreInspector v-if="store.revision && scoreView === 'original'" @place="canvas?.beginPlaceCurrent()"
        @pending="canvas?.nextPending()" @close="inspector = false" />
    </div>
  </main>
</template>
