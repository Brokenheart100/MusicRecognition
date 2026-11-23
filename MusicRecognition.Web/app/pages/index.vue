<script setup lang="ts">
import { onKeyStroke, useActiveElement, useEventListener } from '@vueuse/core'
import type { RecentScore } from '~~/shared/types/score'

type CanvasHandle = {
  beginPlaceCurrent: () => void
  cancelInteraction: () => void
  nextPending: () => void
  moveSelection: (dx: number, dy: number) => void
}

const { t } = useI18n()
const store = useWorkspaceStore()
const activeElement = useActiveElement()
const editingFocus = computed(() => !!activeElement.value?.closest('input,textarea,select,[role="combobox"],[role="listbox"],[role="option"],[role="menu"],[role="dialog"],[contenteditable]:not([contenteditable="false"])'))
const fileInput = ref<HTMLInputElement>()
const canvas = ref<CanvasHandle>()
const sidebar = ref(false)
const collapsed = ref(false)
const inspector = ref(true)
const help = ref(false)
// Keep the guide eager-free: load once on first open, then retain it for close transitions.
const guideMounted = ref(false)
const toast = useToast()
const promptedPages = new Set<string>()
const zoom = ref(100)
const labels = ref(true)
const scoreView = ref<'original' | 'musicxml'>('original')
const staff = ref('all')
const voice = ref('all')

