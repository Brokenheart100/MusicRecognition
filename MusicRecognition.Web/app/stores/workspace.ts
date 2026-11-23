import { acceptHMRUpdate, defineStore, skipHydrate } from 'pinia'
import { StorageSerializers, useSessionStorage, useStorage as useVueUseStorage } from '@vueuse/core'
import { defaultAnnotationStyle, RecentScoreSchema, type AnnotationStyle, type Draft, type NoteAddition, type Numbering, type Pitch, type RecentScore } from '~~/shared/types/score'
import { applyDraftEdit, draftFingerprint, emptyDraft, normalizeDraft, parseStoredDraft, revisionBaseline, revisionPayload, snapshotPlacements, snapshotStyles, useDraftHistory } from '~/utils/draft'
import { useConfirmStore } from './confirm'
// 前端工作台的大脑（Pinia store）：保存当前打开哪份乐谱、第几页、选中哪些音符、
// 本地草稿改了什么、能否撤销，以及最近打开的乐谱列表。
// 界面组件都从这里读状态、调动作；网络请求交给 useScoreServer，草稿写进浏览器存储。
// 关键概念：draft（草稿）是还没保存的修改；revision（修订）是服务器上已保存的快照。
// 页面刷新后草稿不会丢，重新打开同一任务的同一修订时会自动恢复。

// 深拷贝：草稿对象要和旧快照彻底分开，避免改一处连带改掉历史记录。
const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value))
// 浏览器里保存最近打开的乐谱列表所用的存储键；v1 表示数据格式版本。
const storageKey = 'pujian:scores:v1'

