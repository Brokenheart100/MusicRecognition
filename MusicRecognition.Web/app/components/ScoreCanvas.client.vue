<script setup lang="ts">
import type { PDFDocumentProxy, RenderTask } from 'pdfjs-dist'
import { useDebounceFn, useElementSize } from '@vueuse/core'
import type { Annotation, Note, Pitch } from '~~/shared/types/score'
// 乐谱画布：把 PDF 的一页渲染（render）到 canvas 上，再叠一层透明 SVG 画标注。
// 下层 canvas 是原谱图像，上层 SVG 画简谱数字、选中框、拖拽预览和框选矩形。
// 用户能在这里点选音符、拖动简谱数字调整标注位置（placement）、框选一批音符，
// 也能用点一下放一个的方式，手动摆放自动排版没放好的数字。
// 它只负责显示和收集手势，真正的数据改动都调用 workspace store。

// 父组件传入的显示选项：缩放百分比、是否显示简谱、只看哪个五线谱（staff）和声部（voice）。
const props = defineProps<{ zoom: number; labels: boolean; staff: string; voice: string }>()
// 手工补录时把点击位置和最近音符的上下文交给父组件，由它打开音高选择框。
type AddNoteContext = { x: number; y: number; measure: number; part: number; staff: number; voice: number; system: number; pitch: Pitch }
const emit = defineEmits<{ 'add-note': [context: AddNoteContext] }>()
// store 是唯一的数据来源；stage、svg、canvas、group 是四个需要直接操作的 DOM 节点。
const store = useWorkspaceStore()
const { t } = useI18n()
const stage = ref<HTMLElement>()
const svgEl = ref<SVGSVGElement>()
const canvas = ref<HTMLCanvasElement>()
const group = ref<SVGGElement>()
// 实时测量画布容器宽度，纸张宽度跟着容器自适应，窗口缩放时不用手动计算。
const { width: stageWidth } = useElementSize(stage)
// 渲染的三段状态：空闲、读取 PDF、画到 canvas，供加载动画和遮罩使用。
const renderPhase = ref<'idle' | 'loading' | 'rendering'>('idle')
const renderError = ref('')
const rendered = ref(false)
// 后端还没给出页面尺寸时的兜底值：A4 纸在 216 DPI 下的像素大小（1786×2526）。
const fallback = ref({ width: 1786, height: 2526 })
// placing 为真表示点一下放置模式：下一次点击会把选中的数字放到鼠标处。
const placing = ref(false)
// adding 为真表示手工补录模式：下一次点击会记录漏掉音符的位置并打开音高选择。
const adding = ref(false)
// 框选状态：起点、当前点、是否按住 Shift 或 Ctrl（追加选择）、是否真的拖动了。
const marquee = ref<{ startX: number; startY: number; x: number; y: number; additive: boolean; moved: boolean } | null>(null)
// 拖拽开始时记录每个被选标注的位置快照，拖动中只改预览偏移，松手才提交。
type DragAnchor = { id: string; x: number; y: number; width: number; height: number }
// 拖拽状态：起点、当前位移、被拖动标注的快照，以及是否超过算拖动的阈值。
const drag = ref<{ startX: number; startY: number; dx: number; dy: number; anchors: DragAnchor[]; moved: boolean } | null>(null)
// pdf 是当前 PDF 文档对象，task 是正在进行的渲染任务（新渲染前要取消旧的）。
let pdf: PDFDocumentProxy | undefined
let task: RenderTask | undefined
// sequence 和 renderSequence 是两份票据号：异步结果回来时核对号码，
// 号码对不上说明用户已经切页或再次缩放，旧结果直接丢弃，避免画面错乱。
let sequence = 0
let renderSequence = 0

