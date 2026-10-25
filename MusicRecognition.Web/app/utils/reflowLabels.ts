import type { ReflowNote } from '~~/shared/api/generated/types.gen'
import { automaticFontScales, chordFontScale } from './annotationSizing'

const ns = 'http://www.w3.org/2000/svg'
type Box = { x: number; y: number; width: number; height: number }
type Direction = 'below' | 'above' | 'right' | 'left'
type Candidate = Box & { direction: Direction }
const union = (boxes: Box[]): Box => {
  const x = Math.min(...boxes.map(b => b.x)), y = Math.min(...boxes.map(b => b.y))
  return { x, y, width: Math.max(...boxes.map(b => b.x + b.width)) - x, height: Math.max(...boxes.map(b => b.y + b.height)) - y }
}

// Verovio pageWidth=2100 is a 210 mm page. Point sizes belong to that
// physical page, not to a notehead's glyph dimensions; zoom scales both.
export const reflowFontSize = (points: number | null, headHeight: number, viewBoxWidth: number, pageWidthMm = 210) => {
  const unit = 25.4 / 72 * viewBoxWidth / pageWidthMm
  return points === null ? Math.max(4 * unit, Math.min(8 * unit, headHeight * 1.35)) : points * unit
}

export function* reflowCandidates(anchor: Box, width: number, height: number, interline: number, staff?: Box): Generator<Candidate> {
  const gap = interline * .2, step = interline * .2
  const shifts = [0, -step, step, -2 * step, 2 * step]
  const below = Math.max(anchor.y + anchor.height, staff ? staff.y + staff.height : -Infinity)
  const above = Math.min(anchor.y, staff?.y ?? Infinity)
  for (const direction of ['below', 'above'] as const) {
    for (let row = 0; row <= 35; row++) {
      const y = direction === 'below' ? below + gap + row * step : above - gap - height - row * step
      for (const dx of shifts) yield { direction, x: anchor.x + (anchor.width - width) / 2 + dx, y, width, height }
    }
  }
  for (const direction of ['right', 'left'] as const) {
    for (let column = 0; column <= 10; column++) {
      const x = direction === 'right' ? anchor.x + anchor.width + gap + column * step : anchor.x - gap - width - column * step
      for (const dy of shifts) yield { direction, x, y: anchor.y + (anchor.height - height) / 2 + dy, width, height }
    }
  }
}
const overlaps = (a: Box, b: Box) => a.x < b.x + b.width && a.x + a.width > b.x && a.y < b.y + b.height && a.y + a.height > b.y
function boxIn(element: SVGGraphicsElement, space: SVGSVGElement): Box | null {
  const transform = element.getScreenCTM(), base = space.getScreenCTM()
  if (!transform || !base) return null
  const raw = element.getBBox(), stroke = Number.parseFloat(getComputedStyle(element).strokeWidth) || 1
  const b = { x: raw.x - (raw.width ? 0 : stroke / 2), y: raw.y - (raw.height ? 0 : stroke / 2), width: raw.width || stroke, height: raw.height || stroke }
  const matrix = base.inverse().multiply(transform)
  const points = [[b.x, b.y], [b.x + b.width, b.y], [b.x, b.y + b.height], [b.x + b.width, b.y + b.height]].map(p => new DOMPoint(p[0], p[1]).matrixTransform(matrix))
  const x = Math.min(...points.map(p => p.x)), y = Math.min(...points.map(p => p.y))
  return { x, y, width: Math.max(...points.map(p => p.x)) - x, height: Math.max(...points.map(p => p.y)) - y }
}

