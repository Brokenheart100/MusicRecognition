<script setup lang="ts">
import type { PDFDocumentProxy, RenderTask } from 'pdfjs-dist'
import { useDebounceFn, useElementSize } from '@vueuse/core'
import type { Annotation, Note } from '~~/shared/types/score'

const props = defineProps<{ zoom: number; labels: boolean; staff: string; voice: string }>()
const store = useWorkspaceStore()
const { t } = useI18n()
const stage = ref<HTMLElement>()
const svgEl = ref<SVGSVGElement>()
const canvas = ref<HTMLCanvasElement>()
const group = ref<SVGGElement>()
const { width: stageWidth } = useElementSize(stage)
const renderPhase = ref<'idle' | 'loading' | 'rendering'>('idle')
const renderError = ref('')
const rendered = ref(false)
const fallback = ref({ width: 1786, height: 2526 })
const placing = ref(false)
const marquee = ref<{ startX: number; startY: number; x: number; y: number; additive: boolean; moved: boolean } | null>(null)
type DragAnchor = { id: string; x: number; y: number; width: number; height: number }
const drag = ref<{ startX: number; startY: number; dx: number; dy: number; anchors: DragAnchor[]; moved: boolean } | null>(null)
let pdf: PDFDocumentProxy | undefined
let task: RenderTask | undefined
let sequence = 0
let renderSequence = 0

const physical = computed(() => {
  const data = store.annotations
  if (!data) return fallback.value
  const geometry = data.pdfGeometry
  if (!geometry) return { width: data.width, height: data.height }
  return { width: (geometry.cropBox[2] - geometry.cropBox[0]) * geometry.rasterDpi / 72, height: (geometry.cropBox[3] - geometry.cropBox[1]) * geometry.rasterDpi / 72 }
})
const rotated = computed(() => [90, 270].includes(store.annotations?.pdfGeometry?.rotation ?? 0))
const display = computed(() => rotated.value ? { width: physical.value.height, height: physical.value.width } : physical.value)
const paperWidth = computed(() => Math.min(790, Math.max(250, stageWidth.value - 64)) * props.zoom / 100)
const matrix = computed(() => {
  const { width: w, height: h } = physical.value
  return ({ 0: 'matrix(1 0 0 1 0 0)', 90: `matrix(0 1 -1 0 ${h} 0)`, 180: `matrix(-1 0 0 -1 ${w} ${h})`, 270: `matrix(0 -1 1 0 0 ${w})` } as Record<number, string>)[store.annotations?.pdfGeometry?.rotation ?? 0]
})
const visibleNotes = computed(() => store.notes.filter(n => (props.staff === 'all' || String(n.staff) === props.staff) && (props.voice === 'all' || String(n.voice) === props.voice)))
const visibleIds = computed(() => new Set(visibleNotes.value.map(n => n.id)))
const rovingId = computed(() => visibleNotes.value.some(note => note.id === store.selectedId) ? store.selectedId : visibleNotes.value[0]?.id ?? null)
const entries = computed(() => {
  const snapshot = store.revision?.score
  const notesById = new Map(snapshot?.notes.map(note => [note.id, note]) ?? [])
  const factor = store.annotations?.pdfGeometry ? 72 / store.annotations.pdfGeometry.rasterDpi : 595.2755905512 / (store.annotations?.width || 1)
  return store.annotations?.annotations.filter(a => visibleIds.value.has(a.noteId)).map(a => {
    // Per-note overrides from the draft win; otherwise the page-wide style applies.
    const style = store.styleOf(a.noteId)
    const savedStyle = notesById.get(a.noteId)?.style ?? snapshot?.annotationStyle
    // Keep the server's per-chord fitted size after saving. Only unsaved switches
    // from a fixed size show the preferred automatic size until export lays it out.
    const pixels = style.fontSizePoints == null
      ? savedStyle?.fontSizePoints == null ? a.fontSize : preferredAnnotationPixels(snapshot?.interline ?? a.fontSize, factor)
      : Math.round(style.fontSizePoints / factor)
    const ratio = pixels / a.fontSize
    return { ...a, color: style.color, fontSize: pixels, width: a.width * ratio, height: a.height * ratio,
      baselineOffset: a.baselineOffset.map(value => value * ratio) as [number, number],
      dots: a.dots.map(dot => dot.map(value => value * ratio) as [number, number, number]) }
  }) ?? []
})
const selectedAnnotation = computed(() => entries.value.find(a => a.noteId === store.selectedId))
// Mark notes whose annotation has no visible number, whether the layout left it
// pending or a manual draft was cleared. A missing annotation entry has no
// placement flow behind it, so it is not signalled here; hidden annotations are
// intentionally silent.
const unannotatedIds = computed(() => {
  const byId = new Map(entries.value.map(a => [a.noteId, a]))
  return new Set(visibleNotes.value.flatMap(note => {
    const annotation = byId.get(note.id)
    if (!annotation || store.placement(note.id)?.hidden || annotation.status === 'hidden') return []
    return location(annotation) ? [] : [note.id]
  }))
})
const unannotatedCount = computed(() => unannotatedIds.value.size)