// 页面的真实像素尺寸。PDF 坐标以点（1/72 英寸）为单位，乘上后端光栅化用的
// DPI（每英寸像素数）再除以 72，就得到画布上的像素宽高；cropBox 是去掉白边后的可见区域。
const physical = computed(() => {
  const data = store.annotations
  if (!data) return fallback.value
  const geometry = data.pdfGeometry
  if (!geometry) return { width: data.width, height: data.height }
  return { width: (geometry.cropBox[2] - geometry.cropBox[0]) * geometry.rasterDpi / 72, height: (geometry.cropBox[3] - geometry.cropBox[1]) * geometry.rasterDpi / 72 }
})
// 有些扫描页在 PDF 里标了旋转 90 度或 270 度，此时显示宽高要对调。
const rotated = computed(() => [90, 270].includes(store.annotations?.pdfGeometry?.rotation ?? 0))
// 最终用于显示的尺寸（已考虑旋转），SVG 的 viewBox 和纸张长宽比都用它。
const display = computed(() => rotated.value ? { width: physical.value.height, height: physical.value.width } : physical.value)
// 纸张显示宽度：最多 790 像素、最少 250 像素，并随缩放百分比变化。
const paperWidth = computed(() => Math.min(790, Math.max(250, stageWidth.value - 64)) * props.zoom / 100)
// SVG 旋转矩阵：把原始页面坐标映射到旋转后的显示坐标，
// 90 或 270 度时交换 x/y，180 度时两个方向都取反；0 度就是恒等变换。
const matrix = computed(() => {
  const { width: w, height: h } = physical.value
  return ({ 0: 'matrix(1 0 0 1 0 0)', 90: `matrix(0 1 -1 0 ${h} 0)`, 180: `matrix(-1 0 0 -1 ${w} ${h})`, 270: `matrix(0 -1 1 0 0 ${w})` } as Record<number, string>)[store.annotations?.pdfGeometry?.rotation ?? 0]
})
// 当前筛选下要显示的音符：按谱表和声部过滤，画布和键盘导航共用同一份清单。
const visibleNotes = computed(() => store.notes.filter(n => (props.staff === 'all' || String(n.staff) === props.staff) && (props.voice === 'all' || String(n.voice) === props.voice)))
// 用 Set 存可见音符 id，画标注时按 id 快速判断这个数字要不要画。
const visibleIds = computed(() => new Set(visibleNotes.value.map(n => n.id)))
// 键盘无障碍（roving tabindex）：整组音符只让一个能 Tab 聚焦，优先当前选中项。
const rovingId = computed(() => visibleNotes.value.some(note => note.id === store.selectedId) ? store.selectedId : visibleNotes.value[0]?.id ?? null)
// 把服务器下发的标注和本地草稿合并成可以直接画的列表：
// 这里会算每个文字的最终字号、颜色和缩放后的几何尺寸，是画布最核心的一段计算。
const entries = computed(() => {
  const snapshot = store.revision?.score
// snapshot 是服务器保存的这份修订，notesById 方便按音符查它保存的样式。
  const notesById = new Map(snapshot?.notes.map(note => [note.id, note]) ?? [])
// factor 是像素到 PDF 点的换算比例：有页面几何信息时用 72 除以光栅 DPI，
// 否则按 A4 纸宽 595.28 点与图片宽度的比例估算。
  const factor = store.annotations?.pdfGeometry ? 72 / store.annotations.pdfGeometry.rasterDpi : 595.2755905512 / (store.annotations?.width || 1)
  return store.annotations?.annotations.filter(a => visibleIds.value.has(a.noteId)).map(a => {
    // Per-note overrides from the draft win; otherwise the page-wide style applies.
    const style = store.styleOf(a.noteId)
    const savedStyle = notesById.get(a.noteId)?.style ?? snapshot?.annotationStyle
    // Keep the server's per-chord fitted size after saving. Only unsaved switches
    // from a fixed size show the preferred automatic size until export lays it out.
// 最终字号有三种来源：草稿指定值、服务端保存值、按五线谱行距自动推荐。
    const pixels = style.fontSizePoints == null
      ? savedStyle?.fontSizePoints == null ? a.fontSize : preferredAnnotationPixels(snapshot?.interline ?? a.fontSize, factor)
      : Math.round(style.fontSizePoints / factor)
// 字号变化会等比例缩放标注的宽、高、基线和八度点，保证文字和图形不变形。
    const ratio = pixels / a.fontSize
    return { ...a, color: style.color, fontSize: pixels, width: a.width * ratio, height: a.height * ratio,
      baselineOffset: a.baselineOffset.map(value => value * ratio) as [number, number],
      dots: a.dots.map(dot => dot.map(value => value * ratio) as [number, number, number]) }
  }) ?? []
})
// 当前选中音符对应的标注，用来画音符头到数字的引导线。
const selectedAnnotation = computed(() => entries.value.find(a => a.noteId === store.selectedId))
// Mark notes whose annotation has no visible number, whether the layout left it
// pending or a manual draft was cleared. A missing annotation entry has no
// placement flow behind it, so it is not signalled here; hidden annotations are
// intentionally silent.
// unannotatedIds 统计该有数字却没有的音符，画布会给它们画一个提示圈。
const unannotatedIds = computed(() => {
  const byId = new Map(entries.value.map(a => [a.noteId, a]))
  return new Set(visibleNotes.value.flatMap(note => {
    const annotation = byId.get(note.id)
    if (!annotation || store.placement(note.id)?.hidden || annotation.status === 'hidden') return []
    return location(annotation) ? [] : [note.id]
  }))
})
const unannotatedCount = computed(() => unannotatedIds.value.size)

