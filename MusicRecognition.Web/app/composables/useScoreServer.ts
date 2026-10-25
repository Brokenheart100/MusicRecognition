import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { createClient } from '~~/shared/api/generated/client'
import { zRevisionRequest } from '~~/shared/api/generated/zod.gen'
import * as sdk from '~~/shared/api/generated/sdk.gen'
import type { RecentScore } from '~~/shared/types/score'
import type { RevisionRequest, JobView, RevisionView, DocumentExportView, AnnotationsView } from '~~/shared/api/generated/types.gen'

const running = (state?: string | null) => !!state && ['Queued', 'Processing', 'Cancelling'].includes(state)
const interval = () => document.hidden ? 5000 : 1500

// Query owns server snapshots; Pinia exposes readonly projections to the editor.
export function useScoreServer(active: Ref<RecentScore | null>, pageNumber: Ref<number>, selectedVersion: Ref<number>) {
  const cache = useQueryClient()
  const scope = computed(() => ({ id: active.value?.id ?? '', token: active.value?.token ?? '', key: import.meta.client ? crypto.randomUUID() : '' }))
  const client = createClient({ baseUrl: import.meta.client ? location.origin : 'http://localhost', credentials: 'omit' })
  function access(id: string) { return { client, path: { id }, headers: { 'X-Score-Token': scope.value.token }, throwOnError: true as const } }
  const prefix = computed(() => ['scores', scope.value.key] as const)
  const documentQuery = useQuery<JobView>(computed(() => {
    const options = access(scope.value.id)
    return { queryKey: [...prefix.value, 'job', scope.value.id], enabled: import.meta.client && !!active.value,
      queryFn: async ({ signal }: { signal: AbortSignal }) => (await sdk.getScore({ ...options, signal })).data,
      refetchInterval: (query: { state: { data?: JobView } }) => {
        const value = query.state.data
        return running(value?.state) || value?.pages?.some(p => running(p.state) || running(p.exportState)) ? interval() : false
      },
    }
  }))
  const documentJob = computed(() => documentQuery.data.value ?? null)
  const multiPage = computed(() => (documentJob.value?.pageCount ?? 1) > 1)
  const currentJobId = computed(() => multiPage.value ? documentJob.value?.pages?.find(p => p.number === pageNumber.value)?.id ?? '' : scope.value.id)
  const pageQuery = useQuery<JobView>(computed(() => {
    const id = currentJobId.value, options = access(id)
    return { queryKey: [...prefix.value, 'job', id], enabled: import.meta.client && multiPage.value && !!id,
      queryFn: async ({ signal }: { signal: AbortSignal }) => (await sdk.getScore({ ...options, signal })).data,
      refetchInterval: (query: { state: { data?: JobView } }) => running(query.state.data?.state) ? interval() : false,
    }
  }))
  const job = computed(() => multiPage.value ? pageQuery.data.value ?? null : documentJob.value)
  const version = computed(() => selectedVersion.value || job.value?.currentRevision || 0)
  const revisionQuery = useQuery<RevisionView>(computed(() => {
    const options = { ...access(currentJobId.value), query: { version: version.value } }
    return { queryKey: [...prefix.value, 'revision', currentJobId.value, version.value], enabled: import.meta.client && !!currentJobId.value && version.value > 0,
      queryFn: async ({ signal }: { signal: AbortSignal }) => (await sdk.getRevision({ ...options, signal })).data,
      refetchInterval: (query: { state: { data?: RevisionView } }) => running(query.state.data?.state) ? interval() : false,
    }
  }))
  const revision = computed(() => revisionQuery.data.value ?? null)
  const layoutQuery = useQuery<AnnotationsView>(computed(() => {
    const options = { ...access(currentJobId.value), query: { version: version.value } }
    return { queryKey: [...prefix.value, 'layout', currentJobId.value, version.value], enabled: import.meta.client && revision.value?.state === 'Ready',
      queryFn: async ({ signal }: { signal: AbortSignal }) => (await sdk.getScoreLayout({ ...options, signal })).data, staleTime: Infinity,
    }
  }))
  const exportQuery = useQuery<DocumentExportView>(computed(() => {
    const options = { ...access(scope.value.id), path: { id: scope.value.id, version: documentJob.value?.currentRevision ?? 0 } }
    return { queryKey: [...prefix.value, 'export', options.path.id, options.path.version], enabled: import.meta.client && multiPage.value && options.path.version > 0,
      queryFn: async ({ signal }: { signal: AbortSignal }) => (await sdk.getDocumentExport({ ...options, signal })).data,
      refetchInterval: (query: { state: { data?: DocumentExportView } }) => running(query.state.data?.state) ? interval() : false,
    }
  }))
  const sourceQuery = useQuery<Blob>(computed(() => {
    const options = access(currentJobId.value)
    return { queryKey: [...prefix.value, 'source', currentJobId.value], enabled: import.meta.client && !!currentJobId.value,
      queryFn: async ({ signal }: { signal: AbortSignal }) => (await sdk.getScoreArtifact({ ...options, path: { ...options.path, name: 'source' }, signal, parseAs: 'blob' })).data as Blob,
      staleTime: Infinity,
    }
  }))
  const importMutation = useMutation({ mutationFn: async (input: { file: File; token: string; key: string; preprocessing: 'None' | 'Adaptive' }) =>
    (await sdk.importScore({ client, body: { file: input.file, preprocessing: input.preprocessing }, headers: { 'X-Score-Token': input.token, 'Idempotency-Key': input.key }, throwOnError: true })).data })
  const revisionMutation = useMutation({ mutationFn: async (input: { id: string; body: RevisionRequest }) => (await sdk.createRevision({ ...access(input.id), body: zRevisionRequest.parse(input.body) })).data })
  const exportMutation = useMutation({ mutationFn: async () => (await sdk.createDocumentExport(access(scope.value.id))).data })
  const commandMutation = useMutation({ mutationFn: async (action: 'cancel' | 'retry') => (await (action === 'cancel' ? sdk.cancelScore : sdk.retryScore)(access(scope.value.id))).data })
  async function refresh() { await cache.invalidateQueries({ queryKey: prefix.value }) }
  async function artifact(id: string, version: number) {
    return (await sdk.getScoreArtifact({ ...access(id), path: { id, name: 'annotated.pdf' }, query: { version }, parseAs: 'blob' })).data as Blob
  }
  async function musicXml(id: string, signal: AbortSignal) {
    return (await sdk.getScoreArtifact({ ...access(id), path: { id, name: 'score.mxl' }, signal, parseAs: 'blob' })).data as Blob
  }
  async function reflow(id: string, version: number, signal: AbortSignal) {
    return (await sdk.getScoreReflow({ ...access(id), query: { version }, signal })).data
  }
  // Cancel and evict private content, including blobs, when switching documents.
  watch(scope, (_value, previous) => { if (previous) { void cache.cancelQueries({ queryKey: ['scores', previous.key] }); cache.removeQueries({ queryKey: ['scores', previous.key] }) } })
  const queryError = computed(() => documentQuery.error.value || pageQuery.error.value || revisionQuery.error.value || layoutQuery.error.value || sourceQuery.error.value || exportQuery.error.value)
  return { job, documentJob, multiPage, currentJobId, revision, annotations: computed(() => layoutQuery.data.value ?? null), documentExport: computed(() => exportQuery.data.value ?? null),
    sourceBlob: sourceQuery.data, queryError, importMutation, revisionMutation, exportMutation, commandMutation, refresh, artifact, musicXml, reflow }
}
