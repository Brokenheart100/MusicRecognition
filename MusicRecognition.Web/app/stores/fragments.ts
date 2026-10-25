import { defineStore, skipHydrate } from 'pinia'
import { useStorage } from '@vueuse/core'
import { z } from 'zod'
import type { FragmentPlan, FragmentView } from '~~/shared/api/generated/types.gen'
import { zFragmentPlan } from '~~/shared/api/generated/zod.gen'

const sessionSchema = z.object({ id: z.uuid(), token: z.string().min(32), title: z.string() })
export type FragmentSession = z.infer<typeof sessionSchema>
const copy = <T>(value: T): T => structuredClone(toRaw(value))
const empty = (): FragmentPlan => ({ title: '我的片段拼谱', fragments: [], reviewConfirmed: false })

export const useFragmentsStore = defineStore('fragments', () => {
  const recent = skipHydrate(useStorage<FragmentSession[]>('pujian:fragments:v1', [], undefined, {
    initOnMounted: true, writeDefaults: false,
    serializer: { read: raw => { try { return z.array(sessionSchema).parse(JSON.parse(raw)).slice(0,30) } catch { return [] } }, write: JSON.stringify },
  }))
  const savedDrafts = skipHydrate(useStorage<Record<string, { version: number; plan: FragmentPlan }>>('pujian:fragment-drafts:v1', {}, undefined, { initOnMounted: true, writeDefaults: false }))
  const active = ref<FragmentSession | null>(null)
  const files = shallowRef<File[]>([])
  const draft = ref<FragmentPlan>(empty())
  const baseline = ref('')
  const version = ref(0)
  const latest = ref(0)
  const selectedVersion = ref(0)
  const dirty = computed(() => JSON.stringify(draft.value) !== baseline.value)
  const historical = computed(() => selectedVersion.value > 0 && selectedVersion.value < latest.value)
  let restored = '', restoring = false
  watch(draft, value => {
    if (active.value && version.value && !restoring && !historical.value) savedDrafts.value[active.value.id] = { version: version.value, plan: copy(value) }
  }, { deep: true, flush: 'sync' })
  function restore(view: FragmentView) {
    const key = `${view.id}:${view.version}`
    latest.value = Math.max(latest.value, view.version)
    if (restored === key) return
    restored = key; restoring = true; version.value = view.version
    baseline.value = JSON.stringify(view.plan)
    const stored = savedDrafts.value[view.id]
    const parsed=zFragmentPlan.safeParse(stored?.plan)
    draft.value = copy(!historical.value && stored?.version === view.version && parsed.success ? parsed.data : view.plan)
    restoring = false
  }
  function start() { restoring = true; active.value = null; files.value = []; draft.value = empty(); baseline.value = ''; selectedVersion.value=0; version.value=0; latest.value=0; restored=''; restoring=false }
  function open(session: FragmentSession) { if (active.value?.id===session.id && selectedVersion.value===0) return; start(); active.value = session }
  function remember(view: FragmentView, token: string) {
    const session = { id: view.id, token, title: view.plan.title }
    recent.value = [session, ...recent.value.filter(s => s.id !== view.id)].slice(0,30)
    open(session); restore(view)
  }
  function add(values: File[]) {
    if (active.value) return
    const first = files.value.length
    files.value = [...files.value, ...values]
    draft.value.fragments.push(...values.map((_, i) => ({ sourceIndex: first+i, crop: null, startMeasure: 1, endMeasure: null })))
  }
  function move(from: number, to: number) {
    if (to<0 || to>=draft.value.fragments.length || historical.value) return
    const [item] = draft.value.fragments.splice(from,1)
    if (item) draft.value.fragments.splice(to,0,item)
    draft.value.reviewConfirmed=false
  }
  function remove(index: number) {
    const [removed]=draft.value.fragments.splice(index,1)
    if (!active.value && removed) {
      files.value=files.value.filter((_,i)=>i!==removed.sourceIndex)
      draft.value.fragments.forEach(f=>{if(f.sourceIndex>removed.sourceIndex)f.sourceIndex--})
    }
    draft.value.reviewConfirmed=false
  }
  return { recent, active, files, draft, version, latest, selectedVersion, dirty, historical, restore, start, open, remember, add, move, remove }
})
