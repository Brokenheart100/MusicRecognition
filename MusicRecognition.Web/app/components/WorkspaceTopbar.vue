<script setup lang="ts">
// 工作台顶部的横条：左边是回到首页/当前乐谱名的面包屑（breadcrumb），右边是设置和帮助按钮。
// 它自己不保存数据，只把按钮点击变成事件（event）交给父页面处理，例如折叠侧栏。
// 桌面端和手机端共用同一份模板，靠 CSS 决定显示哪个按钮。
const emit = defineEmits<{ toggleCollapse: []; openSidebar: []; openHelp: [] }>()
const store = useWorkspaceStore()
const { t } = useI18n()
// 面包屑的一项：显示文字、点击后跳到哪个地址、是否高亮为当前页。
type BreadcrumbItem = { label: string; to?: string; active?: boolean; onClick?: () => void }
// 面包屑内容随当前打开的乐谱变化，所以用 computed 自动重新计算。
const breadcrumbItems = computed<BreadcrumbItem[]>(() => {
// 第一项固定是"工作台"，点它会回到乐谱列表。
  const items: BreadcrumbItem[] = [{ label: t('shell.workspace'), to: '/', onClick: () => store.home() }]
// 打开了乐谱时追加第二项：去掉 .pdf 之类后缀的文件名，当前项高亮且不可点。
  if (store.active) items.push({ label: store.active.name.replace(/\.[^.]+$/, ''), active: true })
  return items
})
</script>

<template>
<!-- 顶栏分左右两栏：左边导航和折叠按钮，右边功能按钮。 -->
  <header class="topbar">
    <div class="topbar-left">
<!-- 这两个按钮二选一显示：宽屏显示"折叠侧栏"，手机显示"打开乐谱库"。 -->
      <UButton icon="i-lucide-panel-left" class="desktop-collapse" color="neutral" variant="ghost"
        :aria-label="$t('shell.collapseSidebar')" @click="emit('toggleCollapse')" />
      <UButton icon="i-lucide-panel-left" class="mobile-sidebar" color="neutral" variant="ghost"
        :aria-label="$t('shell.openLibrary')" @click="emit('openSidebar')" />
<!-- 面包屑的字号、间距和分隔符颜色在这里微调，避免抢走乐谱内容的视线。 -->
      <UBreadcrumb class="breadcrumb" :items="breadcrumbItems" separator-icon="i-lucide-chevron-right" color="neutral"
        :ui="{ list: 'gap-[13px]', link: 'text-[12px]', separatorIcon: 'size-3 text-[#bdc1cf] dark:text-[#6b7388]' }" />
    </div>
<!-- 右侧依次是状态标签、外观偏好设置、使用指南和用户头像。 -->
    <div class="topbar-actions"><span class="workspace-tag"><span class="status-dot" /> {{ $t('shell.tagline')
        }}</span>
      <AppPreferences />
<!-- 指南按钮外包一层悬浮提示（tooltip），鼠标停上去能看到说明。 -->
      <UTooltip :text="$t('shell.guideTooltip')">
        <UButton icon="i-lucide-book-open" color="neutral" variant="ghost" :aria-label="$t('shell.openGuide')"
          @click="emit('openHelp')" />
      </UTooltip><UAvatar :text="$t('app.avatar')" size="md" class="avatar" role="img" :ui="{ fallback: 'text-inherit' }"
        :aria-label="$t('shell.avatarAria')" />
    </div>
  </header>
</template>