// 一个标注最终画在哪里：拖动中看临时偏移，否则看草稿里手动放置的坐标，
// 再否则用服务器排版给出的位置（labelBounds 左上角）；返回 null 表示没有位置、不画。
function location(a: Annotation): readonly [number, number] | null {
  const anchor = drag.value?.anchors.find(value => value.id === a.noteId)
  if (anchor && drag.value) return [anchor.x + drag.value.dx, anchor.y + drag.value.dy]
  const p = store.draft.placements[a.noteId]
  return p?.x != null && p.y != null ? [p.x, p.y] : a.labelBounds ? [a.labelBounds[0], a.labelBounds[1]] : null
}
// 生成读屏软件念给用户听的音符描述；没有可见数字的音符会额外说明。
function noteAria(note: Note) {
  const base = t('canvas.noteTargetAria', { step: note.pitch.step, octave: note.pitch.octave, measure: note.measure, staff: note.staff })
  return unannotatedIds.value.has(note.id) ? `${base} ${t('canvas.unannotatedNoteAria')}` : base
}
// 服务器把简谱数字转成了矢量字形（glyph）路径，这里按字符顺序排好路径和横向偏移；
// 如果服务器没给字形，模板里会退化成直接用 SVG 文字写字。
function paths(a: Annotation) {
  const font = store.annotations?.glyphs
  if (!font) return []
  let x = 0
  return [...a.text].map(char => {
    const glyph = font.characters[char]
    const result = { path: glyph?.path ?? '', x }
    x += glyph?.advance ?? 0
    return result
  })
}
// 把鼠标的屏幕坐标换算成纸张内部坐标（会抵消旋转），拖拽和放置都用它。
function point(event: PointerEvent) {
  const transform = group.value?.getScreenCTM()
  return transform ? new DOMPoint(event.clientX, event.clientY).matrixTransform(transform.inverse()) : null
}
// 把屏幕坐标换算成 SVG 的 viewBox 坐标，框选矩形用的是这一套坐标。
function viewBoxPoint(event: PointerEvent) {
  const screen = svgEl.value?.getScreenCTM()
  return screen ? new DOMPoint(event.clientX, event.clientY).matrixTransform(screen.inverse()) : null
}
// 再把 viewBox 坐标换算到旋转前的纸张坐标，这样框选才能和音符的真实 bounds 比较。
function localPoint(value: { x: number; y: number }) {
  // The paper group carries the page rotation, so marquee corners travel viewBox -> screen -> local.
  const screen = svgEl.value?.getScreenCTM(), local = group.value?.getScreenCTM()
  if (!screen || !local) return null
  const point = new DOMPoint(value.x, value.y).matrixTransform(screen)
  return new DOMPoint(point.x, point.y).matrixTransform(local.inverse())
}
// 按下空白处开始框选；按住 Shift、Ctrl 或 Cmd 表示把新框到的音符追加到已选集合。
function startMarquee(event: PointerEvent) {
  const start = viewBoxPoint(event); if (!start) return
  svgEl.value?.setPointerCapture(event.pointerId)
  marquee.value = { startX: start.x, startY: start.y, x: start.x, y: start.y, additive: event.shiftKey || event.ctrlKey || event.metaKey, moved: false }
}
// 拖动时不断更新框选矩形；位移超过 4 像素才算拖拽，否则当作一次普通点击。
function moveMarquee(event: PointerEvent) {
  const value = marquee.value; if (!value) return
  const current = viewBoxPoint(event); if (!current) return
  value.x = current.x; value.y = current.y
  value.moved ||= Math.abs(current.x - value.startX) + Math.abs(current.y - value.startY) > 4
}
// 松手时结算框选：没拖动就清空选择；拖了就找出与矩形相交的音符和简谱数字。
function endMarquee() {
  const value = marquee.value; if (!value) return
  marquee.value = null
  if (!value.moved) { if (!value.additive) store.clearSelection(); return }
  const from = localPoint({ x: value.startX, y: value.startY }), to = localPoint({ x: value.x, y: value.y })
  if (!from || !to) return
// 框选坐标在 viewBox 里，先换成纸张坐标；四个边界可能反着拉，所以取 min/max。
  const left = Math.min(from.x, to.x), right = Math.max(from.x, to.x), top = Math.min(from.y, to.y), bottom = Math.max(from.y, to.y)
// 音符头和框选矩形有重叠即算选中，用音符的包围盒 bounds 判断。
  const ids = new Set(visibleNotes.value
    .filter(n => n.bounds.x < right && n.bounds.x + n.bounds.width > left && n.bounds.y < bottom && n.bounds.y + n.bounds.height > top)
    .map(n => n.id))
  // Labels live in the blank space around the staff, so a box drawn around
  // the numbers must select them even when no notehead falls inside the box.
  if (props.labels) {
    for (const a of entries.value) {
      if (store.placement(a.noteId)?.hidden) continue
      const p = location(a)
      if (!p) continue
      if (p[0] < right && p[0] + a.width > left && p[1] < bottom && p[1] + a.height > top) ids.add(a.noteId)
    }
  }
// selectRegion 负责去重、过滤不存在的 id，并同步主选中音符。
  store.selectRegion([...ids], value.additive)
}
// 指针按下的分流：放置模式就点一下放数字；否则点数字或音符开始拖拽（子元素拦截），
// 点空白处则开始框选。
function pointerDown(event: PointerEvent) { if (event.button !== 0 || !event.isPrimary) return; if (adding.value) { addAt(event); return } if (placing.value) { place(event); return } startMarquee(event) }
// 指针移动：正在拖拽就更新拖拽预览，否则更新框选矩形。
function pointerMove(event: PointerEvent) { if (drag.value) { move(event); return } moveMarquee(event) }
// 指针抬起：结算拖拽或框选。
function pointerUp() { if (drag.value) { end(); return } endMarquee() }
// 找出可以拖动的标注：属于当前选择、没有被隐藏、且已经有位置。
function movableAnchors(): DragAnchor[] {
  const ids = new Set(store.selection.length ? store.selection : store.selectedId ? [store.selectedId] : [])
  return entries.value.flatMap(a => {
    if (!ids.has(a.noteId) || store.placement(a.noteId)?.hidden) return []
    const p = location(a)
    return p ? [{ id: a.noteId, x: p[0], y: p[1], width: a.width, height: a.height }] : []
  })
}
// 把整组标注的位移限制在纸面内：留 2 到 3 像素边距，防止数字被拖到纸外看不见。
function boundedDelta(anchors: DragAnchor[], dx: number, dy: number) {
  // Clamp the shared translation, rather than each label, to keep spacing intact.
// 先算这组标注整体最多能左、右、上、下移动多少，再夹住请求的位移。
  const width = Math.min(physical.value.width, store.revision?.score.width ?? physical.value.width)
  const height = Math.min(physical.value.height, store.revision?.score.height ?? physical.value.height)
  const left = Math.max(...anchors.map(a => 2 - a.x)), right = Math.min(...anchors.map(a => width - a.width - 3 - a.x))
  const top = Math.max(...anchors.map(a => 2 - a.y)), bottom = Math.min(...anchors.map(a => height - a.height - 3 - a.y))
  return { dx: left > right ? 0 : Math.max(left, Math.min(right, dx)), dy: top > bottom ? 0 : Math.max(top, Math.min(bottom, dy)) }
}
// 松手或按方向键后，把最终位置交给 store（store 会取整、查边界并记入草稿）。
function commitMove(anchors: DragAnchor[], dx: number, dy: number) {
  store.positionMany(anchors.map(a => ({ id: a.id, x: a.x + dx, y: a.y + dy })))
}
// 键盘方向键移动选中标注；不可编辑（历史版本或导出中）时忽略。
function moveSelection(dx: number, dy: number) {
  if (!store.editable) return
  const anchors = movableAnchors(); if (!anchors.length) return
  const delta = boundedDelta(anchors, dx, dy)
  commitMove(anchors, delta.dx, delta.dy)
}
// 在某个数字或音符上按下鼠标：Shift 或 Ctrl 点击是加选、减选，普通点击只选中它；
// 如果拖的是当前选中的数字，就记录起点和快照，等鼠标移动再真正开始拖。
function start(event: PointerEvent, id: string) {
  if (event.button !== 0 || !event.isPrimary) return
  event.preventDefault()
  if (event.shiftKey || event.ctrlKey || event.metaKey) { store.toggle(id); return }
  // Focusing a member of the selection keeps the whole selected set.
  store.selectedId = id
  if (!store.editable) return
// 只有可编辑、显示着简谱数字、且至少有一个可拖标注时才启动拖拽。
  const cursor = point(event), anchors = movableAnchors()
  if (!anchors.length || !cursor || !props.labels) return
  svgEl.value?.setPointerCapture(event.pointerId)
  drag.value = { startX: cursor.x, startY: cursor.y, dx: 0, dy: 0, anchors, moved: false }
}
// 拖动中先算位移，超过 2 像素才算真拖动，然后套用边界限制生成预览偏移。
function move(event: PointerEvent) {
  if (!drag.value) return
  const p = point(event); if (!p) return
  drag.value.moved ||= Math.abs(p.x - drag.value.startX) + Math.abs(p.y - drag.value.startY) > 2
  if (!drag.value.moved) return
  const delta = boundedDelta(drag.value.anchors, p.x - drag.value.startX, p.y - drag.value.startY)
  drag.value.dx = delta.dx; drag.value.dy = delta.dy
}
// 松开鼠标：有实际位移才提交，否则当作一次普通点选。
function end() {
  if (drag.value?.moved && (drag.value.dx || drag.value.dy)) commitMove(drag.value.anchors, drag.value.dx, drag.value.dy)
  drag.value = null
}
// 点一下放置：把选中数字的中心对准鼠标点，并退出放置模式。
function place(event: PointerEvent) {
  if (!placing.value || !selectedAnnotation.value || !store.selectedId) return
  const cursor = point(event); if (!cursor) return
  store.position(store.selectedId, cursor.x - selectedAnnotation.value.width / 2, cursor.y - selectedAnnotation.value.height / 2)
  placing.value = false
}
// 进入放置模式，等用户下一次点击画布；由 P 键或检查器按钮触发。
function beginPlaceCurrent() { if (!store.editable || !selectedAnnotation.value) return; adding.value = false; placing.value = true }
// 进入手工补录模式：下一次点击画布空白处时记录位置，并打开音高选择。
function beginAddNote() { if (!store.editable || !store.revision) return; placing.value = false; adding.value = true; drag.value = null; marquee.value = null }
// 把点击位置换算成纸张坐标，并从当前筛选内最近的音符继承小节、谱表、声部和音高。
function addAt(event: PointerEvent) {
  const cursor = point(event); if (!cursor) return
  let nearest: Note | null = null; let best = Infinity
  for (const note of visibleNotes.value) {
    const dx = note.bounds.x + note.bounds.width / 2 - cursor.x
    const dy = note.bounds.y + note.bounds.height / 2 - cursor.y
    const distance = dx * dx + dy * dy
    if (distance < best) { best = distance; nearest = note }
  }
  emit('add-note', {
    x: cursor.x, y: cursor.y,
    measure: nearest?.measure ?? 1, part: nearest?.part ?? 1, staff: nearest?.staff ?? 1,
    voice: nearest?.voice ?? 0, system: nearest?.system ?? 1,
    pitch: nearest ? { ...nearest.pitch } : { step: 'C', alter: 0, octave: 4 },
  })
  adding.value = false
}
// Esc 或取消按钮：同时退出放置、补录模式，取消拖拽和框选。
function cancelInteraction() { placing.value = false; adding.value = false; drag.value = null; marquee.value = null }
// 在需要手动放置的标注之间循环跳转，配合 N 键快速处理排版失败的数字。
function nextPending() {
  const pending = store.annotations?.annotations.filter(a => a.status === 'needs-manual-placement' && !store.placement(a.noteId)?.hidden) ?? []
  if (!pending.length) return
  const index = pending.findIndex(a => a.noteId === store.selectedId)
  store.selectedId = pending[(index + 1) % pending.length]!.noteId
}
// 在没有可见简谱数字的音符之间循环跳转，逐个补标注。
function nextUnannotated() {
  const ids = [...unannotatedIds.value]
  if (!ids.length) return
  const index = store.selectedId ? ids.indexOf(store.selectedId) : -1
  store.selectedId = ids[(index + 1) % ids.length]!
}
// 把 PDF 的第一页画到 canvas 上。编辑器里的原稿都是单页子 PDF，
// 所以永远渲染第 1 页，页码只是任务导航信息。整个过程做三件事：
// 取消上一次渲染、按当前纸宽算缩放、用设备像素比让画面更清晰。
async function paint() {
  if (!pdf || !canvas.value) return
// 每次渲染取一个递增票号，异步返回时检查票号是否还是最新的，防止旧画面覆盖新画面。
  const ticket = ++renderSequence
  renderPhase.value = 'rendering'
  rendered.value = false
  task?.cancel()
  try {
// 等上一次取消操作彻底结束再开始，避免两个渲染同时写同一块 canvas。
    await task?.promise.catch(() => { })
    // Each editor source is a single-page child PDF; the document page number is only navigation metadata.
    const page = await pdf.getPage(1)
    if (ticket !== renderSequence || !canvas.value) return
// 拿到第 1 页的原始视口（scale 为 1 时 1 个 PDF 点等于 1 个 CSS 像素）。
    const original = page.getViewport({ scale: 1 })
// 按纸张显示宽度算出缩放比例，再据此得到本次渲染的视口尺寸。
    const viewport = page.getViewport({ scale: paperWidth.value / original.width })
// 把视口变换记到 DOM 上，便于自动化测试核对渲染参数。
    canvas.value.dataset.pdfTransform = JSON.stringify(viewport.transform)
// 高分辨率屏幕最多按 2 倍像素渲染，再高只是更占内存，肉眼几乎看不出差别。
    const ratio = Math.min(window.devicePixelRatio || 1, 2)
// canvas 内部像素尺寸乘上倍率，CSS 显示尺寸仍由外层纸张宽度决定。
    canvas.value.width = Math.ceil(viewport.width * ratio); canvas.value.height = Math.ceil(viewport.height * ratio)
// 渲染时再放大一次，让 PDF 内容按高分辨率绘制。
    task = page.render({ canvas: canvas.value, viewport, transform: [ratio, 0, 0, ratio, 0, 0] })
    await task.promise
// 票号仍是最新的才承认这次渲染成功，否则说明已被更新的渲染取代。
    if (ticket === renderSequence) { rendered.value = true; renderError.value = '' }
// 取消渲染会抛 RenderingCancelledException；这是预期行为，不当作错误提示。
  } catch (cause) { if (ticket === renderSequence && (cause as Error).name !== 'RenderingCancelledException') renderError.value = t('canvas.pdfPreviewError') }
  finally { if (ticket === renderSequence) renderPhase.value = 'idle' }
}
// 切换乐谱或页面时加载新的原稿文件：清掉旧 PDF、旧的 Blob URL 和所有交互状态。
// 用动态 import 引入 pdfjs，避免首屏为用不到的 PDF 解析器付出加载成本。
watch(() => store.source, async (source, previousSource) => {
  drag.value = null; marquee.value = null; placing.value = false
// 换文档也算一次渲染世代：旧文档的异步加载结果回来时必须丢弃。
  const ticket = ++sequence
  ++renderSequence
  renderPhase.value = source?.pdf ? 'loading' : 'idle'
  rendered.value = false; renderError.value = ''
  task?.cancel(); if (pdf) { await pdf.loadingTask.destroy(); pdf = undefined }
// 非 PDF 的扫描图会生成临时 Blob URL，换文档时要释放，否则内存越用越多。
  if (previousSource?.url) URL.revokeObjectURL(previousSource.url)
  if (!source) return
  if (!source.pdf) return
  try {
// PDF.js 的 worker 和字体、解码资源都放在站点本地的 /vendor/pdfjs/版本/ 下，
// 这样不依赖外网 CDN，离线也能用，版本跟着依赖走。
    const pdfjs = await import('pdfjs-dist')
// 把整个 PDF 读进内存再解析；standardFontDataUrl 和 wasmUrl 提供内置字体与解码器。
    const assets = `/vendor/pdfjs/${pdfjs.version}/`
    pdfjs.GlobalWorkerOptions.workerSrc = `${assets}pdf.worker.mjs`
    const document = await pdfjs.getDocument({ data: new Uint8Array(await source.blob.arrayBuffer()), standardFontDataUrl: `${assets}standard_fonts/`, wasmUrl: `${assets}wasm/` }).promise
// 加载期间用户又切了文档：销毁刚加载好的 PDF，直接返回。
    if (ticket !== sequence) { await document.loadingTask.destroy(); return }
    pdf = document
// 先用 3 倍缩放量一遍页面尺寸，作为拿不到后端几何信息时的兜底纸张大小。
    const page = await pdf.getPage(1), viewport = page.getViewport({ scale: 3 })
    fallback.value = { width: viewport.width, height: viewport.height }
// 等 DOM 更新出 canvas 之后再真正绘制。
    await nextTick(); await paint()
  } catch { if (ticket === sequence) { renderError.value = t('canvas.pdfReadError'); renderPhase.value = 'idle' } }
  finally { if (ticket === sequence && renderPhase.value === 'loading') renderPhase.value = 'idle' }
}, { immediate: true })
// 窗口变宽或用户缩放时重画；防抖 100 毫秒，避免拖动缩放条时疯狂重绘。
watch(paperWidth, useDebounceFn(() => { void paint() }, 100))
// 选中音符变化时把它滚动到可视区域；如果焦点原来在某个音符上，就把焦点跟过去。
watch(() => store.selectedId, async (id) => {
  placing.value = false
  if (!id) drag.value = null
  const restoreFocus = !!document.activeElement?.closest('.note-target')
  await nextTick()
  const target = id ? stage.value?.querySelector<HTMLElement>(`.note-target[data-note-id="${id}"]`) : null
  target?.scrollIntoView({ block: 'nearest', inline: 'nearest' })
  if (restoreFocus && target) target.focus({ preventScroll: true })
})
// 组件卸载：让所有未完成的异步任务失效，取消渲染并释放 PDF 和 Blob URL。
onBeforeUnmount(() => { ++sequence; ++renderSequence; task?.cancel(); void pdf?.loadingTask.destroy(); if (store.source?.url) URL.revokeObjectURL(store.source.url) })
// 暴露给父组件（ScoreWorkspace 和页面）调用的方法，用于快捷键和按钮控制画布。
defineExpose({ beginPlaceCurrent, beginAddNote, cancelInteraction, nextPending, nextUnannotated, unannotatedCount, moveSelection })
</script>