function pick() { fileInput.value?.click() }
async function choose(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]; if (file) { sidebar.value = false; await store.upload(file) }
  input.value = ''
}
async function open(item: RecentScore) { sidebar.value = false; staff.value = 'all'; voice.value = 'all'; await store.open(item) }
function moveSelected(dx: number, dy: number) {
  canvas.value?.moveSelection(dx, dy)
}
function keyboard(event: KeyboardEvent) {
  const mod = event.metaKey || event.ctrlKey, key = event.key.toLowerCase()
  // Saving/help remain available from fields. Text editing retains native
  // select-all and undo, and other dialogs/popovers keep their own keyboard flow.
  if (mod && !event.altKey && !event.shiftKey && key === 'k') {
    event.preventDefault(); if (!event.repeat) help.value = !help.value; return
  }
  if (help.value || activeElement.value?.closest('[role="dialog"],[role="listbox"],[role="menu"]')) return
  if (mod && !event.altKey && !event.shiftKey && key === 's') { event.preventDefault(); if (!event.repeat) void store.save(); return }
  if (editingFocus.value || event.altKey) return
  if (mod) {
    if (key === 'o' && !event.shiftKey) { event.preventDefault(); if (!event.repeat && !store.uploading) pick(); return }
    if (key === 'e' && event.shiftKey && scoreView.value === 'original' && store.revision) {
      event.preventDefault(); if (!event.repeat) void store.download(); return
    }
    if (scoreView.value !== 'original' || !store.revision) return
    if (key === 'z') { event.preventDefault(); if (!event.repeat) { if (event.shiftKey) store.redo(); else store.undo() }; return }
    if (key === 'y' && !event.shiftKey) { event.preventDefault(); if (!event.repeat) store.redo(); return }
    if (key === 'a' && !event.shiftKey) { event.preventDefault(); if (!event.repeat) store.select(filteredNotes().map(n => n.id)); return }
    return // Never interpret browser shortcuts such as Ctrl+J as note navigation.
  }
  if (key === '?' && !event.repeat) { event.preventDefault(); help.value = true; return }
  if (!store.active) return
  if (event.key === 'Escape') { canvas.value?.cancelInteraction(); store.clearSelection(); return }
  if (!event.repeat) {
    if (key === 'b') { event.preventDefault(); if (window.innerWidth >= 1020) collapsed.value = !collapsed.value; else sidebar.value = !sidebar.value; return }
    if (key === 'i' && store.revision && scoreView.value === 'original') { event.preventDefault(); inspector.value = !inspector.value; return }
    if (key === 'l' && store.revision && scoreView.value !== 'musicxml') { event.preventDefault(); labels.value = !labels.value; return }
    if ((key === '[' || key === ']') && store.multiPage) { event.preventDefault(); void store.selectPage(store.pageNumber + (key === ']' ? 1 : -1)); return }
  }
  if (!store.revision) return
  if (['+', '=', '-', '0'].includes(key)) {
    event.preventDefault(); zoom.value = key === '0' ? 100 : Math.max(50, Math.min(200, zoom.value + (key === '-' ? -25 : 25))); return
  }
  if (scoreView.value !== 'original') return
  const delta = event.shiftKey ? 10 : 1
  const direction: Record<string, [number, number]> = { ArrowLeft: [-delta, 0], ArrowRight: [delta, 0], ArrowUp: [0, -delta], ArrowDown: [0, delta] }
  if (store.selectedId && direction[event.key]) { event.preventDefault(); moveSelected(...direction[event.key]!); return }
  if (['j', 'k'].includes(key) && !event.repeat) {
    event.preventDefault()
    const notes = filteredNotes()
    const index = notes.findIndex(n => n.id === store.selectedId)
    const next = index < 0 ? (key === 'j' ? 0 : notes.length - 1) : (index + (key === 'j' ? 1 : -1) + notes.length) % notes.length
    store.select(notes[next] ? [notes[next]!.id] : [])
    inspector.value = true
  }
  if ((event.key === 'Delete' || event.key === 'Backspace') && store.selectedId) {
    event.preventDefault(); if (!event.repeat) store.hideMany(store.selection.length ? store.selection : [store.selectedId])
  }
  if (key === 'n' && !event.repeat) { event.preventDefault(); canvas.value?.nextPending() }
  if (key === 'p' && store.selectedId && store.editable && !event.repeat) { event.preventDefault(); canvas.value?.beginPlaceCurrent() }
}
function filteredNotes() { return store.notes.filter(n => (staff.value === 'all' || String(n.staff) === staff.value) && (voice.value === 'all' || String(n.voice) === voice.value)) }
function beforeLeave(event: BeforeUnloadEvent) { if (store.dirty) { event.preventDefault(); event.returnValue = '' } }
onKeyStroke(event => !event.defaultPrevented && !event.isComposing, keyboard)
useEventListener('beforeunload', beforeLeave)
onMounted(() => { inspector.value = window.innerWidth >= 1020; void store.init() })
onBeforeUnmount(() => { store.dispose() })
watch(() => store.selectedId, value => { if (value) inspector.value = true })
watch(help, value => { if (value) guideMounted.value = true })
// Prompt once per recognized page; refetches and draft saves must not repeat it.
watch(
  () => [store.currentJobId, store.pageNumber, store.revision] as const,
  ([jobId, page, revision]) => {
    if (!jobId || !revision) return
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
    <input ref="fileInput" class="sr-only" type="file" accept=".pdf,.jpg,.jpeg,.png" :aria-label="$t('shell.chooseFileAria')"
      @change="choose">
    <button v-if="sidebar" class="sidebar-scrim" :aria-label="$t('shell.closeSidebar')" @click="sidebar = false" />
    <WorkspaceSidebar @request-import="pick" @select="open" @help="help = true" @close="sidebar = false" />

    <div class="main-shell">
      <WorkspaceTopbar @toggle-collapse="collapsed = !collapsed" @open-sidebar="sidebar = true"
        @open-help="help = true" />

      <UAlert v-if="store.error" class="error-banner" color="error" variant="soft" icon="i-lucide-circle-alert"
        :description="store.error" orientation="horizontal" close role="alert"
        :actions="[{ label: $t('shell.retryConnection'), color: 'neutral', variant: 'ghost', onClick: () => store.poll() }]"
        :ui="{ description: 'text-[11px] opacity-100', close: 'p-1' }" @update:open="store.error = ''" />

      <WelcomePanel v-if="!store.active" @request-import="pick" />
      <LazyScoreWorkspace v-else ref="canvas" v-model:inspector="inspector" v-model:zoom="zoom" v-model:labels="labels"
        v-model:staff="staff" v-model:voice="voice" v-model:score-view="scoreView" @pick="pick" />
    </div>

    <LazyKeyboardGuide v-if="guideMounted" v-model="help" />
  </div>
</template>
