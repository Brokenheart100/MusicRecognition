import { defineStore } from 'pinia'

type ConfirmRequest = { title: string; description: string; confirmLabel: string; resolve: (value: boolean) => void }

export const useConfirmStore = defineStore('confirm', () => {
  const request = ref<ConfirmRequest | null>(null)
  const open = ref(false)
  let settled = false

  function ask(options: Omit<ConfirmRequest, 'resolve'>): Promise<boolean> {
    request.value?.resolve(false)
    settled = false
    return new Promise((resolve) => {
      request.value = { ...options, resolve }
      open.value = true
    })
  }

  function answer(value: boolean) {
    if (!request.value || settled) return
    settled = true
    open.value = false
    request.value.resolve(value)
  }

  function clear() {
    request.value = null
    settled = false
  }

  return { request, open, ask, answer, clear }
})
