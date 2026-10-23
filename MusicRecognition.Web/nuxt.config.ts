export default defineNuxtConfig({
  compatibilityDate: '2026-10-22',
  modules: ['@nuxt/ui', '@pinia/nuxt', '@vueuse/nuxt', '@nuxt/eslint'],
  css: ['~/assets/css/main.css'],
  ui: { fonts: false },
  colorMode: { preference: 'light', fallback: 'light' },
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