// All geometry belongs to the NEW SVG. Original PDF coordinates never enter here.
export function placeReflowLabels(host: HTMLElement, notes: ReflowNote[], enabled: boolean) {
  const obstacles = new Map<SVGSVGElement, Box[]>()
  const occupied = new Map<SVGSVGElement, Box[]>()
  const resolved = new Set<string>(), placed = new Set<string>(), pending = new Set<string>(), hidden = new Set<string>()
  const seen = new Set<string>(), staffFrames = new Map<SVGGraphicsElement, Box>()
  type Member = { note: ReflowNote; label: SVGGElement; bounds: Box; anchor: Box; size: number }
  type Group = { key: SVGGraphicsElement; svg: SVGSVGElement; staff?: Box; members: Member[]; anchor: Box }
  const groups = new Map<SVGGraphicsElement, Group>()
  const placementDirections = { below: 0, above: 0, right: 0, left: 0 }
  let renderedOccurrences = 0
  for (const svg of host.querySelectorAll<SVGSVGElement>('svg')) {
    obstacles.set(svg, [...svg.querySelectorAll<SVGGraphicsElement>('.notehead,.accid,.stem,.beam,.flag,.rest,.clef,.keySig,.meterSig,.tie,.slur,.dynam,.dir,.artic,.fermata,.syl,.tupletNum,.barLine,text,.staff > path')]
      .filter(element => element.closest('svg') === svg)
      .map(element => boxIn(element, svg)).filter((b): b is Box => !!b && b.width > 0 && b.height > 0))
    occupied.set(svg, [])
    for (const staff of svg.querySelectorAll<SVGGraphicsElement>('.staff')) {
      if (staff.closest('svg') !== svg) continue
      const lines = [...staff.children].filter(child => child.tagName.toLowerCase() === 'path')
        .map(child => boxIn(child as SVGGraphicsElement, svg)).filter((b): b is Box => !!b)
      if (lines.length) staffFrames.set(staff, union(lines))
    }
  }
  for (const note of notes) {
    const element = host.querySelector<SVGGraphicsElement>(`[id="${note.xmlId}"].note`)
    const head = element?.querySelector<SVGGraphicsElement>('.notehead'), svg = element?.closest('svg')
    if (!element || !head || !svg) continue
    renderedOccurrences++
    element.dataset.noteId = note.noteId
    resolved.add(note.noteId)
    if (note.hidden) { hidden.add(note.noteId); continue }
    if (seen.has(note.noteId) || !enabled) continue
    seen.add(note.noteId)
    const anchor = boxIn(head, svg)
    if (!anchor || !anchor.height) { pending.add(note.noteId); continue }
    const size = reflowFontSize(note.fontSizePoints, anchor.height, svg.viewBox.baseVal.width)
    const label = document.createElementNS(ns, 'g')
    label.setAttribute('class', 'reflow-label'); label.dataset.noteId = note.noteId
    label.setAttribute('fill', note.color)
    const text = document.createElementNS(ns, 'text')
    text.setAttribute('font-size', String(size)); text.setAttribute('font-family', 'Arial, sans-serif')
    text.textContent = (note.alteration > 0 ? '♯'.repeat(note.alteration) : '♭'.repeat(-note.alteration)) + note.degree
    label.append(text); svg.append(label)
    const textBox = text.getBBox()
    for (let i = 0; i < Math.abs(note.octaveDots); i++) {
      const dot = document.createElementNS(ns, 'circle')
      dot.setAttribute('cx', String(textBox.x + textBox.width - size * .28))
      dot.setAttribute('cy', String(note.octaveDots > 0 ? textBox.y - size * (.22 + i * .22) : textBox.y + textBox.height + size * (.2 + i * .22)))
      dot.setAttribute('r', String(size * .065)); label.append(dot)
    }
    const key = element.closest<SVGGraphicsElement>('.chord') ?? element
    const staffElement = element.closest<SVGGraphicsElement>('.staff')
    const group: Group = groups.get(key) ?? { key, svg, staff: staffElement ? staffFrames.get(staffElement) : undefined, members: [], anchor }
    group.members.push({ note, label, bounds: label.getBBox(), anchor, size })
    group.anchor = union(group.members.map(member => member.anchor)); groups.set(key, group)
  }
  const pages = [...host.querySelectorAll<SVGSVGElement>('svg')]
  const ordered = [...groups.values()].sort((a, b) => pages.indexOf(a.svg) - pages.indexOf(b.svg) ||
    (a.staff?.y ?? a.anchor.y) - (b.staff?.y ?? b.anchor.y) || a.anchor.x - b.anchor.x)
  for (const group of ordered) {
    group.members.sort((a, b) => a.anchor.y - b.anchor.y)
    const { svg, anchor, staff } = group, page = svg.viewBox.baseVal
    const interline = staff ? staff.height / 4 : anchor.height * 1.2
    const pad = interline * .15, gap = interline * .15
    const cx = anchor.x + anchor.width / 2
    const frames = [...staffFrames.entries()].filter(([element, b]) => element.closest('svg') === svg && cx >= b.x && cx <= b.x + b.width).map(([, b]) => b)
    const previous = staff ? Math.max(page.y, ...frames.filter(b => b.y + b.height < staff.y - 1).map(b => b.y + b.height)) : page.y
    const following = staff ? Math.min(page.y + page.height, ...frames.filter(b => b.y > staff.y + staff.height + 1).map(b => b.y)) : page.y + page.height
    const minY = staff && previous !== page.y ? (previous + staff.y) / 2 : page.y
    const maxY = staff && following !== page.y + page.height ? (following + staff.y + staff.height) / 2 : page.y + page.height
    const neighbors = ordered.filter(other => other !== group && other.svg === svg &&
      (staff && other.staff ? Math.abs(staff.y - other.staff.y) < interline : Math.abs(anchor.y - other.anchor.y) < interline * 4))
    let chosen: Candidate | undefined
    let chosenScale = 1
    const minimum = reflowFontSize(3, anchor.height, page.width)
    const countScale = chordFontScale(group.members.length)
    const scaleOf = (member: Member, scale: number) => member.note.fontSizePoints === null ? Math.max(minimum / member.size, countScale * scale) : 1
    const scales = group.members.some(m => m.note.fontSizePoints === null) ? automaticFontScales(1, 0) : [1]
    search: for (const direction of ['below', 'above', 'right', 'left'] as const) {
      for (const scale of scales) {
        const width = Math.max(...group.members.map(m => m.bounds.width * scaleOf(m, scale)))
        const height = group.members.reduce((sum, m) => sum + m.bounds.height * scaleOf(m, scale), 0) + gap * (group.members.length - 1)
        for (const candidate of reflowCandidates(anchor, width, height, interline, staff)) {
          if (candidate.direction !== direction) continue
          if (candidate.x - pad < page.x || candidate.y - pad < minY || candidate.x + width + pad > page.x + page.width || candidate.y + height + pad > maxY) continue
          const labelX = candidate.x + width / 2
          if (neighbors.some(other => Math.abs(other.anchor.x + other.anchor.width / 2 - labelX) + interline * .1 < Math.abs(cx - labelX))) continue
          const reserved = { x: candidate.x - pad, y: candidate.y - pad, width: width + 2 * pad, height: height + 2 * pad }
          if ((obstacles.get(svg) ?? []).some(obstacle => overlaps(reserved, obstacle)) || (occupied.get(svg) ?? []).some(other => overlaps(reserved, other))) continue
          chosen = candidate; chosenScale = scale; occupied.get(svg)!.push(reserved); break search
        }
      }
    }
    let cursor = chosen?.y ?? 0
    for (const member of group.members) {
      if (!chosen) { member.label.remove(); pending.add(member.note.noteId); continue }
      const scale = scaleOf(member, chosenScale), b = member.bounds
      const width = b.width * scale, height = b.height * scale, x = chosen.x + (chosen.width - width) / 2
      member.label.setAttribute('transform', `translate(${x - b.x * scale} ${cursor - b.y * scale}) scale(${scale})`)
      member.label.dataset.bounds = JSON.stringify({ x, y: cursor, width, height })
      member.label.dataset.fontSize = String(member.size * scale)
      member.label.dataset.direction = chosen.direction; member.label.dataset.chordId = group.key.id
      cursor += height + gap; placed.add(member.note.noteId); placementDirections[chosen.direction]++
    }
  }
  return { layoutPolicy: 'below-first-v1', fontSizingPolicy: 'adaptive-chord-count-v2', placementDirections, mappedHeads: resolved.size, renderedOccurrences, placed: placed.size, pending: pending.size, hidden: hidden.size,
    missingIds: notes.filter(note => !host.querySelector(`[id="${note.xmlId}"].note`)).map(note => note.xmlId) }
}
