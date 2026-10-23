import { z } from 'zod'
import { zAnnotationPlacement, zAnnotationStyle, zNumberingOptions, zPitch } from '../api/generated/zod.gen'
import type { AnnotationStyle, Pitch, AnnotationPlacement as Placement, NumberingOptions as Numbering } from '../api/generated/types.gen'
export type { AnnotationStyle } from '../api/generated/types.gen'
export type { Step, Pitch, AnnotationPlacement as Placement, KeyChange, NumberingOptions as Numbering, Note, JobView as Job, PageJobView as PageJob, DocumentExportView as DocumentExport, RevisionView as Revision, AnnotationView as Annotation, AnnotationsView as Annotations } from '../api/generated/types.gen'
export { zJobView as JobSchema, zRevisionView as RevisionSchema, zDocumentExportView as DocumentExportSchema, zAnnotationsView as AnnotationsSchema } from '../api/generated/zod.gen'

export interface RecentScore { id: string; token: string; name: string; date: string }
export const defaultAnnotationStyle = (): AnnotationStyle => ({ fontSizePoints: null, color: '#000000' })
export interface Draft { pitches: Record<string, Pitch>; placements: Record<string, Placement>; numbering: Numbering; annotationStyle: AnnotationStyle; styles: Record<string, AnnotationStyle> }
export const RecentScoreSchema = z.object({ id: z.string().uuid(), token: z.string().min(32).max(200), name: z.string(), date: z.string() })
export const DraftSchema = z.object({
  pitches: z.record(z.string(), zPitch),
  placements: z.record(z.string(), zAnnotationPlacement),
  numbering: zNumberingOptions,
  annotationStyle: zAnnotationStyle.default(defaultAnnotationStyle),
  styles: z.record(z.string(), zAnnotationStyle).default({}),
})
