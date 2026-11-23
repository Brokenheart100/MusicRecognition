<script setup lang="ts">
const { t, locale, setLocale } = useI18n()
const colorMode = useColorMode()

const dark = computed(() => colorMode.value === 'dark')
const languageTarget = computed(() => locale.value === 'zh-CN' ? 'English' : '中文')
const languageLabel = computed(() => t('preferences.switchTo', { language: languageTarget.value }))
const themeLabel = computed(() => dark.value ? t('preferences.themeLight') : t('preferences.themeDark'))

function toggleLanguage() {
  void setLocale(locale.value === 'zh-CN' ? 'en' : 'zh-CN')
}
function toggleTheme() {
  colorMode.preference = dark.value ? 'light' : 'dark'
}
</script>

<template>
  <div class="app-preferences">
    <ClientOnly>
      <UButton color="neutral" variant="ghost" class="language-button" :aria-label="languageLabel" :title="languageLabel"
        @click="toggleLanguage">
        <span class="language-mark">{{ locale === 'zh-CN' ? 'EN' : '中' }}</span>
      </UButton>
      <UButton color="neutral" variant="ghost" :icon="dark ? 'i-lucide-sun' : 'i-lucide-moon'" :aria-label="themeLabel"
        :title="themeLabel" @click="toggleTheme" />
      <template #fallback>
        <span class="preferences-fallback" aria-hidden="true" />
      </template>
    </ClientOnly>
  </div>
</template>

<style scoped>
.app-preferences {
  display: inline-flex;
  align-items: center;
  gap: 2px;
}

.language-mark {
  font-size: 11px;
  font-weight: 650;
  letter-spacing: .04em;
}

.preferences-fallback {
  display: inline-block;
  width: 82px;
  height: 32px;
}
</style>
