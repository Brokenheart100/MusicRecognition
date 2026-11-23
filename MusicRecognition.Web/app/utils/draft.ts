import { useRefHistory } from '@vueuse/core'
import type { Ref } from 'vue'
import { defaultAnnotationStyle, DraftSchema, type AnnotationStyle, type Draft, type Numbering, type Placement } from '../../shared/types/score'

type ScoreSnapshot = {
  notes: { id: string; placement?: Placement | null; style?: AnnotationStyle | null }[]
  numbering: Numbering
  annotationStyle?: AnnotationStyle | null
}

export function emptyDraft(): Draft {
  return { pitches: {}, placements: {}, numbering: { mode: 'FixedC', keys: [] }, annotationStyle: defaultAnnotationStyle(), styles: {} }
}

export function normalizeDraft(value: Partial<Draft>): Draft {
  return { pitches: value.pitches ?? {}, placements: value.placements ?? {}, numbering: value.numbering ?? { mode: 'FixedC', keys: [] }, annotationStyle: value.annotationStyle ?? defaultAnnotationStyle(), styles: value.styles ?? {} }
}

export function parseStoredDraft(raw: string | null): Draft | null {
  if (!raw) return null
  try {
    const parsed = DraftSchema.safeParse(JSON.parse(raw))
    return parsed.success ? parsed.data : null
  } catch { return null }
}

export function applyDraftEdit(draft: Draft, edit: (value: Draft) => void): Draft | null {
  const next = JSON.parse(JSON.stringify(draft)) as Draft
  edit(next)
  return JSON.stringify(next) === JSON.stringify(draft) ? null : next
}

export function useDraftHistory(draft: Ref<Draft>) {
  return useRefHistory(draft, { capacity: 50, clone: false, flush: 'sync' })
}

export function snapshotPlacements(score: ScoreSnapshot): Record<string, Placement> {
  return Object.fromEntries(score.notes.filter(note => note.placement).map(note => [note.id, note.placement!]))
}

export function snapshotStyles(score: ScoreSnapshot): Record<string, AnnotationStyle> {
  return Object.fromEntries(score.notes.filter(note => note.style).map(note => [note.id, { fontSizePoints: note.style!.fontSizePoints, color: note.style!.color.toUpperCase() }]))
}

function normalizedStyles(draft: Draft) {
  return Object.fromEntries(Object.entries(draft.styles ?? {}).map(([id, style]) => [id, { fontSizePoints: style.fontSizePoints, color: style.color.toUpperCase() }]))
}

export function draftFingerprint(draft: Draft): string {
  return JSON.stringify({ ...draft, styles: normalizedStyles(draft), annotationStyle: { fontSizePoints: draft.annotationStyle.fontSizePoints, color: draft.annotationStyle.color.toUpperCase() } })
}

export function revisionBaseline(score: ScoreSnapshot): string {
  const annotationStyle = score.annotationStyle ?? defaultAnnotationStyle()
  return JSON.stringify({ pitches: {}, placements: snapshotPlacements(score), numbering: score.numbering, annotationStyle: { fontSizePoints: annotationStyle.fontSizePoints, color: annotationStyle.color.toUpperCase() }, styles: snapshotStyles(score) })
}

export function revisionPayload(draft: Draft, score: ScoreSnapshot) {
  const styles = normalizedStyles(draft)
  return {
    numbering: draft.numbering,
    annotationStyle: draft.annotationStyle,
    corrections: Object.entries(draft.pitches).map(([noteId, pitch]) => ({ noteId, pitch })),
    placements: Object.entries(draft.placements).map(([noteId, placement]) => ({ noteId, placement })),
    styleChanges: [
      ...Object.entries(styles).map(([noteId, style]) => ({ noteId, style })),
      ...score.notes.filter(note => note.style && !styles[note.id]).map(note => ({ noteId: note.id, style: null })),
    ],
  }
}
