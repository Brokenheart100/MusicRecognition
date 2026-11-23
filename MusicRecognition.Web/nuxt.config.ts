export default defineNuxtConfig({
  compatibilityDate: '2026-10-22',
  modules: ['@nuxt/ui', '@pinia/nuxt', '@vueuse/nuxt', '@nuxt/eslint', '@nuxtjs/color-mode', '@nuxtjs/i18n'],
  css: ['~/assets/css/main.css', '~/assets/css/dark.css'],
  ui: { fonts: false },
  colorMode: {
    preference: 'system',
    fallback: 'light',
    storageKey: 'pujian-color-mode',
  },
  i18n: {
    strategy: 'no_prefix',
    defaultLocale: 'zh-CN',
    locales: [
      { code: 'zh-CN', language: 'zh-CN', name: '中文', file: 'zh-CN.json' },
      { code: 'en', language: 'en-US', name: 'English', file: 'en.json' },
    ],
    detectBrowserLanguage: {
      useCookie: true,
      cookieKey: 'pujian-locale',
      redirectOn: 'root',
      fallbackLocale: 'zh-CN',
    },
  },
  devtools: { enabled: false },
  runtimeConfig: { apiBase: '' },
  app: {
    head: {
      title: '谱间 · 让读谱，更从容',
      htmlAttrs: { lang: 'zh-CN' },
      meta: [{ name: 'description', content: '在原钢琴五线谱的留白处标注简谱，轻松校对，从容练习。' }],
      link: [{ rel: 'icon', type: 'image/svg+xml', href: '/favicon.svg' }],
    },
  },
  icon: { serverBundle: { collections: ['lucide'] } },
  nitro: { preset: 'node-server' },
})