<template>
<!-- 画布舞台：负责加载或错误提示、纸张容器、原谱层和标注层 -->
  <div ref="stage" class="score-stage" :class="{ 'is-placing': placing, 'is-adding': adding }">
<!-- 手工补录提示条：提醒点击漏掉的音符位置，并提供取消按钮 -->
    <div v-if="adding" class="placement-hint" role="status">
      <UIcon name="i-lucide-plus" /> {{ $t('canvas.addNoteHint') }}
      <UButton size="xs" color="neutral" variant="ghost" :label="$t('canvas.cancelAddNote')" @click="adding = false" />
    </div>
<!-- 放置模式提示条：提醒点一下放数字，并提供取消按钮 -->
    <div v-if="placing" class="placement-hint" role="status">
      <UIcon name="i-lucide-mouse-pointer-2" /> {{ $t('canvas.placeHint') }}
      <UButton size="xs" color="neutral" variant="ghost" :label="$t('canvas.cancelPlacement')" @click="placing = false" />
    </div>
<!-- PDF 渲染失败时显示错误文字，不再显示加载动画 -->
    <div v-if="renderError" class="preview-error" role="alert">{{ renderError }}</div>
<!-- 原稿还没下载完时的加载提示 -->
    <div v-else-if="!store.source" class="preview-loading">
      <UIcon name="i-lucide-loader-circle" class="spin" />
      <p>{{ $t('canvas.opening') }}</p>
    </div>
