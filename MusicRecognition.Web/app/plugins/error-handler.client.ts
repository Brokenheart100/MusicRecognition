export default defineNuxtPlugin((nuxtApp) => {
  const previous = nuxtApp.vueApp.config.errorHandler
  nuxtApp.vueApp.config.errorHandler = (error, instance, info) => {
    console.error(`[vue:${info}]`, error)
    previous?.(error, instance, info)
  }
  window.addEventListener('unhandledrejection', event => console.error('[unhandledrejection]', event.reason))
})
