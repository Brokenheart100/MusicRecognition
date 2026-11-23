<script setup lang="ts">
import { en, zh_cn } from '@nuxt/ui/locale'

const { t, locale } = useI18n()
const uiLocale = computed(() => locale.value === 'zh-CN' ? zh_cn : en)

useHead(() => ({
  title: t('meta.title'),
  htmlAttrs: { lang: locale.value },
  meta: [{ name: 'description', content: t('meta.description') }],
}))
</script>

<template>
  <UApp :locale="uiLocale">
    <NuxtPage />
    <ConfirmDialog />
  </UApp>
</template>