// 用组合式写法定义 store，下面按本地状态、派生状态、动作的顺序排列。
export const useWorkspaceStore = defineStore('workspace', () => {
// i18n 负责把错误和提示翻译成当前语言；confirmation 是全局确认弹窗。
  const { t } = useI18n()
  const confirmation = useConfirmStore()
// 全局错误文案：上传、保存、下载等动作失败时写在这里，页面顶部统一提示。
  const error = ref('')
// 浏览器存储（localStorage、sessionStorage）的通用设置：组件挂载后再初始化，
// 同步写入，不监听其他标签页的改动，写入失败时转成可读错误。
  const storageOptions = {
    initOnMounted: true, writeDefaults: false, flush: 'sync' as const, listenToStorageChanges: false,
    onError: () => { error.value = t('store.storageError') },
  }
  // Server state must never overwrite browser-owned records during Pinia hydration.
// skipHydrate 让服务端渲染阶段不读本地存储，避免客户端和服务器画面不一致。
// 自定义读写：逐条校验格式（坏数据直接丢掉），最多保留 40 条最近记录。
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
// 记住上次打开的是哪份乐谱，刷新页面后自动恢复。
  const activeId = useVueUseStorage<string | null>('pujian:active', null, undefined, { ...storageOptions, serializer: StorageSerializers.string })
// 进行中的上传：文件身份、访问令牌（token）和幂等键（Idempotency-Key）。
// 存在 sessionStorage 里，同一标签页刷新后重试上传可以复用，不会重复建任务。
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
// 当前文档、当前页码，以及要查看的历史修订号（0 表示跟随最新）。
  const active = ref<RecentScore | null>(null)
  const pageNumber = ref(1)
  const selectedVersion = ref(0)
// 所有服务器数据都来自 useScoreServer 的查询；这里解构出常用结果。
  const server = useScoreServer(active, pageNumber, selectedVersion)
  const { job, documentJob, documentExport: latestDocumentExport, revision, annotations, multiPage, currentJobId } = server
// 当前页的原稿：Blob 是文件内容；PDF 走 canvas 渲染，扫描图会生成临时 URL。
  const source = shallowRef<{ url: string; pdf: boolean; blob: Blob; pageId?: string } | null>(null)
// 选中集合：selectedId 是检查器的主音符，selection 支持 Shift 多选和框选。
  const selectedId = ref<string | null>(null)
  // Shift/ctrl click and the canvas marquee keep a set; selectedId stays the inspector's primary note.
  const selection = ref<string[]>([])
// 防止 selectedId 变化触发的同步和 select 主动设置互相打架。
  let syncingSelection = false
// 本地草稿：音高修正、标注位置、简谱换算和样式；初值来自修订快照。
  const draft = ref<Draft>(emptyDraft())
  // Every edit replaces draft.value instead of mutating it, so snapshots can share the old object.
// 撤销和重做历史；每次编辑都产生新草稿对象，因此可以安全退回旧对象。
  const history = useDraftHistory(draft)
// 撤销和重做是否可用由历史记录决定，界面上用来禁用按钮。
  const { canUndo, canRedo } = history
  // The key changes only after a revision has loaded, before restoring its draft.
// 当前草稿对应的存储键；切换任务或修订后才更新，避免写到错误位置。
  const storedDraftKey = ref('pujian:draft:inactive')
// 按任务 id 加修订号持久化的草稿，刷新页面后恢复未保存的修改。
  const storedDraft = useVueUseStorage<Draft | null>(storedDraftKey, null, undefined, {
    ...storageOptions,
    serializer: {
      read: parseStoredDraft,
      write: JSON.stringify,
    },
  })
// 上传、增强扫描、识别引擎、保存等动作的开关和选项，用来禁用按钮、显示加载动画。
  const uploading = ref(false)
// 是否对图片扫描件做自适应增强（Adaptive）；对拍照质量差的谱子有帮助。
  const enhanceScan = ref(false)
// 识别引擎：默认 Audiveris；homr 用于部分乐谱，识别效果不同。
  const recognitionEngine = ref<'Audiveris' | 'Homr'>('Audiveris')
  const saving = ref(false)
// ready 表示浏览器存储已初始化完成，避免在恢复完成前写回存储。
  const ready = ref(false)
// 每次切换文档或页面都递增；异步请求回来发现 epoch 变了就丢弃结果。
  let epoch = 0
// 正在恢复草稿时置真，避免把恢复过程当成用户编辑写回存储。
  let restoring = false

// 按小节、谱表、x 坐标、y 坐标排序，得到从头到尾的阅读顺序。
  const notes = computed(() => [...(revision.value?.score.notes ?? [])].sort((a, b) => a.measure - b.measure || a.staff - b.staff || a.bounds.x - b.bounds.x || a.bounds.y - b.bounds.y))
// 当前主选中音符；不存在时返回 null，检查器据此显示空状态。
  const selected = computed(() => notes.value.find(n => n.id === selectedId.value) ?? null)
// 当前多选集合里的音符对象，批量修改样式时用。
  const selectedNotes = computed(() => notes.value.filter(n => selection.value.includes(n.id)))
// 选中音符的包围盒加一点留白，作为区域重识别的范围；坐标与识别图像素一致。
  const selectionRegion = computed(() => {
    const chosen = selectedNotes.value
    const page = revision.value?.score
    if (!chosen.length || !page) return null
// 留白至少 4 像素，并随五线谱行距放大，避免只截到符头、丢掉临时记号或符干。
    const pad = Math.max(4, page.interline * 1.5)
    let left = Infinity, top = Infinity, right = -Infinity, bottom = -Infinity
    for (const note of chosen) {
      left = Math.min(left, note.bounds.x)
      top = Math.min(top, note.bounds.y)
      right = Math.max(right, note.bounds.x + note.bounds.width)
      bottom = Math.max(bottom, note.bounds.y + note.bounds.height)
    }
    left = Math.max(0, left - pad); top = Math.max(0, top - pad)
    right = Math.min(page.width, right + pad); bottom = Math.min(page.height, bottom + pad)
    if (right - left < 2 || bottom - top < 2) return null
    const round = (value: number) => Math.round(value * 100) / 100
    return { x: round(left), y: round(top), width: round(right - left), height: round(bottom - top) }
  })
  // Any direct selectedId write (canvas click, keyboard focus) keeps the set consistent.
  watch(selectedId, id => {
    if (syncingSelection) return
    if (!id) { if (selection.value.length) selection.value = []; return }
    if (!selection.value.includes(id)) selection.value = [id]
  }, { flush: 'sync' })
// 统一的选择入口：过滤不存在的音符 id、去重，并把第一个设为主选中。
  function select(ids: string[]) {
    syncingSelection = true
    selection.value = [...new Set(ids.filter(id => notes.value.some(n => n.id === id)))]
    selectedId.value = selection.value[0] ?? null
    syncingSelection = false
  }
// Ctrl 或 Cmd 点击：已选中就移除，否则加入选择集合。
  function toggle(id: string) { select(selection.value.includes(id) ? selection.value.filter(value => value !== id) : [...selection.value, id]) }
// 框选：additive 为真时把新选中的并进原选择，否则替换。
  function selectRegion(ids: string[], additive = false) { select(additive ? [...selection.value, ...ids] : ids) }
  function clearSelection() { select([]) }
// 当前修订号落后于任务最新修订号，说明正在看历史版本（只读）。
  const historical = computed(() => !!revision.value && revision.value.version !== job.value?.currentRevision)
  // Server snapshots are the source of truth for manual placements, so a saved drag survives re-opening the revision.
// 草稿和服务器修订是否不一致；保存按钮、离开提示都看它。
  const dirty = computed(() => !!revision.value && draftFingerprint(draft.value) !== revisionBaseline(revision.value.score))
// 识别任务还在排队、识别或取消中，页头显示转圈状态。
  const processing = computed(() => !!job.value && ['Queued', 'Processing', 'Cancelling'].includes(job.value.state))
// 排版或整份导出仍在后台进行；此时禁止编辑，避免坐标和排版结果打架。
  const exporting = computed(() => (revision.value && ['Queued', 'Processing'].includes(revision.value.state)) || (!!documentExport.value && ['Queued', 'Processing'].includes(documentExport.value.state)))
// 多页文档的每页任务列表，单页时为空数组。
  const pages = computed(() => documentJob.value?.pages ?? [])
  // A ready merge is downloadable only while it matches every current page revision.
// 合并导出已经就绪但某页又产生了新修订时，当成过期，隐藏下载按钮。
  const documentExport = computed(() => {
    const value = latestDocumentExport.value
    return value?.state === 'Ready' && (!pages.value.length || value.pages.some(p => pages.value.find(page => page.id === p.id)?.currentRevision !== p.version)) ? null : value
  })
// 只有有修订、不是历史版本、不在保存或导出中，才允许编辑。
  const editable = computed(() => !!revision.value && !historical.value && !saving.value && !exporting.value)
// 待手动放置的简谱数字数量，用于页头和检查器的黄色徽标。
  const pending = computed(() => annotations.value?.annotations.filter(a => a.status === 'needs-manual-placement' && !placement(a.noteId)?.hidden).length ?? 0)
// 被用户隐藏的简谱数字数量。
  const hiddenCount = computed(() => notes.value.filter(n => placement(n.id)?.hidden).length)

// 把后端错误整理成一句用户能看懂的中文：先看业务错误码，再看 HTTP 状态码。
  function problem(cause: unknown): string {
    const value = cause as { title?: string; detail?: string; data?: { title?: string; detail?: string }; statusCode?: number; message?: string }
    const code = value.data?.title ?? value.title
    const statusCode = typeof value.statusCode === 'number' ? value.statusCode : undefined
// 常见业务错误码与提示文案的对照表，新增错误码时在这里补一行。
    const messages: Record<string, string> = {
      'revision-conflict': t('store.revisionConflict'),
      'concurrency-conflict': t('store.concurrencyConflict'),
      'not-found': t('store.notFound'),
      'invalid-token': t('store.invalidToken'),
      'unsupported-type': t('store.unsupportedType'),
      'file-too-large': t('store.fileTooLarge'),
    }
    if (code && messages[code]) return messages[code]
// 5xx 是服务器内部问题，统一提示服务暂时不可用。
    if (statusCode && statusCode >= 500) return t('store.serviceUnavailable')
    return value.data?.detail || value.detail || value.message || (cause instanceof Error ? cause.message : '') || t('store.connectionLost')
  }
// 草稿存储键：不同任务、不同修订各自独立，切页或切版本不会串草稿。
  function draftKey() { return currentJobId.value && revision.value ? `pujian:draft:${currentJobId.value}:${revision.value.version}` : '' }
// 草稿变化时同步写入浏览器存储：有未保存改动才写，没有就删掉。
// 存储是同步写入，这里直接存 draft，避免额外深拷贝拖慢频繁拖动。
  watch(draft, () => {
    if (!ready.value || restoring || !draftKey() || storedDraftKey.value !== draftKey()) return
    // useStorage serializes synchronously; a defensive clone here would double the write cost.
    storedDraft.value = dirty.value ? draft.value : null
  }, { deep: true, flush: 'sync' })

// 用一份草稿替换当前草稿：期间暂停历史记录，恢复后再清空撤销栈。
  function restoreDraft(value: Draft) {
    history.pause()
    // Normalize here so every later read can assume all five maps exist.
    draft.value = normalizeDraft(value)
    history.resume(true)
    history.clear()
  }

// 任一服务器查询出错，就写进全局错误文案。
  watch(server.queryError, value => { if (value) error.value = problem(value) })
// 原稿 Blob 或当前任务变化时更新画布数据源；旧的临时 URL 要立即释放。
  watch([server.sourceBlob, currentJobId], ([blob, id]) => {
    if (source.value?.url) URL.revokeObjectURL(source.value.url)
    source.value = blob && id ? { url: /pdf/i.test(blob.type) ? '' : URL.createObjectURL(blob), pdf: /pdf/i.test(blob.type), blob, pageId: id } : null
  })
  // A refetch of the same immutable revision must not reset draft/history.
// 记录已经恢复过草稿的任务加修订，防止同一修订重复拉取时重置草稿。
  let restoredRevision = ''
// 修订数据到达或变化时恢复草稿：
// 优先合并浏览器里存的草稿，其次是服务器修订加空修改的干净初始草稿。
  watch(revision, value => {
    if (!value) return
    const key = `${currentJobId.value}:${value.version}`
    if (restoredRevision === key) return
    restoredRevision = key; restoring = true
    storedDraftKey.value = draftKey()
// 服务器上的位置和样式作为底稿，浏览器草稿覆盖在上面，已保存的拖动结果不会丢。
    restoreDraft(!historical.value && storedDraft.value ? { ...storedDraft.value, placements: { ...snapshotPlacements(value.score), ...(storedDraft.value.placements ?? {}) }, styles: { ...snapshotStyles(value.score), ...(storedDraft.value.styles ?? {}) } } : { pitches: {}, placements: snapshotPlacements(value.score), numbering: value.score.numbering, annotationStyle: value.score.annotationStyle ?? defaultAnnotationStyle(), styles: snapshotStyles(value.score) })
    restoring = false
  }, { flush: 'sync' })
// 手动触发一次全量刷新（错误条上的重试连接按钮用）。
  async function poll() { await server.refresh() }
// 打开一份乐谱：递增 epoch 作废旧请求，重置页码、版本、选择和错误。
  async function open(item: RecentScore) {
    ++epoch; active.value = item; pageNumber.value = 1; selectedVersion.value = 0
// 清空撤销历史，避免把上一份乐谱的编辑历史带进来。
    selectedId.value = null; clearSelection(); error.value = ''; activeId.value = item.id; restoredRevision = ''; history.clear()
  }
// 切换多页文档的当前页；有未保存草稿时先弹确认。
  async function selectPage(number: number) {
// 页码越界或就是当前页时什么都不做。
    if (!multiPage.value || number < 1 || number > pages.value.length || number === pageNumber.value) return
    if (dirty.value && !await confirmation.ask({ title: t('store.confirmPageSwitchTitle'), description: t('store.confirmPageSwitch'), confirmLabel: t('common.continue') })) return
    ++epoch; pageNumber.value = number; selectedVersion.value = 0; selectedId.value = null; clearSelection()
    restoredRevision = ''; history.clear(); error.value = ''
  }
// 上传新文件：先做格式、大小、页数、加密检查，再创建识别任务。
// 这是整个流程的起点；上传成功后任务进入后台识别队列。
  async function upload(file: File) {
    if (uploading.value) return
// 只接受 PDF 和 JPG、PNG；单文件上限 20 MB（和服务器限制保持一致）。
    if (!/\.(pdf|jpe?g|png)$/i.test(file.name)) { error.value = t('store.unsupportedType'); return }
    if (file.size > 20 * 1024 * 1024) { error.value = t('store.fileTooLarge'); return }
// PDF 先在浏览器里检查页数和是否加密，提前给出友好提示，省一次上传。
    if (/\.pdf$/i.test(file.name)) {
      uploading.value = true
      try {
        const issue = await inspectScorePdf(file)
        if (issue) { error.value = issue.key === 'pdfTooManyPages' ? t('store.pdfTooManyPages', { pages: issue.pages }) : t('store.' + issue.key); return }
      } catch { error.value = t('store.inspectPdfError'); return }
      finally { uploading.value = false }
    }
// 有未保存草稿时先确认，避免用户误操作丢掉修改。
    if (dirty.value && !await confirmation.ask({ title: t('store.confirmOpenAnotherTitle'), description: t('store.confirmOpenAnother'), confirmLabel: t('common.continue') })) return
// ticket 是本次上传的票号；上传期间用户切了文档就丢弃结果。
    const ticket = ++epoch
    uploading.value = true; error.value = ''
// 图片扫描件可以选择自适应增强预处理，提升识别率。
    const preprocessing = enhanceScan.value ? 'Adaptive' : 'None'
    try {
// 身份相同说明是同一份文件的重试，可以复用上次生成的凭证。
      // Retrying the same interrupted upload reuses its capability and idempotency key.
// 文件身份由文件名、大小、修改时间、预处理和识别引擎组成。
      const identity = `${file.name}:${file.size}:${file.lastModified}:${preprocessing}:${recognitionEngine.value}`
      let pending = pendingUpload.value
      if (!pending || pending.identity !== identity) {
// 生成 32 字节随机访问令牌和幂等键：令牌证明这份文件是我的，
// 幂等键保证网络重试不会在服务器上创建两个相同的任务。
        const bytes = crypto.getRandomValues(new Uint8Array(32))
        pending = { identity, token: Array.from(bytes, x => x.toString(16).padStart(2, '0')).join(''), key: crypto.randomUUID() }
        pendingUpload.value = pending
      }
// 真正调用上传接口，得到任务 id 和文件名。
      const value = await server.importMutation.mutateAsync({ file, preprocessing, engine: recognitionEngine.value, token: pending.token, key: pending.key })
      if (ticket !== epoch) return
      const item = { id: value.id, name: value.fileName, token: pending.token, date: new Date().toISOString() }
// 把新任务插到最近列表最前面，同 id 去重，最多保留 40 条。
      recent.value = [item, ...recent.value.filter(r => r.id !== item.id)].slice(0, 40)
      await open(item)
// 上传成功后清掉进行中的上传信息，下次重新开始。
      pendingUpload.value = null
// 任何失败都转成可读文案放进全局错误条。
    } catch (cause) { error.value = problem(cause) } finally { uploading.value = false }
  }
// 加载内置的示例乐谱（小步舞曲）并走正常上传流程，方便新用户一键体验。
  async function example() {
    try { const blob = await $fetch<Blob>('/samples/minuet.pdf', { responseType: 'blob' }); await upload(new File([blob], t('store.exampleFile'), { type: 'application/pdf', lastModified: 1 })) }
    catch (cause) { error.value = problem(cause) }
  }
// 所有草稿编辑的统一入口：不可编辑时忽略；applyDraftEdit 返回新草稿，没变化则为空。
  function change(edit: (value: Draft) => void) {
    if (!editable.value) return
    const next = applyDraftEdit(draft.value, edit)
    if (next) draft.value = next
  }
// 读取音符当前音高：草稿里的修正优先，否则用服务器识别结果。
  function pitch(id: string) { return draft.value.pitches[id] ?? notes.value.find(n => n.id === id)?.pitch }
// 读取标注位置：草稿优先，其次服务器排版给的标注位置；都没有返回 null。
  function placement(id: string) { return draft.value.placements[id] ?? notes.value.find(n => n.id === id)?.placement ?? null }
// 修正音高：写入草稿的音高表，不直接改服务器数据。
  function correct(id: string, value: Pitch) { change(d => { d.pitches[id] = clone(value) }) }
// 单个标注定位：坐标四舍五入成整数像素，再走批量逻辑。
  function position(id: string, x: number, y: number) {
    positionMany([{ id, x: Math.round(x), y: Math.round(y) }])
  }
// 批量定位（拖拽一组数字时用）：过滤非法坐标，把位置限制在纸面内并取消隐藏。
  function positionMany(values: { id: string; x: number; y: number }[]) {
    if (!values.length || values.some(value => !Number.isFinite(value.x) || !Number.isFinite(value.y))) return
    const ids = new Set(notes.value.map(note => note.id))
    change(d => {
      for (const value of values) {
        if (!ids.has(value.id)) continue
        d.placements[value.id] = {
          hidden: false,
// 2 和 3 是贴边余量：至少留 2 像素，最多到纸宽高减 3，避免数字压到纸外。
          x: Math.max(2, Math.min((revision.value?.score.width ?? 3) - 3, value.x)),
          y: Math.max(2, Math.min((revision.value?.score.height ?? 3) - 3, value.y))
        }
      }
    })
  }
// 隐藏或显示单个简谱数字：不删除标注，只是不画出来，导出时也不画。
  function hide(id: string) { change(d => { d.placements[id] = { ...(placement(id) ?? { x: null, y: null }), hidden: !placement(id)?.hidden } }) }
// 批量隐藏或显示：只要有一个目标当前是显示的，就全部隐藏；否则全部显示。
  function hideMany(ids: string[]) {
    const known = new Set(notes.value.map(note => note.id))
    const targets = [...new Set(ids)].filter(id => known.has(id))
    if (!targets.length) return
// 用当前状态决定这次是隐藏还是显示，让同一个按钮可以来回切换。
    const hidden = !targets.every(id => placement(id)?.hidden)
    change(d => {
      for (const id of targets) d.placements[id] = { ...(placement(id) ?? { x: null, y: null }), hidden }
    })
  }
// 恢复自动位置：清掉手动坐标并取消隐藏，让服务端排版重新决定数字位置。
  function automatic(id: string) { change(d => { d.placements[id] = { hidden: false, x: null, y: null } }) }
// 修改简谱换算设置（模式加转调记录），整页数字会按新设置重算。
  function numbering(value: Numbering) { change(d => { d.numbering = clone(value) }) }
// 修改整页默认标注样式（字号、颜色）。
  function annotationStyle(value: AnnotationStyle) { change(d => { d.annotationStyle = clone(value) }) }
  // Editing targets the marquee/shift selection, else the primary note, else the whole page.
  const styleTargets = computed(() => selection.value.length ? selection.value : selectedId.value ? [selectedId.value] : [])
  // Drafts restored from an older local copy can lack the styles map, so every read falls back to the page style.
// 某个音符最终生效的样式：草稿里的单独设置优先，否则用整页默认样式。
  function styleOf(id: string): AnnotationStyle {
    const value = draft.value.styles?.[id]
    return value ? { fontSizePoints: value.fontSizePoints, color: value.color } : { fontSizePoints: draft.value.annotationStyle.fontSizePoints, color: draft.value.annotationStyle.color }
  }
// 这个音符的样式是否被单独改过，检查器据此显示恢复默认按钮。
  function styleOverridden(id: string) { return !!draft.value.styles?.[id] }
// 给当前目标批量设置样式；和整页默认值相同的项会从单独设置里删掉，保持数据干净。
  function setStyle(patch: { fontSizePoints?: number | null; color?: string }) {
    const targets = styleTargets.value
    if (!targets.length) return
    change(d => {
      d.styles ??= {}
      for (const id of targets) {
        const base = d.styles[id] ?? d.annotationStyle
        const next = { fontSizePoints: patch.fontSizePoints !== undefined ? patch.fontSizePoints : base.fontSizePoints, color: (patch.color ?? base.color).toUpperCase() }
// 逐项和整页默认值比较，相同就把这项覆盖移除。
        if (next.fontSizePoints === d.annotationStyle.fontSizePoints && next.color === d.annotationStyle.color.toUpperCase())
          d.styles = Object.fromEntries(Object.entries(d.styles).filter(([key]) => key !== id))
        else d.styles[id] = next
      }
    })
  }
// 清除选中音符的单独样式，恢复成整页默认。
  function resetStyle() {
    const targets = styleTargets.value
    if (!targets.length) return
    change(d => { d.styles = Object.fromEntries(Object.entries(d.styles ?? {}).filter(([key]) => !targets.includes(key))) })
  }
// 撤销和重做只改本地草稿，不访问服务器；历史记录由 useDraftHistory 维护。
  function undo() { if (editable.value) history.undo() }
  function redo() { if (editable.value) history.redo() }
// 保存草稿：把相对服务器修订的差异提交成新修订，成功后跟随到新版本。
// expectedRevision 是乐观锁：服务器已有更新版本时保存会被拒绝，而不是覆盖。
  async function save(recalculate = false) {
    if (!active.value || !currentJobId.value || !revision.value || !editable.value) return
    saving.value = true; error.value = ''; const ticket = epoch
    const oldDraftKey = draftKey()
    try {
// 没有改动且不是强制重新排版时直接返回，避免生成空修订。
      if (!dirty.value && !recalculate) return
      const id = currentJobId.value
// 记下当前修订，后面用它作为这次修改的基线。
      const saved = revision.value
// revisionPayload 只发送改过的部分；服务端据此合并出完整的新修订。
      const body = revisionPayload(draft.value, saved.score)
// 提交新修订；返回值里带着新版本号。
      const value = await server.revisionMutation.mutateAsync({ id, body: { expectedRevision: saved.version, ...body } })
      if (ticket !== epoch) return
// 保存成功后删掉本地草稿，避免下次打开又恢复出已经保存的内容。
      if (storedDraftKey.value === oldDraftKey) storedDraft.value = null
// 切到新生成的修订号并刷新所有查询，让界面显示刚保存的版本。
      selectedVersion.value = value.version
      await server.refresh()

    } catch (cause) { if (ticket === epoch) error.value = problem(cause) } finally { saving.value = false }
  }
// 手工补录音符的上下文：画布点击位置，以及从最近音符继承的小节、谱表、声部和谱表组。
  type ManualNoteContext = { x: number; y: number; measure: number; part: number; staff: number; voice: number; system: number; pitch: Pitch }
// 手工添加一个漏掉的音符：立即生成新修订，成功后跟随到新版本并选中它。
// 当前草稿里的其他修改会一起提交，避免切换版本时丢失。
  async function addManualNote(input: ManualNoteContext) {
    if (!active.value || !currentJobId.value || !revision.value || !editable.value) return false
    saving.value = true; error.value = ''; const ticket = epoch
    const oldDraftKey = draftKey()
    try {
      const saved = revision.value
      const score = saved.score
// 用五线谱行距估算符头大小，并保证矩形始终落在纸面内。
      const size = Math.max(1, Math.min(30, Math.max(6, score.interline * 1.2), score.width, score.height))
      const x = Math.max(0, Math.min(score.width - size, input.x - size / 2))
      const y = Math.max(0, Math.min(score.height - size, input.y - size / 2))
      const id = crypto.randomUUID()
      const addition: NoteAddition = {
        id, measure: input.measure, part: input.part, staff: input.staff, voice: input.voice, system: input.system,
        pitch: clone(input.pitch),
        bounds: { x: Math.round(x * 100) / 100, y: Math.round(y * 100) / 100, width: Math.round(size * 100) / 100, height: Math.round(size * 100) / 100 },
        placement: null,
        style: null,
      }
      const body = revisionPayload(draft.value, score, [addition])
      const value = await server.revisionMutation.mutateAsync({ id: currentJobId.value, body: { expectedRevision: saved.version, ...body } })
      if (ticket !== epoch) return false
      if (storedDraftKey.value === oldDraftKey) storedDraft.value = null
      selectedVersion.value = value.version
// 修订到达后 notes 会包含这个 id；先设主选中，检查器随后自动显示它。
      selectedId.value = id
      await server.refresh()
      return true
    } catch (cause) { if (ticket === epoch) error.value = problem(cause); return false }
    finally { saving.value = false }
  }
// 删除手工添加的音符：只允许 manual 音符，先确认，再生成新修订。
  async function removeManualNote(id: string) {
    const note = notes.value.find(value => value.id === id)
    if (!note?.manual || !editable.value) return false
    if (!await confirmation.ask({ title: t('score.removeNoteTitle'), description: t('score.removeNoteDescription'), confirmLabel: t('score.removeNoteConfirm') })) return false
    if (!active.value || !currentJobId.value || !revision.value || !editable.value) return false
    saving.value = true; error.value = ''; const ticket = epoch
    const oldDraftKey = draftKey()
    try {
      const saved = revision.value
      const body = revisionPayload(draft.value, saved.score, [], [id])
      const value = await server.revisionMutation.mutateAsync({ id: currentJobId.value, body: { expectedRevision: saved.version, ...body } })
      if (ticket !== epoch) return false
      if (storedDraftKey.value === oldDraftKey) storedDraft.value = null
      selectedVersion.value = value.version
      selectedId.value = null; clearSelection()
      await server.refresh()
      return true
    } catch (cause) { if (ticket === epoch) error.value = problem(cause); return false }
    finally { saving.value = false }
  }
// 查看历史版本：有未保存草稿时先确认，因为切版本会丢弃当前草稿。
  async function showVersion(version: number) {
    if (dirty.value && !await confirmation.ask({ title: t('store.confirmVersionTitle'), description: t('store.confirmVersion'), confirmLabel: t('common.continue') })) return
// 重置恢复标记和选择，让修订到达后按新版本重新恢复草稿。
    restoredRevision = ''; selectedId.value = null; clearSelection(); selectedVersion.value = version
  }
// 下载当前页的标注 PDF：要求修订已排版完成且没有未保存改动。
  async function download() {
    if (!active.value || !currentJobId.value || !revision.value || revision.value.state !== 'Ready' || dirty.value) return
    try {
      const blob = await server.artifact(currentJobId.value, revision.value.version)
      const url = URL.createObjectURL(blob); const link = document.createElement('a')
// 用临时 URL 触发浏览器下载，1 秒后释放 URL，给下载留出启动时间。
      link.href = url; link.download = t('store.annotatedFile', { name: active.value.name.replace(/\.[^.]+$/, ''), version: revision.value.version }); link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000)
    } catch (cause) { error.value = problem(cause) }
  }
// 请求把多页标注合并成一份 PDF。
  async function exportDocument() {
// 所有页都识别和排版完成且没有未保存改动，才允许开始合并。
    if (server.exportMutation.isPending.value || !active.value || !multiPage.value || pages.value.length !== documentJob.value?.pageCount || pages.value.some(p => p.state !== 'Ready' || p.exportState !== 'Ready') || dirty.value) return
    try { await server.exportMutation.mutateAsync(); await server.refresh() }
    catch (cause) { error.value = problem(cause) }
  }
// 下载已经合并好的整份标注 PDF。
  async function downloadDocument() {
    if (!active.value || documentExport.value?.state !== 'Ready') return
    try {
      const blob = await server.artifact(active.value.id, documentExport.value.version)
      const url = URL.createObjectURL(blob); const link = document.createElement('a')
      link.href = url; link.download = t('store.fullAnnotatedFile', { name: active.value.name.replace(/\.[^.]+$/, ''), version: documentExport.value.version }); link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000)
    } catch (cause) { error.value = problem(cause) }
  }
