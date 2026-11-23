import { z } from 'zod'
import { zAnnotationPlacement, zAnnotationStyle, zNumberingOptions, zPitch } from '../api/generated/zod.gen'
import type { AnnotationStyle, Pitch, AnnotationPlacement as Placement, NumberingOptions as Numbering } from '../api/generated/types.gen'
// 前端"乐谱数据"的统一入口：把自动生成的接口类型和校验规则打包，再补上本机草稿结构。
// 页面里只从这里 import，不直接碰 generated 目录；将来接口变了也只需要改这一处。
// 带 z 前缀的是运行时格式校验（Zod），用来防止后端返回意外数据；不带的是纯类型。
export type { AnnotationStyle } from '../api/generated/types.gen'
export type { Step, Pitch, AnnotationPlacement as Placement, KeyChange, NumberingOptions as Numbering, Note, NoteAddition, JobView as Job, PageJobView as PageJob, DocumentExportView as DocumentExport, RevisionView as Revision, AnnotationView as Annotation, AnnotationsView as Annotations } from '../api/generated/types.gen'
// 把接口生成的长类型名和校验规则重新导出，页面用短名字即可（如 Placement、Numbering）。
export { zJobView as JobSchema, zRevisionView as RevisionSchema, zDocumentExportView as DocumentExportSchema, zAnnotationsView as AnnotationsSchema } from '../api/generated/zod.gen'

// 最近打开的乐谱条目：token 是访问令牌（token），凭它才能读写这份乐谱，属于敏感信息。
export interface RecentScore { id: string; token: string; name: string; date: string }
// 标注默认样式：字号不指定（null 表示跟随页面），颜色固定黑色。
export const defaultAnnotationStyle = (): AnnotationStyle => ({ fontSizePoints: null, color: '#000000' })
// 本机草稿（draft）：记录用户在当前浏览器里改过的音高、标注位置和样式，尚未提交给后端。
export interface Draft { pitches: Record<string, Pitch>; placements: Record<string, Placement>; numbering: Numbering; annotationStyle: AnnotationStyle; styles: Record<string, AnnotationStyle> }
// 最近乐谱的校验规则：token 长度限定 32~200，防止把异常字符串当成令牌存下来。
export const RecentScoreSchema = z.object({ id: z.string().uuid(), token: z.string().min(32).max(200), name: z.string(), date: z.string() })
// 草稿校验规则：从浏览器缓存读草稿时先用它校验，旧版本结构对不上就丢弃重建。
export const DraftSchema = z.object({
  pitches: z.record(z.string(), zPitch),
  placements: z.record(z.string(), zAnnotationPlacement),
  numbering: zNumberingOptions,
// default 表示旧草稿没有这一项时自动补默认值，不会因为缺字段而读不出来。
  annotationStyle: zAnnotationStyle.default(defaultAnnotationStyle),
  styles: z.record(z.string(), zAnnotationStyle).default({}),
})