<!-- 纸张容器：宽高比按页面真实宽高，缩放只改宽度，高度自动跟着变 -->
    <div v-else class="score-paper"
      :style="{ width: `${paperWidth}px`, aspectRatio: `${display.width} / ${display.height}` }"
      :data-rendered="rendered" data-testid="score-paper">
<!-- 原谱层：PDF 页画在 canvas 上；扫描图片没有 PDF 时用 img 显示 -->
      <canvas v-if="store.source.pdf" ref="canvas" class="pdf-canvas" :aria-label="$t('canvas.pdfAria')" />
<!-- 扫描照片不是 PDF，直接显示图片；图片加载完再显示标注层，避免先画出错位 -->
      <img v-else :src="store.source.url" :alt="$t('canvas.pdfAria')" class="scan-image" @load="rendered = true">
<!-- 读取 PDF 或渲染中的遮罩和进度条 -->
      <div v-if="store.source.pdf && renderPhase !== 'idle'" class="paper-loading" role="status" aria-live="polite">
        <span><UIcon name="i-lucide-loader-circle" class="spin" /> {{ renderPhase === 'loading' ? $t('canvas.reading') : $t('canvas.rendering') }}</span>
        <div class="paper-progress" role="progressbar" :aria-label="$t('canvas.renderProgressAria')" :aria-valuetext="$t('canvas.processing')" />
      </div>