function location(a: Annotation): readonly [number, number] | null {
  const anchor = drag.value?.anchors.find(value => value.id === a.noteId)
  if (anchor && drag.value) return [anchor.x + drag.value.dx, anchor.y + drag.value.dy]
  const p = store.draft.placements[a.noteId]
  return p?.x != null && p.y != null ? [p.x, p.y] : a.labelBounds ? [a.labelBounds[0], a.labelBounds[1]] : null
}
function noteAria(note: Note) {
  const base = t('canvas.noteTargetAria', { step: note.pitch.step, octave: note.pitch.octave, measure: note.measure, staff: note.staff })
  return unannotatedIds.value.has(note.id) ? `${base} ${t('canvas.unannotatedNoteAria')}` : base
}
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
function point(event: PointerEvent) {
  const transform = group.value?.getScreenCTM()
  return transform ? new DOMPoint(event.clientX, event.clientY).matrixTransform(transform.inverse()) : null
}
function viewBoxPoint(event: PointerEvent) {
  const screen = svgEl.value?.getScreenCTM()
  return screen ? new DOMPoint(event.clientX, event.clientY).matrixTransform(screen.inverse()) : null
}
function localPoint(value: { x: number; y: number }) {
  // The paper group carries the page rotation, so marquee corners travel viewBox -> screen -> local.
  const screen = svgEl.value?.getScreenCTM(), local = group.value?.getScreenCTM()
  if (!screen || !local) return null
  const point = new DOMPoint(value.x, value.y).matrixTransform(screen)
  return new DOMPoint(point.x, point.y).matrixTransform(local.inverse())
}
function startMarquee(event: PointerEvent) {
  const start = viewBoxPoint(event); if (!start) return
  svgEl.value?.setPointerCapture(event.pointerId)
  marquee.value = { startX: start.x, startY: start.y, x: start.x, y: start.y, additive: event.shiftKey || event.ctrlKey || event.metaKey, moved: false }
}
function moveMarquee(event: PointerEvent) {
  const value = marquee.value; if (!value) return
  const current = viewBoxPoint(event); if (!current) return
  value.x = current.x; value.y = current.y
  value.moved ||= Math.abs(current.x - value.startX) + Math.abs(current.y - value.startY) > 4
}
function endMarquee() {
  const value = marquee.value; if (!value) return
  marquee.value = null
  if (!value.moved) { if (!value.additive) store.clearSelection(); return }
  const from = localPoint({ x: value.startX, y: value.startY }), to = localPoint({ x: value.x, y: value.y })
  if (!from || !to) return
  const left = Math.min(from.x, to.x), right = Math.max(from.x, to.x), top = Math.min(from.y, to.y), bottom = Math.max(from.y, to.y)
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
  store.selectRegion([...ids], value.additive)
}
function pointerDown(event: PointerEvent) { if (event.button !== 0 || !event.isPrimary) return; if (placing.value) { place(event); return } startMarquee(event) }
function pointerMove(event: PointerEvent) { if (drag.value) { move(event); return } moveMarquee(event) }
function pointerUp() { if (drag.value) { end(); return } endMarquee() }
function movableAnchors(): DragAnchor[] {
  const ids = new Set(store.selection.length ? store.selection : store.selectedId ? [store.selectedId] : [])
  return entries.value.flatMap(a => {
    if (!ids.has(a.noteId) || store.placement(a.noteId)?.hidden) return []
    const p = location(a)
    return p ? [{ id: a.noteId, x: p[0], y: p[1], width: a.width, height: a.height }] : []
  })
}
function boundedDelta(anchors: DragAnchor[], dx: number, dy: number) {
  // Clamp the shared translation, rather than each label, to keep spacing intact.
  const width = Math.min(physical.value.width, store.revision?.score.width ?? physical.value.width)
  const height = Math.min(physical.value.height, store.revision?.score.height ?? physical.value.height)
  const left = Math.max(...anchors.map(a => 2 - a.x)), right = Math.min(...anchors.map(a => width - a.width - 3 - a.x))
  const top = Math.max(...anchors.map(a => 2 - a.y)), bottom = Math.min(...anchors.map(a => height - a.height - 3 - a.y))
  return { dx: left > right ? 0 : Math.max(left, Math.min(right, dx)), dy: top > bottom ? 0 : Math.max(top, Math.min(bottom, dy)) }
}
function commitMove(anchors: DragAnchor[], dx: number, dy: number) {
  store.positionMany(anchors.map(a => ({ id: a.id, x: a.x + dx, y: a.y + dy })))
}
function moveSelection(dx: number, dy: number) {
  if (!store.editable) return
  const anchors = movableAnchors(); if (!anchors.length) return
  const delta = boundedDelta(anchors, dx, dy)
  commitMove(anchors, delta.dx, delta.dy)
}
function start(event: PointerEvent, id: string) {
  if (event.button !== 0 || !event.isPrimary) return
  event.preventDefault()
  if (event.shiftKey || event.ctrlKey || event.metaKey) { store.toggle(id); return }
  // Focusing a member of the selection keeps the whole selected set.
  store.selectedId = id
  if (!store.editable) return
  const cursor = point(event), anchors = movableAnchors()
  if (!anchors.length || !cursor || !props.labels) return
  svgEl.value?.setPointerCapture(event.pointerId)
  drag.value = { startX: cursor.x, startY: cursor.y, dx: 0, dy: 0, anchors, moved: false }
}
function move(event: PointerEvent) {
  if (!drag.value) return
  const p = point(event); if (!p) return
  drag.value.moved ||= Math.abs(p.x - drag.value.startX) + Math.abs(p.y - drag.value.startY) > 2
  if (!drag.value.moved) return
  const delta = boundedDelta(drag.value.anchors, p.x - drag.value.startX, p.y - drag.value.startY)
  drag.value.dx = delta.dx; drag.value.dy = delta.dy
}
function end() {
  if (drag.value?.moved && (drag.value.dx || drag.value.dy)) commitMove(drag.value.anchors, drag.value.dx, drag.value.dy)
  drag.value = null
}
function place(event: PointerEvent) {
  if (!placing.value || !selectedAnnotation.value || !store.selectedId) return
  const cursor = point(event); if (!cursor) return
  store.position(store.selectedId, cursor.x - selectedAnnotation.value.width / 2, cursor.y - selectedAnnotation.value.height / 2)
  placing.value = false
}
function beginPlaceCurrent() { if (!store.editable || !selectedAnnotation.value) return; placing.value = true }
function cancelInteraction() { placing.value = false; drag.value = null; marquee.value = null }
function nextPending() {
  const pending = store.annotations?.annotations.filter(a => a.status === 'needs-manual-placement' && !store.placement(a.noteId)?.hidden) ?? []
  if (!pending.length) return
  const index = pending.findIndex(a => a.noteId === store.selectedId)
  store.selectedId = pending[(index + 1) % pending.length]!.noteId
}
function nextUnannotated() {
  const ids = [...unannotatedIds.value]
  if (!ids.length) return
  const index = store.selectedId ? ids.indexOf(store.selectedId) : -1
  store.selectedId = ids[(index + 1) % ids.length]!
}
async function paint() {
  if (!pdf || !canvas.value) return
  const ticket = ++renderSequence
  renderPhase.value = 'rendering'
  rendered.value = false
  task?.cancel()
  try {
    await task?.promise.catch(() => { })
    // Each editor source is a single-page child PDF; the document page number is only navigation metadata.
    const page = await pdf.getPage(1)
    if (ticket !== renderSequence || !canvas.value) return
    const original = page.getViewport({ scale: 1 })
    const viewport = page.getViewport({ scale: paperWidth.value / original.width })
    canvas.value.dataset.pdfTransform = JSON.stringify(viewport.transform)
    const ratio = Math.min(window.devicePixelRatio || 1, 2)
    canvas.value.width = Math.ceil(viewport.width * ratio); canvas.value.height = Math.ceil(viewport.height * ratio)
    task = page.render({ canvas: canvas.value, viewport, transform: [ratio, 0, 0, ratio, 0, 0] })
    await task.promise
    if (ticket === renderSequence) { rendered.value = true; renderError.value = '' }
  } catch (cause) { if (ticket === renderSequence && (cause as Error).name !== 'RenderingCancelledException') renderError.value = t('canvas.pdfPreviewError') }
  finally { if (ticket === renderSequence) renderPhase.value = 'idle' }
}
watch(() => store.source, async (source, previousSource) => {
  drag.value = null; marquee.value = null; placing.value = false
  const ticket = ++sequence
  ++renderSequence
  renderPhase.value = source?.pdf ? 'loading' : 'idle'
  rendered.value = false; renderError.value = ''
  task?.cancel(); if (pdf) { await pdf.loadingTask.destroy(); pdf = undefined }
  if (previousSource?.url) URL.revokeObjectURL(previousSource.url)
  if (!source) return
  if (!source.pdf) return
  try {
    const pdfjs = await import('pdfjs-dist')
    const assets = `/vendor/pdfjs/${pdfjs.version}/`
    pdfjs.GlobalWorkerOptions.workerSrc = `${assets}pdf.worker.mjs`
    const document = await pdfjs.getDocument({ data: new Uint8Array(await source.blob.arrayBuffer()), standardFontDataUrl: `${assets}standard_fonts/`, wasmUrl: `${assets}wasm/` }).promise
    if (ticket !== sequence) { await document.loadingTask.destroy(); return }
    pdf = document
    const page = await pdf.getPage(1), viewport = page.getViewport({ scale: 3 })
    fallback.value = { width: viewport.width, height: viewport.height }
    await nextTick(); await paint()
  } catch { if (ticket === sequence) { renderError.value = t('canvas.pdfReadError'); renderPhase.value = 'idle' } }
  finally { if (ticket === sequence && renderPhase.value === 'loading') renderPhase.value = 'idle' }
}, { immediate: true })
watch(paperWidth, useDebounceFn(() => { void paint() }, 100))
watch(() => store.selectedId, async (id) => {
  placing.value = false
  if (!id) drag.value = null
  const restoreFocus = !!document.activeElement?.closest('.note-target')
  await nextTick()
  const target = id ? stage.value?.querySelector<HTMLElement>(`.note-target[data-note-id="${id}"]`) : null
  target?.scrollIntoView({ block: 'nearest', inline: 'nearest' })
  if (restoreFocus && target) target.focus({ preventScroll: true })
})
onBeforeUnmount(() => { ++sequence; ++renderSequence; task?.cancel(); void pdf?.loadingTask.destroy(); if (store.source?.url) URL.revokeObjectURL(store.source.url) })
defineExpose({ beginPlaceCurrent, cancelInteraction, nextPending, nextUnannotated, unannotatedCount, moveSelection })
</script>

