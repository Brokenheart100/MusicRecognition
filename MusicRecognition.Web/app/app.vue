<script setup lang="ts">
import { en, zh_cn } from '@nuxt/ui/locale'
// 这是整个网站的"总外壳"：每个页面都装进它里面显示，像相框。
// 它统一设置浏览器标签页的标题、页面语言，并跟随中英文切换。
// 这里还挂着全局确认弹窗（modal），任何页面都能共用同一个。
// 页面内容由 NuxtPage 按网址自动切换，这里只负责外层框架。

// t 用来取翻译文案；locale 是当前语言代码，例如 zh-CN 或 en。
const { t, locale } = useI18n()
// 组件库（Nuxt UI）自带语言包：项目语言是中文就用 zh_cn，否则用英文 en。
const uiLocale = computed(() => locale.value === 'zh-CN' ? zh_cn : en)

// 把标题、描述和页面语言写进浏览器 head；语言一变，浏览器标题也跟着变。
useHead(() => ({
  title: t('meta.title'),
  htmlAttrs: { lang: locale.value },
  meta: [{ name: 'description', content: t('meta.description') }],
}))
</script>

<template>
<!-- UApp 提供全局主题、语言和提示层，真正的页面内容都在它里面。 -->
  <UApp :locale="uiLocale">
<!-- 路由占位符：当前网址对应哪个页面，就在这里渲染哪个页面。 -->
    <NuxtPage />
<!-- 全局确认弹窗固定挂最外层，所以切换页面时它也能随时弹出。 -->
    <ConfirmDialog />
  </UApp>
</template>