// 对任务发命令（取消或重试），成功后刷新状态；失败返回 false 供调用方判断。
  async function command(action: 'cancel' | 'retry', id = active.value?.id, region?: { x: number; y: number; width: number; height: number }) {
    if (!active.value || !id) return false
    try { await server.commandMutation.mutateAsync({ action, id, region }); await server.refresh(); return true }
    catch (cause) { error.value = problem(cause); return false }
  }
// 重新识别当前页：已经识别过或有未保存改动时先确认。
// 成功后退回最新修订并清空选择，等待新的识别结果。
  async function rerecognizePage() {
    if (!currentJobId.value || processing.value || exporting.value) return
    if ((job.value?.state === 'Ready' || dirty.value) && !await confirmation.ask({ title: t('score.rerecognizeTitle'), description: t('score.rerecognizeDescription'), confirmLabel: t('common.continue') })) return
    if (!await command('retry', currentJobId.value)) return
    selectedVersion.value = 0; selectedId.value = null; clearSelection()
  }
// 只重识别选中音符所在的区域：区域外音符和旧版本保留，未保存草稿不带入新版本。
  async function rerecognizeSelection() {
    const region = selectionRegion.value
    const count = selection.value.length
    if (!region || !count || !currentJobId.value || processing.value || exporting.value || revision.value?.state !== 'Ready') return
    if (!await confirmation.ask({ title: t('score.rerecognizeSelectionTitle'), description: t('score.rerecognizeSelectionDescription', { count }), confirmLabel: t('common.continue') })) return
    if (!await command('retry', currentJobId.value, region)) return
    selectedVersion.value = 0; selectedId.value = null; clearSelection()
  }
