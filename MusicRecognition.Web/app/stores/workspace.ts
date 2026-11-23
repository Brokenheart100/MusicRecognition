import { acceptHMRUpdate, defineStore, skipHydrate } from 'pinia'
import { StorageSerializers, useSessionStorage, useStorage as useVueUseStorage } from '@vueuse/core'
import { defaultAnnotationStyle, RecentScoreSchema, type AnnotationStyle, type Draft, type Numbering, type Pitch, type RecentScore } from '~~/shared/types/score'
import { applyDraftEdit, draftFingerprint, emptyDraft, normalizeDraft, parseStoredDraft, revisionBaseline, revisionPayload, snapshotPlacements, snapshotStyles, useDraftHistory } from '~/utils/draft'
import { useConfirmStore } from './confirm'

const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value))
const storageKey = 'pujian:scores:v1'

export const useWorkspaceStore = defineStore('workspace', () => {
  const { t } = useI18n()
  const confirmation = useConfirmStore()
  const error = ref('')
  const storageOptions = {
    initOnMounted: true, writeDefaults: false, flush: 'sync' as const, listenToStorageChanges: false,
    onError: () => { error.value = t('store.storageError') },
  }
  // Server state must never overwrite browser-owned records during Pinia hydration.
  const recent = skipHydrate(useVueUseStorage<RecentScore[]>(storageKey, [], undefined, {
    ...storageOptions,
    serializer: {
      read: (raw) => {
        try {
          const values: unknown = JSON.parse(raw)
          return Array.isArray(values) ? values.flatMap(value => {
            const parsed = RecentScoreSchema.safeParse(value)
            return parsed.success ? [parsed.data] : []
          }).slice(0, 40) : []
        } catch { return [] }
      },
      write: JSON.stringify,
    },
  }))
  const activeId = useVueUseStorage<string | null>('pujian:active', null, undefined, { ...storageOptions, serializer: StorageSerializers.string })
  const pendingUpload = useSessionStorage<{ identity: string; token: string; key: string } | null>('pujian:upload', null, {
    ...storageOptions,
    serializer: {
      read: (raw) => {
        try {
          const value = JSON.parse(raw)
          return typeof value?.identity === 'string' && /^[a-f0-9]{64}$/.test(value?.token) && typeof value?.key === 'string' ? value : null
        } catch { return null }
      },
      write: JSON.stringify,
    },
  })
  const active = ref<RecentScore | null>(null)
  const pageNumber = ref(1)
  const selectedVersion = ref(0)
  const server = useScoreServer(active, pageNumber, selectedVersion)
  const { job, documentJob, documentExport: latestDocumentExport, revision, annotations, multiPage, currentJobId } = server
  const source = shallowRef<{ url: string; pdf: boolean; blob: Blob; pageId?: string } | null>(null)
  const selectedId = ref<string | null>(null)
  // Shift/ctrl click and the canvas marquee keep a set; selectedId stays the inspector's primary note.
  const selection = ref<string[]>([])
  let syncingSelection = false
  const draft = ref<Draft>(emptyDraft())
  // Every edit replaces draft.value instead of mutating it, so snapshots can share the old object.
  const history = useDraftHistory(draft)
  const { canUndo, canRedo } = history
  // The key changes only after a revision has loaded, before restoring its draft.
  const storedDraftKey = ref('pujian:draft:inactive')
  const storedDraft = useVueUseStorage<Draft | null>(storedDraftKey, null, undefined, {
    ...storageOptions,
    serializer: {
      read: parseStoredDraft,
      write: JSON.stringify,
    },
  })
  const uploading = ref(false)
  const enhanceScan = ref(false)
  const recognitionEngine = ref<'Audiveris' | 'Homr'>('Audiveris')
  const saving = ref(false)
  const ready = ref(false)
  let epoch = 0
  let restoring = false

  const notes = computed(() => [...(revision.value?.score.notes ?? [])].sort((a, b) => a.measure - b.measure || a.staff - b.staff || a.bounds.x - b.bounds.x || a.bounds.y - b.bounds.y))
  const selected = computed(() => notes.value.find(n => n.id === selectedId.value) ?? null)
  const selectedNotes = computed(() => notes.value.filter(n => selection.value.includes(n.id)))
  // Any direct selectedId write (canvas click, keyboard focus) keeps the set consistent.
  watch(selectedId, id => {
    if (syncingSelection) return
    if (!id) { if (selection.value.length) selection.value = []; return }
    if (!selection.value.includes(id)) selection.value = [id]
  }, { flush: 'sync' })
  function select(ids: string[]) {
    syncingSelection = true
    selection.value = [...new Set(ids.filter(id => notes.value.some(n => n.id === id)))]
    selectedId.value = selection.value[0] ?? null
    syncingSelection = false
  }
  function toggle(id: string) { select(selection.value.includes(id) ? selection.value.filter(value => value !== id) : [...selection.value, id]) }
  function selectRegion(ids: string[], additive = false) { select(additive ? [...selection.value, ...ids] : ids) }
  function clearSelection() { select([]) }
  const historical = computed(() => !!revision.value && revision.value.version !== job.value?.currentRevision)
  // Server snapshots are the source of truth for manual placements, so a saved drag survives re-opening the revision.
  const dirty = computed(() => !!revision.value && draftFingerprint(draft.value) !== revisionBaseline(revision.value.score))
  const processing = computed(() => !!job.value && ['Queued', 'Processing', 'Cancelling'].includes(job.value.state))
  const exporting = computed(() => (revision.value && ['Queued', 'Processing'].includes(revision.value.state)) || (!!documentExport.value && ['Queued', 'Processing'].includes(documentExport.value.state)))
  const pages = computed(() => documentJob.value?.pages ?? [])
  // A ready merge is downloadable only while it matches every current page revision.
  const documentExport = computed(() => {
    const value = latestDocumentExport.value
    return value?.state === 'Ready' && (!pages.value.length || value.pages.some(p => pages.value.find(page => page.id === p.id)?.currentRevision !== p.version)) ? null : value
  })
  const editable = computed(() => !!revision.value && !historical.value && !saving.value && !exporting.value)
  const pending = computed(() => annotations.value?.annotations.filter(a => a.status === 'needs-manual-placement' && !placement(a.noteId)?.hidden).length ?? 0)
  const hiddenCount = computed(() => notes.value.filter(n => placement(n.id)?.hidden).length)

  function problem(cause: unknown): string {
    const value = cause as { title?: string; detail?: string; data?: { title?: string; detail?: string }; statusCode?: number; message?: string }
    const code = value.data?.title ?? value.title
    const statusCode = typeof value.statusCode === 'number' ? value.statusCode : undefined
    const messages: Record<string, string> = {
      'revision-conflict': t('store.revisionConflict'),
      'concurrency-conflict': t('store.concurrencyConflict'),
      'not-found': t('store.notFound'),
      'invalid-token': t('store.invalidToken'),
      'unsupported-type': t('store.unsupportedType'),
      'file-too-large': t('store.fileTooLarge'),
    }
    if (code && messages[code]) return messages[code]
    if (statusCode && statusCode >= 500) return t('store.serviceUnavailable')
    return value.data?.detail || value.detail || value.message || (cause instanceof Error ? cause.message : '') || t('store.connectionLost')
  }
  function draftKey() { return currentJobId.value && revision.value ? `pujian:draft:${currentJobId.value}:${revision.value.version}` : '' }
  watch(draft, () => {
    if (!ready.value || restoring || !draftKey() || storedDraftKey.value !== draftKey()) return
    // useStorage serializes synchronously; a defensive clone here would double the write cost.
    storedDraft.value = dirty.value ? draft.value : null
  }, { deep: true, flush: 'sync' })

  function restoreDraft(value: Draft) {
    history.pause()
    // Normalize here so every later read can assume all five maps exist.
    draft.value = normalizeDraft(value)
    history.resume(true)
    history.clear()
  }

  watch(server.queryError, value => { if (value) error.value = problem(value) })
  watch([server.sourceBlob, currentJobId], ([blob, id]) => {
    if (source.value?.url) URL.revokeObjectURL(source.value.url)
    source.value = blob && id ? { url: /pdf/i.test(blob.type) ? '' : URL.createObjectURL(blob), pdf: /pdf/i.test(blob.type), blob, pageId: id } : null
  })
  // A refetch of the same immutable revision must not reset draft/history.
  let restoredRevision = ''
  watch(revision, value => {
    if (!value) return
    const key = `${currentJobId.value}:${value.version}`
    if (restoredRevision === key) return
    restoredRevision = key; restoring = true
    storedDraftKey.value = draftKey()
    restoreDraft(!historical.value && storedDraft.value ? { ...storedDraft.value, placements: { ...snapshotPlacements(value.score), ...(storedDraft.value.placements ?? {}) }, styles: { ...snapshotStyles(value.score), ...(storedDraft.value.styles ?? {}) } } : { pitches: {}, placements: snapshotPlacements(value.score), numbering: value.score.numbering, annotationStyle: value.score.annotationStyle ?? defaultAnnotationStyle(), styles: snapshotStyles(value.score) })
    restoring = false
  }, { flush: 'sync' })
  async function poll() { await server.refresh() }
  async function open(item: RecentScore) {
    ++epoch; active.value = item; pageNumber.value = 1; selectedVersion.value = 0
    selectedId.value = null; clearSelection(); error.value = ''; activeId.value = item.id; restoredRevision = ''; history.clear()
  }
  async function selectPage(number: number) {
    if (!multiPage.value || number < 1 || number > pages.value.length || number === pageNumber.value) return
    if (dirty.value && !await confirmation.ask({ title: t('store.confirmPageSwitchTitle'), description: t('store.confirmPageSwitch'), confirmLabel: t('common.continue') })) return
    ++epoch; pageNumber.value = number; selectedVersion.value = 0; selectedId.value = null; clearSelection()
    restoredRevision = ''; history.clear(); error.value = ''
  }
  async function upload(file: File) {
    if (uploading.value) return
    if (!/\.(pdf|jpe?g|png)$/i.test(file.name)) { error.value = t('store.unsupportedType'); return }
    if (file.size > 20 * 1024 * 1024) { error.value = t('store.fileTooLarge'); return }
    if (/\.pdf$/i.test(file.name)) {
      uploading.value = true
      try {
        const issue = await inspectScorePdf(file)
        if (issue) { error.value = issue.key === 'pdfTooManyPages' ? t('store.pdfTooManyPages', { pages: issue.pages }) : t('store.' + issue.key); return }
      } catch { error.value = t('store.inspectPdfError'); return }
      finally { uploading.value = false }
    }
    if (dirty.value && !await confirmation.ask({ title: t('store.confirmOpenAnotherTitle'), description: t('store.confirmOpenAnother'), confirmLabel: t('common.continue') })) return
    const ticket = ++epoch
    uploading.value = true; error.value = ''
    const preprocessing = enhanceScan.value ? 'Adaptive' : 'None'
    try {
      // Retrying the same interrupted upload reuses its capability and idempotency key.
      const identity = `${file.name}:${file.size}:${file.lastModified}:${preprocessing}:${recognitionEngine.value}`
      let pending = pendingUpload.value
      if (!pending || pending.identity !== identity) {
        const bytes = crypto.getRandomValues(new Uint8Array(32))
        pending = { identity, token: Array.from(bytes, x => x.toString(16).padStart(2, '0')).join(''), key: crypto.randomUUID() }
        pendingUpload.value = pending
      }
      const value = await server.importMutation.mutateAsync({ file, preprocessing, engine: recognitionEngine.value, token: pending.token, key: pending.key })
      if (ticket !== epoch) return
      const item = { id: value.id, name: value.fileName, token: pending.token, date: new Date().toISOString() }
      recent.value = [item, ...recent.value.filter(r => r.id !== item.id)].slice(0, 40)
      await open(item)
      pendingUpload.value = null
    } catch (cause) { error.value = problem(cause) } finally { uploading.value = false }
  }
  async function example() {
    try { const blob = await $fetch<Blob>('/samples/minuet.pdf', { responseType: 'blob' }); await upload(new File([blob], t('store.exampleFile'), { type: 'application/pdf', lastModified: 1 })) }
    catch (cause) { error.value = problem(cause) }
  }
  function change(edit: (value: Draft) => void) {
    if (!editable.value) return
    const next = applyDraftEdit(draft.value, edit)
    if (next) draft.value = next
  }
  function pitch(id: string) { return draft.value.pitches[id] ?? notes.value.find(n => n.id === id)?.pitch }
  function placement(id: string) { return draft.value.placements[id] ?? notes.value.find(n => n.id === id)?.placement ?? null }
  function correct(id: string, value: Pitch) { change(d => { d.pitches[id] = clone(value) }) }
  function position(id: string, x: number, y: number) {
    positionMany([{ id, x: Math.round(x), y: Math.round(y) }])
  }
  function positionMany(values: { id: string; x: number; y: number }[]) {
    if (!values.length || values.some(value => !Number.isFinite(value.x) || !Number.isFinite(value.y))) return
    const ids = new Set(notes.value.map(note => note.id))
    change(d => {
      for (const value of values) {
        if (!ids.has(value.id)) continue
        d.placements[value.id] = {
          hidden: false,
          x: Math.max(2, Math.min((revision.value?.score.width ?? 3) - 3, value.x)),
          y: Math.max(2, Math.min((revision.value?.score.height ?? 3) - 3, value.y))
        }
      }
    })
  }
  function hide(id: string) { change(d => { d.placements[id] = { ...(placement(id) ?? { x: null, y: null }), hidden: !placement(id)?.hidden } }) }
  function hideMany(ids: string[]) {
    const known = new Set(notes.value.map(note => note.id))
    const targets = [...new Set(ids)].filter(id => known.has(id))
    if (!targets.length) return
    const hidden = !targets.every(id => placement(id)?.hidden)
    change(d => {
      for (const id of targets) d.placements[id] = { ...(placement(id) ?? { x: null, y: null }), hidden }
    })
  }
  function automatic(id: string) { change(d => { d.placements[id] = { hidden: false, x: null, y: null } }) }
  function numbering(value: Numbering) { change(d => { d.numbering = clone(value) }) }
  function annotationStyle(value: AnnotationStyle) { change(d => { d.annotationStyle = clone(value) }) }
  // Editing targets the marquee/shift selection, else the primary note, else the whole page.
  const styleTargets = computed(() => selection.value.length ? selection.value : selectedId.value ? [selectedId.value] : [])
  // Drafts restored from an older local copy can lack the styles map, so every read falls back to the page style.
  function styleOf(id: string): AnnotationStyle {
    const value = draft.value.styles?.[id]
    return value ? { fontSizePoints: value.fontSizePoints, color: value.color } : { fontSizePoints: draft.value.annotationStyle.fontSizePoints, color: draft.value.annotationStyle.color }
  }
  function styleOverridden(id: string) { return !!draft.value.styles?.[id] }
  function setStyle(patch: { fontSizePoints?: number | null; color?: string }) {
    const targets = styleTargets.value
    if (!targets.length) return
    change(d => {
      d.styles ??= {}
      for (const id of targets) {
        const base = d.styles[id] ?? d.annotationStyle
        const next = { fontSizePoints: patch.fontSizePoints !== undefined ? patch.fontSizePoints : base.fontSizePoints, color: (patch.color ?? base.color).toUpperCase() }
        if (next.fontSizePoints === d.annotationStyle.fontSizePoints && next.color === d.annotationStyle.color.toUpperCase())
          d.styles = Object.fromEntries(Object.entries(d.styles).filter(([key]) => key !== id))
        else d.styles[id] = next
      }
    })
  }
  function resetStyle() {
    const targets = styleTargets.value
    if (!targets.length) return
    change(d => { d.styles = Object.fromEntries(Object.entries(d.styles ?? {}).filter(([key]) => !targets.includes(key))) })
  }
  function undo() { if (editable.value) history.undo() }
  function redo() { if (editable.value) history.redo() }
  async function save(recalculate = false) {
    if (!active.value || !currentJobId.value || !revision.value || !editable.value) return
    saving.value = true; error.value = ''; const ticket = epoch
    const oldDraftKey = draftKey()
    try {
      if (!dirty.value && !recalculate) return
      const id = currentJobId.value
      const saved = revision.value
      const body = revisionPayload(draft.value, saved.score)
      const value = await server.revisionMutation.mutateAsync({ id, body: { expectedRevision: saved.version, ...body } })
      if (ticket !== epoch) return
      if (storedDraftKey.value === oldDraftKey) storedDraft.value = null
      selectedVersion.value = value.version
      await server.refresh()

    } catch (cause) { if (ticket === epoch) error.value = problem(cause) } finally { saving.value = false }
  }
  async function showVersion(version: number) {
    if (dirty.value && !await confirmation.ask({ title: t('store.confirmVersionTitle'), description: t('store.confirmVersion'), confirmLabel: t('common.continue') })) return
    restoredRevision = ''; selectedId.value = null; clearSelection(); selectedVersion.value = version
  }
  async function download() {
    if (!active.value || !currentJobId.value || !revision.value || revision.value.state !== 'Ready' || dirty.value) return
    try {
      const blob = await server.artifact(currentJobId.value, revision.value.version)
      const url = URL.createObjectURL(blob); const link = document.createElement('a')
      link.href = url; link.download = t('store.annotatedFile', { name: active.value.name.replace(/\.[^.]+$/, ''), version: revision.value.version }); link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000)
    } catch (cause) { error.value = problem(cause) }
  }
  async function exportDocument() {
    if (server.exportMutation.isPending.value || !active.value || !multiPage.value || pages.value.length !== documentJob.value?.pageCount || pages.value.some(p => p.state !== 'Ready' || p.exportState !== 'Ready') || dirty.value) return
    try { await server.exportMutation.mutateAsync(); await server.refresh() }
    catch (cause) { error.value = problem(cause) }
  }
  async function downloadDocument() {
    if (!active.value || documentExport.value?.state !== 'Ready') return
    try {
      const blob = await server.artifact(active.value.id, documentExport.value.version)
      const url = URL.createObjectURL(blob); const link = document.createElement('a')
      link.href = url; link.download = t('store.fullAnnotatedFile', { name: active.value.name.replace(/\.[^.]+$/, ''), version: documentExport.value.version }); link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000)
    } catch (cause) { error.value = problem(cause) }
  }
  async function command(action: 'cancel' | 'retry', id = active.value?.id) {
    if (!active.value || !id) return false
    try { await server.commandMutation.mutateAsync({ action, id }); await server.refresh(); return true }
    catch (cause) { error.value = problem(cause); return false }
  }
  async function rerecognizePage() {
    if (!currentJobId.value || processing.value || exporting.value) return
    if ((job.value?.state === 'Ready' || dirty.value) && !await confirmation.ask({ title: t('score.rerecognizeTitle'), description: t('score.rerecognizeDescription'), confirmLabel: t('common.continue') })) return
    if (!await command('retry', currentJobId.value)) return
    selectedVersion.value = 0; selectedId.value = null; clearSelection()
  }
  function home() { ++epoch; active.value = null; selectedVersion.value = 0; selectedId.value = null; clearSelection(); error.value = ''; activeId.value = null; restoredRevision = ''; history.clear() }
  function forget(id: string) { recent.value = recent.value.filter(r => r.id !== id); if (active.value?.id === id) home() }
  async function init() {
    await nextTick()
    ready.value = true
    const item = recent.value.find(r => r.id === activeId.value)
    if (item) await open(item)
  }
  // One blob belongs to the app-scoped store and is replaced on open/upload; a hot page remount can reuse it.
  function dispose() { ++epoch }
  return {
    recent, active, job, documentJob, pageNumber, documentExport, multiPage, pages, revision, annotations, source, selectedId, draft, canUndo, canRedo, uploading, enhanceScan, recognitionEngine, saving, error, ready,
    currentJobId, musicXml: server.musicXml,
    notes, selected, selectedNotes, selection, historical, dirty, processing, exporting, editable, pending, hiddenCount,
    init, open, upload, select, toggle, selectRegion, clearSelection, styleOf, styleOverridden, styleTargets, setStyle, resetStyle, example, selectPage, pitch, placement, correct, position, positionMany, hide, hideMany, automatic, numbering, annotationStyle, undo, redo, save, showVersion, download, exportDocument, downloadDocument, command, rerecognizePage, home, forget, dispose, poll
  }
})

if (import.meta.hot) import.meta.hot.accept(acceptHMRUpdate(useWorkspaceStore, import.meta.hot))
