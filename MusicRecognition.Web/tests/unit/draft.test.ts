import assert from 'node:assert/strict'
import { test } from 'node:test'
import { ref } from 'vue'
import type { AnnotationStyle, Draft, Numbering, Placement } from '../../shared/types/score'
import { applyDraftEdit, draftFingerprint, emptyDraft, normalizeDraft, parseStoredDraft, revisionBaseline, revisionPayload, useDraftHistory } from '../../app/utils/draft'

const numbering: Numbering = { mode: 'FixedC', keys: [] }
const placement: Placement = { x: 10, y: 20, hidden: false }
const style: AnnotationStyle = { fontSizePoints: null, color: '#abcdef' }
const score = {
  notes: [{ id: 'n1', placement, style }, { id: 'n2', placement: null, style }],
  numbering,
  annotationStyle: style,
}

function makeDraft(): Draft {
  const draft = emptyDraft()
  draft.numbering = numbering
  draft.annotationStyle = { ...style }
  draft.pitches.n1 = { step: 'C', alter: 1, octave: 4 }
  draft.placements.n1 = { ...placement }
  draft.styles.n1 = { ...style }
  draft.styles.n2 = { ...style }
  return draft
}

test('emptyDraft supplies every map', () => {
  const draft = emptyDraft()
  assert.deepEqual(draft.pitches, {})
  assert.deepEqual(draft.placements, {})
  assert.deepEqual(draft.numbering, { mode: 'FixedC', keys: [] })
  assert.deepEqual(draft.annotationStyle, { fontSizePoints: null, color: '#000000' })
  assert.deepEqual(draft.styles, {})
})

test('normalizeDraft fills missing maps and keeps provided values', () => {
  const draft = normalizeDraft({ numbering })
  assert.deepEqual(draft.pitches, {})
  assert.deepEqual(draft.placements, {})
  assert.deepEqual(draft.numbering, numbering)
  assert.deepEqual(draft.annotationStyle, { fontSizePoints: null, color: '#000000' })
  assert.deepEqual(draft.styles, {})
})

test('parseStoredDraft accepts valid drafts and rejects corrupt or incomplete records', () => {
  const parsed = parseStoredDraft(JSON.stringify(makeDraft()))
  assert.deepEqual(parsed, makeDraft())
  assert.equal(parseStoredDraft('{'), null)
  assert.equal(parseStoredDraft(null), null)
  assert.equal(parseStoredDraft('{"pitches":{}}'), null)
})

test('applyDraftEdit clones before editing and reports no-op edits', () => {
  const draft = makeDraft()
  const next = applyDraftEdit(draft, value => { value.placements.n1.x = 99 })
  assert.ok(next)
  assert.notStrictEqual(next, draft)
  assert.equal(draft.placements.n1.x, 10)
  assert.equal(next.placements.n1.x, 99)
  assert.equal(applyDraftEdit(draft, value => { value.placements.n1.x = 10 }), null)
})

test('dirty fingerprints ignore color case and match the server baseline', () => {
  const draft = makeDraft()
  draft.pitches = {}
  assert.equal(draftFingerprint(draft), revisionBaseline(score))
  draft.styles.n1 = { fontSizePoints: null, color: '#ABCDEF' }
  assert.equal(draftFingerprint(draft), revisionBaseline(score))
  draft.styles.n1 = { fontSizePoints: 7, color: '#ABCDEF' }
  assert.notEqual(draftFingerprint(draft), revisionBaseline(score))
})

test('revisionPayload writes overrides and clears deleted server styles', () => {
  const draft = makeDraft()
  draft.styles = { n1: { fontSizePoints: 7, color: '#abcdef' } }
  const payload = revisionPayload(draft, score)
  assert.equal(payload.corrections.length, 1)
  assert.equal(payload.corrections[0].noteId, 'n1')
  assert.equal(payload.placements[0].placement.x, 10)
  assert.deepEqual(payload.styleChanges, [
    { noteId: 'n1', style: { fontSizePoints: 7, color: '#ABCDEF' } },
    { noteId: 'n2', style: null },
  ])
})

test('useDraftHistory keeps the last fifty edits and supports undo, redo, and clear', () => {
  const draft = ref(emptyDraft())
  const history = useDraftHistory(draft)
  for (let value = 1; value <= 60; value++) {
    draft.value = { ...draft.value, annotationStyle: { ...draft.value.annotationStyle, fontSizePoints: value } }
  }
  assert.equal(history.undoStack.value.length, 50)
  assert.equal(draft.value.annotationStyle.fontSizePoints, 60)
  history.undo()
  assert.equal(draft.value.annotationStyle.fontSizePoints, 59)
  history.redo()
  assert.equal(draft.value.annotationStyle.fontSizePoints, 60)
  history.clear()
  assert.equal(history.canUndo.value, false)
})