<!-- 标注层：透明 SVG 铺在纸上，viewBox 与页面像素一一对应；
     所有点选、拖拽、框选手势都从这一层进入 -->
      <svg v-if="store.annotations" v-show="rendered" ref="svgEl" class="annotation-overlay"
        :viewBox="`0 0 ${display.width} ${display.height}`" :aria-label="$t('canvas.notationLayerAria')"
        :class="{ 'is-selecting': marquee }" @pointerdown="pointerDown"
        @pointermove="pointerMove" @pointerup="pointerUp" @pointercancel="drag = null; marquee = null">
<!-- 承载页面旋转的组：里面的坐标都是旋转前的纸张坐标 -->
        <g ref="group" :transform="matrix">
<!-- 每个音符的简谱数字：可拖动、可点选；没位置或被隐藏的就不画 -->
          <g v-for="a in entries" :key="`label-${a.noteId}`">
            <g v-if="labels && location(a) && !store.placement(a.noteId)?.hidden"
              :transform="`translate(${location(a)![0]} ${location(a)![1]})`" class="annotation-label"
              :data-note-id="a.noteId" @pointerdown.stop="start($event, a.noteId)">
<!-- 透明命中矩形：扩大点击范围，同时显示选中和手动放置状态 -->
              <rect x="0" y="0" :width="a.width" :height="a.height" class="label-hit"
                :class="{ 'is-selected': store.selectedId === a.noteId, 'is-in-selection': store.selection.includes(a.noteId), 'is-manual': store.draft.placements[a.noteId] }" />
