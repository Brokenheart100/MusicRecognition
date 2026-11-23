<script setup lang="ts">
// 顶栏右上角的"外观小开关"：一键切换中英文、一键切换深色/浅色主题。
// 语言和主题的当前值都来自 Nuxt 的全局状态，所以改完全站立即生效，并自动记住选择。
// 按钮放在 ClientOnly 里，因为服务端不知道用户浏览器偏好哪种主题，等页面加载后再显示。
const { t, locale, setLocale } = useI18n()
// colorMode 是深色模式模块，负责保存和读取用户的选择（深色/浅色/跟随系统）。
const colorMode = useColorMode()

// 当前是不是深色模式；为 true 时按钮显示太阳图标。
const dark = computed(() => colorMode.value === 'dark')
// 按钮上显示"要切换到的语言"：现在是中文就显示 English，反之显示中文。
const languageTarget = computed(() => locale.value === 'zh-CN' ? 'English' : '中文')
// 语言按钮的无障碍说明文字，读屏软件会把它念给用户听。
const languageLabel = computed(() => t('preferences.switchTo', { language: languageTarget.value }))
// 主题按钮的说明文字：现在是深色就提示"切换到浅色"，反之亦然。
const themeLabel = computed(() => dark.value ? t('preferences.themeLight') : t('preferences.themeDark'))

// 在中文和英文之间切换；setLocale 会更新全站文案并写入 cookie 记住选择。
function toggleLanguage() {
  void setLocale(locale.value === 'zh-CN' ? 'en' : 'zh-CN')
}
// 在深色和浅色之间切换，新的选择同样会被记住。
function toggleTheme() {
  colorMode.preference = dark.value ? 'light' : 'dark'
}
</script>

<template>
  <div class="app-preferences">
<!-- 这组按钮依赖浏览器里的设置，先等页面在浏览器加载完再渲染。 -->
    <ClientOnly>
<!-- 语言按钮：显示 EN 或 中，点一下切换到另一种语言。 -->
      <UButton color="neutral" variant="ghost" class="language-button" :aria-label="languageLabel" :title="languageLabel"
        @click="toggleLanguage">
        <span class="language-mark">{{ locale === 'zh-CN' ? 'EN' : '中' }}</span>
      </UButton>
<!-- 主题按钮：太阳/月亮图标，点一下切换深色和浅色。 -->
      <UButton color="neutral" variant="ghost" :icon="dark ? 'i-lucide-sun' : 'i-lucide-moon'" :aria-label="themeLabel"
        :title="themeLabel" @click="toggleTheme" />
<!-- 加载完成前先占一个同样大小的空位，避免顶栏布局跳动。 -->
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
/* 两个按钮之间只留 2px，看起来像一组。 */
  gap: 2px;
}

.language-mark {
  font-size: 11px;
  font-weight: 650;
  letter-spacing: .04em;
}

/* 占位块尺寸（82×32）与两个按钮的总宽高一致，防止加载后顶栏跳动。 */
.preferences-fallback {
  display: inline-block;
  width: 82px;
  height: 32px;
}
</style>
