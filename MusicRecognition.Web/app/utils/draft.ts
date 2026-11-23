import { useRefHistory } from '@vueuse/core'
import type { Ref } from 'vue'
import { defaultAnnotationStyle, DraftSchema, type AnnotationStyle, type Draft, type NoteAddition, type Numbering, type Placement } from '../../shared/types/score'
// 本机草稿（draft）工具集：管理"用户在当前浏览器里还没提交的修改"。
// 包括：新建空草稿、读旧缓存时补齐字段、比较草稿是否变化、生成提交给后端的修订数据。
// 草稿只存在浏览器里，刷新页面不丢，但换设备看不到；提交成功后才会变成服务器上的修订。

// 已保存乐谱的快照：只保留和草稿对比有关的字段。
type ScoreSnapshot = {
// 每个音记录服务器上保存的标注位置（placement）和样式（style），没有则为空。
  notes: { id: string; placement?: Placement | null; style?: AnnotationStyle | null }[]
  numbering: Numbering
  annotationStyle?: AnnotationStyle | null
}

// 新建一份空草稿：没有任何修改，简谱按固定 C 调（FixedC）计算。
export function emptyDraft(): Draft {
// keys 是转调记录，空数组表示没有转调；样式用默认值。
  return { pitches: {}, placements: {}, numbering: { mode: 'FixedC', keys: [] }, annotationStyle: defaultAnnotationStyle(), styles: {} }
}

// 把可能缺字段的旧草稿补全：缺什么就用默认值填上，避免页面读到 undefined 出错。
export function normalizeDraft(value: Partial<Draft>): Draft {
  return { pitches: value.pitches ?? {}, placements: value.placements ?? {}, numbering: value.numbering ?? { mode: 'FixedC', keys: [] }, annotationStyle: value.annotationStyle ?? defaultAnnotationStyle(), styles: value.styles ?? {} }
}

// 从浏览器缓存里读草稿：先校验格式，格式不对就当没有草稿。
export function parseStoredDraft(raw: string | null): Draft | null {
  if (!raw) return null
  try {
// safeParse 不抛异常：校验失败会返回 success=false，这里当作没有草稿处理。
    const parsed = DraftSchema.safeParse(JSON.parse(raw))
    return parsed.success ? parsed.data : null
  } catch { return null }
}

// 在草稿副本上执行一次修改；如果改完和原来完全一样，返回 null 表示没有变化。
export function applyDraftEdit(draft: Draft, edit: (value: Draft) => void): Draft | null {
// 先深拷贝再改，避免直接改到界面上正在显示的旧状态（撤销历史还要用旧值）。
  const next = JSON.parse(JSON.stringify(draft)) as Draft
  edit(next)
// 用序列化结果判断有没有实际变化，省得产生无意义的撤销记录。
  return JSON.stringify(next) === JSON.stringify(draft) ? null : next
}

// 给草稿接上撤销/重做历史，编辑器的撤销按钮和 Ctrl+Z 都靠它。
export function useDraftHistory(draft: Ref<Draft>) {
// 50 是最多保留 50 步历史；sync 表示改完立刻记入历史，不等下一帧。
  return useRefHistory(draft, { capacity: 50, clone: false, flush: 'sync' })
}

// 从乐谱快照里提取"有标注位置"的音，转成 音符 id → 位置的映射。
export function snapshotPlacements(score: ScoreSnapshot): Record<string, Placement> {
  return Object.fromEntries(score.notes.filter(note => note.placement).map(note => [note.id, note.placement!]))
}

// 同样提取样式映射，并把颜色统一成大写，方便后面比较。
export function snapshotStyles(score: ScoreSnapshot): Record<string, AnnotationStyle> {
  return Object.fromEntries(score.notes.filter(note => note.style).map(note => [note.id, { fontSizePoints: note.style!.fontSizePoints, color: note.style!.color.toUpperCase() }]))
}

// 把草稿里的样式也统一成可比对的形式（颜色大写）；没有样式时返回空对象。
function normalizedStyles(draft: Draft) {
  return Object.fromEntries(Object.entries(draft.styles ?? {}).map(([id, style]) => [id, { fontSizePoints: style.fontSizePoints, color: style.color.toUpperCase() }]))
}

// 草稿指纹：把草稿变成一串固定文本，用来判断"和上次保存的是不是同一份"。
export function draftFingerprint(draft: Draft): string {
  return JSON.stringify({ ...draft, styles: normalizedStyles(draft), annotationStyle: { fontSizePoints: draft.annotationStyle.fontSizePoints, color: draft.annotationStyle.color.toUpperCase() } })
}

// 修订基线：把服务器上当前版本的标注整理成和草稿一样的格式，便于逐项比较。
export function revisionBaseline(score: ScoreSnapshot): string {
// 服务器还没保存过整体样式时，按默认样式算，保证两边结构一致。
  const annotationStyle = score.annotationStyle ?? defaultAnnotationStyle()
  return JSON.stringify({ pitches: {}, placements: snapshotPlacements(score), numbering: score.numbering, annotationStyle: { fontSizePoints: annotationStyle.fontSizePoints, color: annotationStyle.color.toUpperCase() }, styles: snapshotStyles(score) })
}

// 生成提交给后端的修订内容：简谱设置、整体样式，以及逐音的修改和位置变化。
// addedNotes/removedNoteIds 用于手工补录或删除音符；删除时同时过滤掉该音符的草稿修改。
export function revisionPayload(draft: Draft, score: ScoreSnapshot, addedNotes: NoteAddition[] = [], removedNoteIds: string[] = []) {
  const styles = normalizedStyles(draft)
  const removed = new Set(removedNoteIds)
  return {
    numbering: draft.numbering,
    annotationStyle: draft.annotationStyle,
// 只提交用户改过音高的音，没改的保持原样；已删除的音符不再提交修改。
    corrections: Object.entries(draft.pitches).filter(([noteId]) => !removed.has(noteId)).map(([noteId, pitch]) => ({ noteId, pitch })),
    placements: Object.entries(draft.placements).filter(([noteId]) => !removed.has(noteId)).map(([noteId, placement]) => ({ noteId, placement })),
// style: null 表示这个音原来有自定义样式、现在被恢复成默认了。
    styleChanges: [
      ...Object.entries(styles).filter(([noteId]) => !removed.has(noteId)).map(([noteId, style]) => ({ noteId, style })),
      ...score.notes.filter(note => note.style && !styles[note.id] && !removed.has(note.id)).map(note => ({ noteId: note.id, style: null })),
    ],
    addedNotes,
    removedNoteIds,
  }
}