<!-- 用服务器给的矢量字形画数字；字号变化时通过 scale 缩放，字形不会模糊 -->
              <g :transform="`translate(${a.baselineOffset[0]} ${a.baselineOffset[1]}) scale(${a.fontSize / (store.annotations.glyphs?.unitsPerEm || 2048)} ${-a.fontSize / (store.annotations.glyphs?.unitsPerEm || 2048)})`"
                :fill="a.color">
                <path v-for="(glyph, index) in paths(a)" :key="index" :d="glyph.path"
                  :transform="`translate(${glyph.x} 0)`" />
              </g>
<!-- 服务器没提供字形时的退路：直接用 SVG 文字写简谱数字 -->
              <text v-if="!store.annotations.glyphs" :x="a.baselineOffset[0]" :y="a.baselineOffset[1]"
                :font-size="a.fontSize" :fill="a.color">{{ a.text }}</text>
<!-- 八度点：数字上方或下方的小圆点，表示高八度或低八度 -->
              <circle v-for="(dot, index) in a.dots" :key="index" :cx="dot[0]" :cy="dot[1]" :r="dot[2]" :fill="a.color" />
            </g>
          </g>
<!-- 音符热区：每个音符头周围一块透明区域，负责点选、聚焦和无障碍角色 -->
          <g v-for="note in visibleNotes" :key="note.id" role="button" :tabindex="note.id === rovingId ? 0 : -1"
            :aria-label="noteAria(note)"
            class="note-target" :data-note-id="note.id" @pointerdown.stop="start($event, note.id)"
            @keydown.enter.prevent="store.selectedId = note.id" @keydown.space.prevent="store.selectedId = note.id"
            @focus="store.selectedId = note.id">
