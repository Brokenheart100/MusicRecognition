<script setup lang="ts">
const emit = defineEmits<{ toggleCollapse: []; openSidebar: []; openHelp: [] }>()
const store = useWorkspaceStore()
const { t } = useI18n()
type BreadcrumbItem = { label: string; to?: string; active?: boolean; onClick?: () => void }
const breadcrumbItems = computed<BreadcrumbItem[]>(() => {
  const items: BreadcrumbItem[] = [{ label: t('shell.workspace'), to: '/', onClick: () => store.home() }]
  if (store.active) items.push({ label: store.active.name.replace(/\.[^.]+$/, ''), active: true })
  return items
})
</script>

<template>
  <header class="topbar">
    <div class="topbar-left">
      <UButton icon="i-lucide-panel-left" class="desktop-collapse" color="neutral" variant="ghost"
        :aria-label="$t('shell.collapseSidebar')" @click="emit('toggleCollapse')" />
      <UButton icon="i-lucide-panel-left" class="mobile-sidebar" color="neutral" variant="ghost"
        :aria-label="$t('shell.openLibrary')" @click="emit('openSidebar')" />
      <UBreadcrumb class="breadcrumb" :items="breadcrumbItems" separator-icon="i-lucide-chevron-right" color="neutral"
        :ui="{ list: 'gap-[13px]', link: 'text-[12px]', separatorIcon: 'size-3 text-[#bdc1cf] dark:text-[#6b7388]' }" />
    </div>
    <div class="topbar-actions"><span class="workspace-tag"><span class="status-dot" /> {{ $t('shell.tagline')
        }}</span>
      <AppPreferences />
      <UTooltip :text="$t('shell.guideTooltip')">
        <UButton icon="i-lucide-book-open" color="neutral" variant="ghost" :aria-label="$t('shell.openGuide')"
          @click="emit('openHelp')" />
      </UTooltip><UAvatar :text="$t('app.avatar')" size="md" class="avatar" role="img" :ui="{ fallback: 'text-inherit' }"
        :aria-label="$t('shell.avatarAria')" />
    </div>
  </header>
</template>