<template>
  <div ref="stage" class="score-stage" :class="{ 'is-placing': placing }">
    <div v-if="placing" class="placement-hint" role="status">
      <UIcon name="i-lucide-mouse-pointer-2" /> {{ $t('canvas.placeHint') }}
      <UButton size="xs" color="neutral" variant="ghost" :label="$t('canvas.cancelPlacement')" @click="placing = false" />
    </div>
    <div v-if="renderError" class="preview-error" role="alert">{{ renderError }}</div>
    <div v-else-if="!store.source" class="preview-loading">
      <UIcon name="i-lucide-loader-circle" class="spin" />
      <p>{{ $t('canvas.opening') }}</p>
    </div>
    <div v-else class="score-paper"
      :style="{ width: `${paperWidth}px`, aspectRatio: `${display.width} / ${display.height}` }"
      :data-rendered="rendered" data-testid="score-paper">
      <canvas v-if="store.source.pdf" ref="canvas" class="pdf-canvas" :aria-label="$t('canvas.pdfAria')" />
      <img v-else :src="store.source.url" :alt="$t('canvas.pdfAria')" class="scan-image" @load="rendered = true">
      <div v-if="store.source.pdf && renderPhase !== 'idle'" class="paper-loading" role="status" aria-live="polite">
        <span><UIcon name="i-lucide-loader-circle" class="spin" /> {{ renderPhase === 'loading' ? $t('canvas.reading') : $t('canvas.rendering') }}</span>
        <div class="paper-progress" role="progressbar" :aria-label="$t('canvas.renderProgressAria')" :aria-valuetext="$t('canvas.processing')" />
      </div>
      <svg v-if="store.annotations" v-show="rendered" ref="svgEl" class="annotation-overlay"
        :viewBox="`0 0 ${display.width} ${display.height}`" :aria-label="$t('canvas.notationLayerAria')"
        :class="{ 'is-selecting': marquee }" @pointerdown="pointerDown"
        @pointermove="pointerMove" @pointerup="pointerUp" @pointercancel="drag = null; marquee = null">
        <g ref="group" :transform="matrix">
          <g v-for="a in entries" :key="`label-${a.noteId}`">
            <g v-if="labels && location(a) && !store.placement(a.noteId)?.hidden"
              :transform="`translate(${location(a)![0]} ${location(a)![1]})`" class="annotation-label"
              :data-note-id="a.noteId" @pointerdown.stop="start($event, a.noteId)">
              <rect x="0" y="0" :width="a.width" :height="a.height" class="label-hit"
                :class="{ 'is-selected': store.selectedId === a.noteId, 'is-in-selection': store.selection.includes(a.noteId), 'is-manual': store.draft.placements[a.noteId] }" />
              <g :transform="`translate(${a.baselineOffset[0]} ${a.baselineOffset[1]}) scale(${a.fontSize / (store.annotations.glyphs?.unitsPerEm || 2048)} ${-a.fontSize / (store.annotations.glyphs?.unitsPerEm || 2048)})`"
                :fill="a.color">
                <path v-for="(glyph, index) in paths(a)" :key="index" :d="glyph.path"
                  :transform="`translate(${glyph.x} 0)`" />
              </g>
              <text v-if="!store.annotations.glyphs" :x="a.baselineOffset[0]" :y="a.baselineOffset[1]"
                :font-size="a.fontSize" :fill="a.color">{{ a.text }}</text>
              <circle v-for="(dot, index) in a.dots" :key="index" :cx="dot[0]" :cy="dot[1]" :r="dot[2]" :fill="a.color" />
            </g>
          </g>
          <g v-for="note in visibleNotes" :key="note.id" role="button" :tabindex="note.id === rovingId ? 0 : -1"
            :aria-label="noteAria(note)"
            class="note-target" :data-note-id="note.id" @pointerdown.stop="start($event, note.id)"
            @keydown.enter.prevent="store.selectedId = note.id" @keydown.space.prevent="store.selectedId = note.id"
            @focus="store.selectedId = note.id">
            <circle v-if="unannotatedIds.has(note.id)" class="note-attention"
              :cx="note.bounds.x + note.bounds.width / 2" :cy="note.bounds.y + note.bounds.height / 2"
              :r="Math.max(note.bounds.width, note.bounds.height) / 2 + 7"
              vector-effect="non-scaling-stroke" aria-hidden="true" />
            <rect class="note-focus-ring" :x="note.bounds.x - 9" :y="note.bounds.y - 9"
              :width="note.bounds.width + 18" :height="note.bounds.height + 18" rx="7"
              vector-effect="non-scaling-stroke" aria-hidden="true" />
            <rect :x="note.bounds.x - 5" :y="note.bounds.y - 5" :width="note.bounds.width + 10"
              :height="note.bounds.height + 10" rx="5" class="head-hit"
              :class="{ 'is-selected': store.selectedId === note.id, 'is-in-selection': store.selection.includes(note.id) }" />
          </g>
          <line v-if="store.selected && selectedAnnotation && location(selectedAnnotation)"
            :x1="store.selected.bounds.x + store.selected.bounds.width / 2"
            :y1="store.selected.bounds.y + store.selected.bounds.height / 2"
            :x2="location(selectedAnnotation)![0] + selectedAnnotation.width / 2"
            :y2="location(selectedAnnotation)![1] + selectedAnnotation.height / 2" class="selection-link" />
        </g>
        <rect v-if="marquee && marquee.moved" class="marquee"
          :x="Math.min(marquee.startX, marquee.x)" :y="Math.min(marquee.startY, marquee.y)"
          :width="Math.abs(marquee.x - marquee.startX)" :height="Math.abs(marquee.y - marquee.startY)" />
      </svg>
    </div>
    <p v-if="store.annotations" class="paper-caption">{{ $t('canvas.caption') }}</p>
  </div>
</template>