<!-- 该有简谱数字却缺失时的提醒圈（只提示，不拦截点击） -->
            <circle v-if="unannotatedIds.has(note.id)" class="note-attention"
              :cx="note.bounds.x + note.bounds.width / 2" :cy="note.bounds.y + note.bounds.height / 2"
              :r="Math.max(note.bounds.width, note.bounds.height) / 2 + 7"
              vector-effect="non-scaling-stroke" aria-hidden="true" />
<!-- 键盘焦点环：用非缩放描边，缩放时线条粗细保持不变 -->
            <rect class="note-focus-ring" :x="note.bounds.x - 9" :y="note.bounds.y - 9"
              :width="note.bounds.width + 18" :height="note.bounds.height + 18" rx="7"
              vector-effect="non-scaling-stroke" aria-hidden="true" />
            <rect :x="note.bounds.x - 5" :y="note.bounds.y - 5" :width="note.bounds.width + 10"
              :height="note.bounds.height + 10" rx="5" class="head-hit"
              :class="{ 'is-selected': store.selectedId === note.id, 'is-in-selection': store.selection.includes(note.id) }" />
          </g>
<!-- 选中标注时，从音符头到数字中心画一条细引导线，帮助确认对应关系 -->
          <line v-if="store.selected && selectedAnnotation && location(selectedAnnotation)"
            :x1="store.selected.bounds.x + store.selected.bounds.width / 2"
            :y1="store.selected.bounds.y + store.selected.bounds.height / 2"
            :x2="location(selectedAnnotation)![0] + selectedAnnotation.width / 2"
            :y2="location(selectedAnnotation)![1] + selectedAnnotation.height / 2" class="selection-link" />
        </g>
<!-- 框选矩形：只在真正拖动后显示 -->
        <rect v-if="marquee && marquee.moved" class="marquee"
          :x="Math.min(marquee.startX, marquee.x)" :y="Math.min(marquee.startY, marquee.y)"
          :width="Math.abs(marquee.x - marquee.startX)" :height="Math.abs(marquee.y - marquee.startY)" />
      </svg>
    </div>
<!-- 纸张下方的小字说明，提示数字可以拖动校正 -->
    <p v-if="store.annotations" class="paper-caption">{{ $t('canvas.caption') }}</p>
  </div>
</template>
