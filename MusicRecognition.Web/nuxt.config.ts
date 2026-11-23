// Nuxt 应用的全局配置文件：模块、样式、语言、深色模式、页面标题等都在这里。
// 可以理解成"整个前端项目的总设置"，改这里会影响所有页面。
// 下面按功能分组，逐项说明每个配置是做什么的。
export default defineNuxtConfig({
// 兼容日期：锁定 Nuxt 的默认行为版本，升级框架时避免行为突然变化。
  compatibilityDate: '2026-10-22',
// 启用的模块：UI 组件库、状态管理、工具函数、代码检查、深色模式、多语言。
  modules: ['@nuxt/ui', '@pinia/nuxt', '@vueuse/nuxt', '@nuxt/eslint', '@nuxtjs/color-mode', '@nuxtjs/i18n'],
// 全局样式入口：main.css 放变量和基础样式，dark.css 放深色主题。
  css: ['~/assets/css/main.css', '~/assets/css/dark.css'],
// 关闭组件库自带的字体下载，少请求一份字体文件。
  ui: { fonts: false },
// 深色模式：默认跟随系统，读不到系统设置时用浅色；用户的选择存在浏览器里。
  colorMode: {
    preference: 'system',
    fallback: 'light',
// 浏览器本地存储里保存用户选择的键名，pujian 是项目英文名。
    storageKey: 'pujian-color-mode',
  },
// 多语言（i18n）设置：目前提供简体中文和英文两种界面。
  i18n: {
// no_prefix 表示网址里不加 /zh、/en 前缀，语言靠 cookie 记住。
    strategy: 'no_prefix',
// 用户第一次访问、又没有语言记录时，默认用简体中文。
    defaultLocale: 'zh-CN',
// 语言清单：code 是程序内部标识，file 是对应的翻译文案文件。
    locales: [
      { code: 'zh-CN', language: 'zh-CN', name: '中文', file: 'zh-CN.json' },
      { code: 'en', language: 'en-US', name: 'English', file: 'en.json' },
    ],
// 自动识别浏览器语言：第一次访问时按浏览器设置选语言，并写进 cookie。
    detectBrowserLanguage: {
// useCookie 表示把选择存进 cookie，cookieKey 是它的名字。
      useCookie: true,
      cookieKey: 'pujian-locale',
// 只在进入根路径时按语言跳转一次，刷新页面不会反复跳。
      redirectOn: 'root',
// 浏览器语言匹配不到时，回落到简体中文。
      fallbackLocale: 'zh-CN',
    },
  },
// 关闭 Nuxt 开发者工具面板，正式界面用不到。
  devtools: { enabled: false },
// 运行时配置：apiBase 留空，实际 API 地址由部署环境通过环境变量注入。
  runtimeConfig: { apiBase: '' },
// 页面默认 head：标题、语言、描述、图标等标签都从这里统一设置。
  app: {
    head: {
// 浏览器标签页上显示的标题。
      title: '谱间 · 让读谱，更从容',
      htmlAttrs: { lang: 'zh-CN' },
      meta: [{ name: 'description', content: '在原钢琴五线谱的留白处标注简谱，轻松校对，从容练习。' }],
      link: [{ rel: 'icon', type: 'image/svg+xml', href: '/favicon.svg' }],
    },
  },
// 图标模块：只把 lucide 图标库打包在服务端，减少浏览器端的下载量。
  icon: { serverBundle: { collections: ['lucide'] } },
// 部署方式：以 Node.js 服务器运行，和 Aspire 里的托管方式一致。
  nitro: { preset: 'node-server' },
})
