import { acceptHMRUpdate, defineStore, skipHydrate } from 'pinia'
import { StorageSerializers, useRefHistory, useSessionStorage, useStorage as useVueUseStorage } from '@vueuse/core'
import { defaultAnnotationStyle, DraftSchema, RecentScoreSchema, type AnnotationStyle, type Draft, type Numbering, type Pitch, type RecentScore, type Revision } from '~~/shared/types/score'

const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value))
const emptyDraft = (): Draft => ({ pitches: {}, placements: {}, numbering: { mode: 'FixedC', keys: [] }, annotationStyle: defaultAnnotationStyle(), styles: {} })
const storageKey = 'pujian:scores:v1'

export const useWorkspaceStore = defineStore('workspace', () => {
  const error = ref('')
  const storageOptions = {
    initOnMounted: true, writeDefaults: false, flush: 'sync' as const, listenToStorageChanges: false,
    onError: () => { error.value = '浏览器未能保存本地记录。请保持本页面打开并及时下载乐谱。' },
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
  const history = useRefHistory(draft, { capacity: 50, clone: true, flush: 'sync' })
  const { canUndo, canRedo } = history
  // The key changes only after a revision has loaded, before restoring its draft.
  const storedDraftKey = ref('pujian:draft:inactive')
  const storedDraft = useVueUseStorage<Draft | null>(storedDraftKey, null, undefined, {
    ...storageOptions,
    serializer: {
      read: (raw) => {
        try {
          const parsed = DraftSchema.safeParse(JSON.parse(raw))
          return parsed.success ? parsed.data : null
        } catch { return null }
      },
      write: JSON.stringify,
    },
  })
  const uploading = ref(false)
  const enhanceScan = ref(false)
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
  const snapshotStyles = (value: Revision['score']) => Object.fromEntries(value.notes.filter(n => n.style).map(n => [n.id, { fontSizePoints: n.style!.fontSizePoints, color: n.style!.color.toUpperCase() }]))
  // Server snapshots are the source of truth for manual placements, so a saved drag survives re-opening the revision.
  const snapshotPlacements = (value: Revision['score']) => Object.fromEntries(value.notes.filter(n => n.placement).map(n => [n.id, n.placement!]))
  const baseline = computed(() => revision.value ? JSON.stringify({ pitches: {}, placements: snapshotPlacements(revision.value.score), numbering: revision.value.score.numbering, annotationStyle: revision.value.score.annotationStyle ?? defaultAnnotationStyle(), styles: snapshotStyles(revision.value.score) }) : '')
  const draftStyles = computed(() => Object.fromEntries(Object.entries(draft.value.styles ?? {}).map(([id, style]) => [id, { fontSizePoints: style.fontSizePoints, color: style.color.toUpperCase() }])))
  const historical = computed(() => !!revision.value && revision.value.version !== job.value?.currentRevision)
  const dirty = computed(() => !!revision.value && JSON.stringify({ ...draft.value, styles: draftStyles.value, annotationStyle: { fontSizePoints: draft.value.annotationStyle.fontSizePoints, color: draft.value.annotationStyle.color.toUpperCase() } }) !== baseline.value)
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
    const messages: Record<string, string> = {
      'revision-conflict': '这份乐谱已有新版本，请回到最新版后再编辑。',
      'concurrency-conflict': '乐谱状态刚刚发生变化，请重新打开后再试。',
      'not-found': '暂时无法打开这份乐谱，访问凭据可能已经失效。',
      'invalid-token': '本地访问凭据缺失，请重新上传原文件。',
      'unsupported-type': '请选择 PDF、JPG 或 PNG 乐谱。',
      'file-too-large': '文件超过 20 MB，请先缩小文件。',
    }
    return (code && messages[code]) || value.data?.detail || value.detail || value.message || (cause instanceof Error ? cause.message : '') || '连接暂时中断，请稍后重试。'
  }
  function draftKey() { return currentJobId.value && revision.value ? `pujian:draft:${currentJobId.value}:${revision.value.version}` : '' }
  watch(draft, () => {
    if (!ready.value || restoring || !draftKey() || storedDraftKey.value !== draftKey()) return
    storedDraft.value = dirty.value ? clone(draft.value) : null
  }, { deep: true, flush: 'sync' })

  function restoreDraft(value: Draft) {
    history.pause()
    // Normalize here so every later read can assume all five maps exist.
    draft.value = { pitches: value.pitches ?? {}, placements: value.placements ?? {}, numbering: value.numbering, annotationStyle: value.annotationStyle, styles: value.styles ?? {} }
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
    if (dirty.value && !confirm('当前页有未保存修改，切换后草稿会保留在本机。')) return
    ++epoch; pageNumber.value = number; selectedVersion.value = 0; selectedId.value = null; clearSelection()
    restoredRevision = ''; history.clear(); error.value = ''
  }
  async function upload(file: File) {
    if (uploading.value) return
    if (!/\.(pdf|jpe?g|png)$/i.test(file.name)) { error.value = '请选择 PDF、JPG 或 PNG 乐谱。'; return }
    if (file.size > 20 * 1024 * 1024) { error.value = '文件不能超过 20 MB。'; return }
    if (/\.pdf$/i.test(file.name)) {
      uploading.value = true
      try {
        const issue = await inspectScorePdf(file)
        if (issue) { error.value = issue; return }
      } catch { error.value = '暂时无法检查 PDF，请刷新页面后重试。'; return }
      finally { uploading.value = false }
    }
    if (dirty.value && !confirm('当前有未保存的修改，仍要打开另一份乐谱吗？草稿会保留在本机。')) return
    const ticket = ++epoch
    uploading.value = true; error.value = ''
    const preprocessing = enhanceScan.value ? 'Adaptive' : 'None'
    try {
      // Retrying the same interrupted upload reuses its capability and idempotency key.
      const identity = `${file.name}:${file.size}:${file.lastModified}:${preprocessing}`
      let pending = pendingUpload.value
      if (!pending || pending.identity !== identity) {
        const bytes = crypto.getRandomValues(new Uint8Array(32))
        pending = { identity, token: Array.from(bytes, x => x.toString(16).padStart(2, '0')).join(''), key: crypto.randomUUID() }
        pendingUpload.value = pending
      }
      const value = await server.importMutation.mutateAsync({ file, preprocessing, token: pending.token, key: pending.key })
      if (ticket !== epoch) return
      const item = { id: value.id, name: value.fileName, token: pending.token, date: new Date().toISOString() }
      recent.value = [item, ...recent.value.filter(r => r.id !== item.id)].slice(0, 40)
      await open(item)
      pendingUpload.value = null
    } catch (cause) { error.value = problem(cause) } finally { uploading.value = false }
  }
  async function example() {
    try { const blob = await $fetch<Blob>('/samples/minuet.pdf', { responseType: 'blob' }); await upload(new File([blob], '小步舞曲 · Minuet.pdf', { type: 'application/pdf', lastModified: 1 })) }
    catch (cause) { error.value = problem(cause) }
  }
  function change(edit: (value: Draft) => void) {
    if (!editable.value) return
    const next = clone(draft.value); edit(next)
    if (JSON.stringify(next) === JSON.stringify(draft.value)) return
    draft.value = next
  }
  function pitch(id: string) { return draft.value.pitches[id] ?? notes.value.find(n => n.id === id)?.pitch }
  function placement(id: string) { return draft.value.placements[id] ?? notes.value.find(n => n.id === id)?.placement ?? null }
  function correct(id: string, value: Pitch) { change(d => { d.pitches[id] = clone(value) }) }
  function position(id: string, x: number, y: number) {
    change(d => { d.placements[id] = { hidden: false, x: Math.max(2, Math.min((revision.value?.score.width ?? 3) - 3, Math.round(x))), y: Math.max(2, Math.min((revision.value?.score.height ?? 3) - 3, Math.round(y))) } })
  }
  function hide(id: string) { change(d => { d.placements[id] = { ...(placement(id) ?? { x: null, y: null }), hidden: !placement(id)?.hidden } }) }
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
  async function save() {
    if (!active.value || !currentJobId.value || !revision.value || !editable.value) {
      console.info('[save] skipped:', { active: !!active.value, job: !!currentJobId.value, revision: !!revision.value, editable: editable.value, historical: historical.value, exporting: exporting.value, saving: saving.value })
      return
    }
    saving.value = true; error.value = ''; const ticket = epoch
    const oldDraftKey = draftKey()
    try {
      if (!dirty.value) { console.info('[save] skipped: nothing to save'); return }
      const id = currentJobId.value
      const saved = revision.value
      // Overrides present in the draft are written; ones removed from the draft are cleared with null.
      const styleChanges = [
        ...Object.entries(draftStyles.value).map(([noteId, style]) => ({ noteId, style })),
        ...saved.score.notes.filter(n => n.style && !draftStyles.value[n.id]).map(n => ({ noteId: n.id, style: null })),
      ]
      const value = await server.revisionMutation.mutateAsync({
        id, body: {
          expectedRevision: saved.version, numbering: draft.value.numbering,
          annotationStyle: draft.value.annotationStyle,
          corrections: Object.entries(draft.value.pitches).map(([noteId, value]) => ({ noteId, pitch: value })),
          placements: Object.entries(draft.value.placements).map(([noteId, value]) => ({ noteId, placement: value })),
          styleChanges,
        }
      })
      if (ticket !== epoch) return
      if (storedDraftKey.value === oldDraftKey) storedDraft.value = null
      selectedVersion.value = value.version
      await server.refresh()

    } catch (cause) { if (ticket === epoch) error.value = problem(cause) } finally { saving.value = false }
  }
  async function showVersion(version: number) {
    if (dirty.value && !confirm('打开其他版本前，保留本机草稿并继续吗？')) return
    restoredRevision = ''; selectedId.value = null; clearSelection(); selectedVersion.value = version
  }
  async function download() {
    if (!active.value || !currentJobId.value || !revision.value || revision.value.state !== 'Ready' || dirty.value) return
    try {
      const blob = await server.artifact(currentJobId.value, revision.value.version)
      const url = URL.createObjectURL(blob); const link = document.createElement('a')
      link.href = url; link.download = `${active.value.name.replace(/\.[^.]+$/, '')} · 标注版 v${revision.value.version}.pdf`; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000)
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
      link.href = url; link.download = `${active.value.name.replace(/\.[^.]+$/, '')} · 全曲标注版 v${documentExport.value.version}.pdf`; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000)
    } catch (cause) { error.value = problem(cause) }
  }
  async function command(action: 'cancel' | 'retry') {
    if (!active.value) return
    try { await server.commandMutation.mutateAsync(action); await server.refresh() }
    catch (cause) { error.value = problem(cause) }
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
    recent, active, job, documentJob, pageNumber, documentExport, multiPage, pages, revision, annotations, source, selectedId, draft, canUndo, canRedo, uploading, enhanceScan, saving, error, ready,
    currentJobId, musicXml: server.musicXml,
    notes, selected, selectedNotes, selection, historical, dirty, processing, exporting, editable, pending, hiddenCount,
    init, open, upload, select, toggle, selectRegion, clearSelection, styleOf, styleOverridden, styleTargets, setStyle, resetStyle, example, selectPage, pitch, placement, correct, position, hide, automatic, numbering, annotationStyle, undo, redo, save, showVersion, download, exportDocument, downloadDocument, command, home, forget, dispose, poll
  }
})

if (import.meta.hot) import.meta.hot.accept(acceptHMRUpdate(useWorkspaceStore, import.meta.hot))
