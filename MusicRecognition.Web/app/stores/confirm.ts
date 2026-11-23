import { defineStore } from 'pinia'

// 全站"确认弹窗"的状态仓库（Pinia）：任何页面都能问用户一句"确定吗？"。
// 调用 ask() 会弹出确认框并返回一个 Promise；点确认得到 true，点取消或关闭得到 false。
// 这样业务代码可以写成"如果用户确认，就继续执行"，不用自己管理弹窗开关。
// 一次确认请求的内容：标题、说明、确认按钮文字，以及"用户答复后要调用的回调"。
type ConfirmRequest = { title: string; description: string; confirmLabel: string; resolve: (value: boolean) => void }

export const useConfirmStore = defineStore('confirm', () => {
// request 保存当前这次询问；open 控制弹窗是否显示。
  const request = ref<ConfirmRequest | null>(null)
  const open = ref(false)
// settled 防止用户连点或同时收到多个关闭事件，导致同一个 Promise 被回答两次。
  let settled = false

// 弹出确认框；调用方会 await 这个 Promise，拿到 true/false 后再决定下一步。
  function ask(options: Omit<ConfirmRequest, 'resolve'>): Promise<boolean> {
// 如果上一次确认还开着，先按"取消"结束它，避免旧请求永远没人回答。
    request.value?.resolve(false)
    settled = false
    return new Promise((resolve) => {
// 把 resolve 存进请求里，等用户点按钮时再调用，Promise 就得到结果。
      request.value = { ...options, resolve }
      open.value = true
    })
  }

// 用户作答（点确认、取消、关闭或点遮罩）都走这里；已经答过就直接忽略。
  function answer(value: boolean) {
    if (!request.value || settled) return
    settled = true
    open.value = false
// 先关弹窗，再把结果交给等待中的调用方。
    request.value.resolve(value)
  }

// 弹窗完全收起后清空请求，下次打开是全新的状态。
  function clear() {
    request.value = null
    settled = false
  }

  return { request, open, ask, answer, clear }
})