// 回到欢迎页：清空当前文档（最近列表保留），并作废所有在途请求。
  function home() { ++epoch; active.value = null; selectedVersion.value = 0; selectedId.value = null; clearSelection(); error.value = ''; activeId.value = null; restoredRevision = ''; history.clear() }
// 从最近列表删除一条记录；如果删的正是当前文档，同时回到欢迎页。
  function forget(id: string) { recent.value = recent.value.filter(r => r.id !== id); if (active.value?.id === id) home() }
// 应用启动时调用：等 DOM 就绪，再尝试恢复上次打开的乐谱。
  async function init() {
    await nextTick()
    ready.value = true
    const item = recent.value.find(r => r.id === activeId.value)
    if (item) await open(item)
  }
  // One blob belongs to the app-scoped store and is replaced on open/upload; a hot page remount can reuse it.
// 页面卸载时递增 epoch，让在途请求的结果失效；store 是应用级单例，不能清数据。
  function dispose() { ++epoch }
  return {
// 对组件公开的全部状态和动作；未列出的内部变量只在 store 内部使用。
    recent, active, job, documentJob, pageNumber, documentExport, multiPage, pages, revision, annotations, source, selectedId, draft, canUndo, canRedo, uploading, enhanceScan, recognitionEngine, saving, error, ready,
    currentJobId, musicXml: server.musicXml,
    notes, selected, selectedNotes, selection, selectionRegion, historical, dirty, processing, exporting, editable, pending, hiddenCount,
    init, open, upload, select, toggle, selectRegion, clearSelection, styleOf, styleOverridden, styleTargets, setStyle, resetStyle, example, selectPage, pitch, placement, correct, position, positionMany, hide, hideMany, automatic, numbering, annotationStyle, undo, redo, save, addManualNote, removeManualNote, showVersion, download, exportDocument, downloadDocument, command, rerecognizePage, rerecognizeSelection, home, forget, dispose, poll
  }
})

// 开发时热更新：修改本文件后自动替换 store 定义，不刷新整页。
if (import.meta.hot) import.meta.hot.accept(acceptHMRUpdate(useWorkspaceStore, import.meta.hot))
