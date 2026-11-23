import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { createClient } from '~~/shared/api/generated/client'
import { zRevisionRequest } from '~~/shared/api/generated/zod.gen'
import * as sdk from '~~/shared/api/generated/sdk.gen'
import type { RecentScore } from '~~/shared/types/score'
import type { RevisionRequest, JobView, RevisionView, DocumentExportView, AnnotationsView, RecognitionRegion } from '~~/shared/api/generated/types.gen'
// 服务端数据层：把后端 HTTP 接口包装成 TanStack Query 的查询和变更，
// 让界面拿到识别任务、修订、标注布局、原稿和导出文件，并自动轮询运行中的任务。
// 界面只订阅这里的结果，不直接发请求；缓存、刷新和失效都集中在这一个文件。
// 一句话：这是前端和服务器之间的唯一通道。

// 这三种状态表示后台还在干活，需要继续轮询；其余状态可以停下来。
const running = (state?: string | null) => !!state && ['Queued', 'Processing', 'Cancelling'].includes(state)
// 标签页在前台时 1.5 秒轮询一次，切到后台放慢到 5 秒：省电也省服务器。
const interval = (visible = 1500) => document.hidden ? 5000 : visible

// active 是当前乐谱，pageNumber 是当前页，selectedVersion 是选择的历史修订号。
// Query owns server snapshots; Pinia exposes readonly projections to the editor.
export function useScoreServer(active: Ref<RecentScore | null>, pageNumber: Ref<number>, selectedVersion: Ref<number>) {
// 每个乐谱实例生成一个随机缓存命名空间，切换文档时用它隔离旧数据。
  const cache = useQueryClient()
// scope 收集请求要用的三样东西：乐谱 id、访问令牌（token）、本次会话的缓存 key。
  const scope = computed(() => ({ id: active.value?.id ?? '', token: active.value?.token ?? '', key: import.meta.client ? crypto.randomUUID() : '' }))
// 浏览器里用当前站点地址请求，且不带 cookie；身份只靠请求头里的访问令牌。
  const client = createClient({ baseUrl: import.meta.client ? location.origin : 'http://localhost', credentials: 'omit' })
// 统一的请求参数：路径里的乐谱 id、X-Score-Token 请求头，以及出错时抛异常。
  function access(id: string) { return { client, path: { id }, headers: { 'X-Score-Token': scope.value.token }, throwOnError: true as const } }
// 查询键的前缀：同一乐谱的各类查询都挂在它下面，方便一起失效或删除。
  const prefix = computed(() => ['scores', scope.value.key] as const)
// 整份乐谱的任务（job）查询：上传后先看它是排队、识别还是已经结束。
// 只要文档或任意一页还在运行，就按 interval 自动刷新；全部结束就停止轮询。
  const documentQuery = useQuery<JobView>(computed(() => {
    const options = access(scope.value.id)
    return {
      queryKey: [...prefix.value, 'job', scope.value.id], enabled: import.meta.client && !!active.value,
      queryFn: async ({ signal }: { signal: AbortSignal }) => (await sdk.getScore({ ...options, signal })).data,
// 轮询节奏由状态决定：任务结束就返回 false，TanStack Query 不再自动请求。
      refetchInterval: (query: { state: { data?: JobView } }) => {
        const value = query.state.data
        return running(value?.state) || value?.pages?.some(p => running(p.state) || running(p.exportState)) ? interval() : false
      },
    }
  }))
// data 可能还没返回，用 null 表示还不知道，界面据此显示加载中。
  const documentJob = computed(() => documentQuery.data.value ?? null)
// 页数大于 1 说明是多页 PDF，前端要额外维护当前页的概念。
  const multiPage = computed(() => (documentJob.value?.pageCount ?? 1) > 1)
// 多页时每一页是独立任务，取当前页的任务 id；单页时整份文档就是一个任务。
  const currentJobId = computed(() => multiPage.value ? documentJob.value?.pages?.find(p => p.number === pageNumber.value)?.id ?? '' : scope.value.id)
// 多页模式下单独查询当前页的进度，只在这一页运行时轮询。
  const pageQuery = useQuery<JobView>(computed(() => {
    const id = currentJobId.value, options = access(id)
    return {
      queryKey: [...prefix.value, 'job', id], enabled: import.meta.client && multiPage.value && !!id,
      queryFn: async ({ signal }: { signal: AbortSignal }) => (await sdk.getScore({ ...options, signal })).data,
      refetchInterval: (query: { state: { data?: JobView } }) => running(query.state.data?.state) ? interval() : false,
    }
  }))
// 对界面只暴露当前这一页或这份文档的任务，隐藏单页和多页两种内部结构。
  const job = computed(() => multiPage.value ? pageQuery.data.value ?? null : documentJob.value)
// 用户在看历史版本时用 selectedVersion，否则跟随服务器的最新修订号。
  const version = computed(() => selectedVersion.value || job.value?.currentRevision || 0)
// 修订（revision）查询：保存后的乐谱快照和排版状态都从这里读。
// 排版还在排队或处理中时继续轮询，直到它变成 Ready 或 Failed。
  const revisionQuery = useQuery<RevisionView>(computed(() => {
    const id = currentJobId.value
    const options = { ...access(id), path: { id, version: version.value } }
    return {
      queryKey: [...prefix.value, 'revision', id, version.value], enabled: import.meta.client && !!id && version.value > 0,
      queryFn: async ({ signal }: { signal: AbortSignal }) => (await sdk.getRevision({ ...options, signal })).data,
      refetchInterval: (query: { state: { data?: RevisionView } }) => running(query.state.data?.state) ? interval(2500) : false,
    }
  }))
  const revision = computed(() => revisionQuery.data.value ?? null)
// 标注布局查询：只有修订已经 Ready 才有布局；布局生成后不再变化，永久缓存。
  const layoutQuery = useQuery<AnnotationsView>(computed(() => {
    const id = currentJobId.value
    const options = { ...access(id), path: { id, version: version.value } }
    return {
      queryKey: [...prefix.value, 'layout', id, version.value], enabled: import.meta.client && revision.value?.state === 'Ready',
      queryFn: async ({ signal }: { signal: AbortSignal }) => (await sdk.getRevisionLayout({ ...options, signal })).data, staleTime: Infinity,
    }
  }))
// 多页导出（把每页标注合并成一份 PDF）的进度查询，运行中自动刷新。
  const exportQuery = useQuery<DocumentExportView>(computed(() => {
    const options = { ...access(scope.value.id), path: { id: scope.value.id, version: documentJob.value?.currentRevision ?? 0 } }
    return {
      queryKey: [...prefix.value, 'export', options.path.id, options.path.version], enabled: import.meta.client && multiPage.value && options.path.version > 0,
      queryFn: async ({ signal }: { signal: AbortSignal }) => (await sdk.getDocumentExport({ ...options, signal })).data,
      refetchInterval: (query: { state: { data?: DocumentExportView } }) => running(query.state.data?.state) ? interval(2500) : false,
    }
  }))
// 原稿图片或 PDF 本身，用 Blob 方式下载；同一任务的原稿不会变，所以永不失效。
  const sourceQuery = useQuery<Blob>(computed(() => {
    const options = access(currentJobId.value)
    return {
      queryKey: [...prefix.value, 'source', currentJobId.value], enabled: import.meta.client && !!currentJobId.value,
      queryFn: async ({ signal }: { signal: AbortSignal }) => (await sdk.getScoreSource({ ...options, signal, parseAs: 'blob' })).data as Blob,
      staleTime: Infinity,
    }
  }))
// 上传并创建识别任务；Idempotency-Key 让网络重试不会重复建任务。
  const importMutation = useMutation({
    mutationFn: async (input: { file: File; token: string; key: string; preprocessing: 'None' | 'Adaptive'; engine: 'Audiveris' | 'Homr' }) =>
      (await sdk.importScore({ client, body: { file: input.file, preprocessing: input.preprocessing, engine: input.engine }, headers: { 'X-Score-Token': input.token, 'Idempotency-Key': input.key }, throwOnError: true })).data
  })
// 保存草稿：先按后端契约做 Zod 校验，再把相对上一版的修改提交成新修订。
  const revisionMutation = useMutation({ mutationFn: async (input: { id: string; body: RevisionRequest }) => (await sdk.createRevision({ ...access(input.id), body: zRevisionRequest.parse(input.body) })).data })
// 触发一次整份 PDF 导出任务。
  const exportMutation = useMutation({ mutationFn: async () => (await sdk.createDocumentExport(access(scope.value.id))).data })
// 对任务下命令：取消（cancel）或重试（retry）；重试可带选区，只重识别该区域。
  const commandMutation = useMutation({ mutationFn: async (input: { action: 'cancel' | 'retry'; id: string; region?: RecognitionRegion }) => {
    if (input.action === 'cancel') return (await sdk.createCancellation(access(input.id))).data
    return (await sdk.createRetry({ ...access(input.id), body: input.region ? { region: input.region } : null })).data
  } })
// 让这份乐谱的所有查询重新拉取一遍；上传、保存、取消后调用。
  async function refresh() { await cache.invalidateQueries({ queryKey: prefix.value }) }
// 下载某一版已经排版好的标注 PDF（用户点导出时用）。
  async function artifact(id: string, version: number) {
    return (await sdk.getAnnotatedPdf({ ...access(id), path: { id, version }, parseAs: 'blob' })).data as Blob
  }
// 下载乐谱对应的 MusicXML 文件，供 MusicXML 预览使用。
  async function musicXml(id: string, signal: AbortSignal) {
    return (await sdk.getScoreMusicXml({ ...access(id), signal, parseAs: 'blob' })).data as Blob
  }
  // Cancel and evict private content, including blobs, when switching documents.
  watch(scope, (_value, previous) => { if (previous) { void cache.cancelQueries({ queryKey: ['scores', previous.key] }); cache.removeQueries({ queryKey: ['scores', previous.key] }) } })
// 六类查询里任意一个出错，都汇总成一个错误，由页面统一提示。
  const queryError = computed(() => documentQuery.error.value || pageQuery.error.value || revisionQuery.error.value || layoutQuery.error.value || sourceQuery.error.value || exportQuery.error.value)
// 对外的接口清单：状态是只读数据，动作是 mutation 或普通异步函数。
  return {
    job, documentJob, multiPage, currentJobId, revision, annotations: computed(() => layoutQuery.data.value ?? null), documentExport: computed(() => exportQuery.data.value ?? null),
    sourceBlob: sourceQuery.data, queryError, importMutation, revisionMutation, exportMutation, commandMutation, refresh, artifact, musicXml
  }
}
