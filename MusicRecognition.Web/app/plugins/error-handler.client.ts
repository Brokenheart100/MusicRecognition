// 浏览器端的"兜底医生"：页面里任何地方报错，都会先经过这里。
// 作用是把错误统一打印到浏览器控制台，方便排查；它不吞错误，也不弹窗打扰用户。
// 文件名的 .client 表示只在浏览器运行，服务端渲染阶段不会执行。
export default defineNuxtPlugin((nuxtApp) => {
// 先保存 Vue 原有的错误处理函数，稍后还要接着调用，避免影响框架自身逻辑。
  const previous = nuxtApp.vueApp.config.errorHandler
// 接管组件渲染和事件里的错误：先打日志，再交回原来的处理函数。
  nuxtApp.vueApp.config.errorHandler = (error, instance, info) => {
    console.error(`[vue:${info}]`, error)
    previous?.(error, instance, info)
  }
// 除了 Vue 内部的错误，也监听没人处理的 Promise 异常，两类错误都输出到控制台。
  window.addEventListener('unhandledrejection', event => console.error('[unhandledrejection]', event.reason))
})
