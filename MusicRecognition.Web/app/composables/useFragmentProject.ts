import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { createClient } from '~~/shared/api/generated/client'
import * as sdk from '~~/shared/api/generated/sdk.gen'
import { zFragmentPlan } from '~~/shared/api/generated/zod.gen'

export function useFragmentProject() {
  const store = useFragmentsStore()
  const client = createClient({ baseUrl: location.origin, credentials: 'omit' })
  const cache = useQueryClient()
  const prefix = ['fragment-project', crypto.randomUUID()]
  const access = () => ({ client, path: { id: store.active!.id }, headers: { 'X-Score-Token': store.active!.token }, throwOnError: true as const })
  const query = useQuery(computed(() => ({
    queryKey: [...prefix, store.active?.id, store.selectedVersion], enabled: !!store.active,
    queryFn: async ({ signal }: { signal: AbortSignal }) => (await sdk.getFragmentProject({ ...access(), query: { version: store.selectedVersion || undefined }, signal })).data,
    refetchInterval: (q: { state: { data?: { state: string } } }) => ['Queued','Processing','Cancelling'].includes(q.state.data?.state ?? '') ? 1500 : false,
  })))
  watch(query.data, value => { if (value) store.restore(value) }, { immediate: true })
  const create = useMutation({ mutationFn: async (input: { token: string; key: string }) => (await sdk.importFragments({ client,
    body: { files: store.files, plan: JSON.stringify(zFragmentPlan.parse(store.draft)) }, headers: { 'X-Score-Token': input.token, 'Idempotency-Key': input.key }, throwOnError: true })).data,
    onSuccess: (view, input) => store.remember(view,input.token),
  })
  const revise = useMutation({ mutationFn: async () => (await sdk.createFragmentRevision({ ...access(), body: { expectedRevision: store.version, plan: zFragmentPlan.parse(store.draft) } })).data,
    onSuccess: async () => { store.selectedVersion=0; await cache.invalidateQueries({ queryKey: prefix }) },
  })
  const command = useMutation({ mutationFn: async (action: 'retry' | 'cancel') => (await (action==='retry'?sdk.retryFragmentProject:sdk.cancelFragmentProject)(access())).data,
    onSuccess: async () => { await cache.invalidateQueries({ queryKey: prefix }) },
  })
  async function artifact(name: string, signal?: AbortSignal) {
    return (await sdk.getFragmentArtifact({ ...access(), path: { id: store.active!.id, name }, query: { version: store.version }, parseAs: 'blob', signal })).data as Blob
  }
  async function image(index: number) { return (await sdk.getFragmentImage({ ...access(), path: { id: store.active!.id, index }, parseAs: 'blob' })).data as Blob }
  onBeforeUnmount(() => { void cache.cancelQueries({queryKey:prefix}); cache.removeQueries({queryKey:prefix}) })
  return { query, create, revise, command, artifact, image }
}
