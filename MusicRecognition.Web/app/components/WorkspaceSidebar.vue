<script setup lang="ts">
import type { RecentScore } from '~~/shared/types/score'

const emit = defineEmits<{
  requestImport: []
  select: [item: RecentScore]
  help: []
  close: []
}>()
const store = useWorkspaceStore()
const search = ref('')
const recent = computed(() => store.recent.filter(item => item.name.toLowerCase().includes(search.value.toLowerCase())))
</script>

<template>
  <aside class="library-sidebar glass" :aria-label="$t('shell.libraryAria')">
    <button class="brand" :aria-label="$t('shell.brandAria')" @click="store.home(); emit('close')"><img src="/favicon.svg" alt=""
        width="38" height="38"><span>{{ $t('app.name') }}<small>{{ $t('app.latin') }}</small></span></button>
    <UButton icon="i-lucide-plus" :label="$t('shell.import')" size="lg" class="new-score" :loading="store.uploading"
      aria-keyshortcuts="Control+O Meta+O" :title="$t('shell.importTitle')" @click="emit('requestImport')" />
    <UButton color="neutral" variant="ghost" class="sidebar-home" :class="{ active: !store.active }"
      :aria-current="!store.active ? 'page' : undefined"
      :ui="{ base: 'w-full justify-start font-normal hover:bg-transparent' }"
      @click="store.home(); emit('close')">
      <UIcon name="i-lucide-layout-dashboard" /><span>{{ $t('shell.workspace') }}</span>
      <UIcon name="i-lucide-chevron-right" class="tiny" />
    </UButton>
    <div class="library-heading"><span>{{ $t('shell.myScores') }}</span><span class="library-count">{{
      store.recent.length.toString().padStart(2, '0') }}</span></div>
    <UInput v-if="store.recent.length > 4" v-model="search" icon="i-lucide-search"
      :placeholder="$t('shell.searchPlaceholder')" :aria-label="$t('shell.searchAria')" class="library-search" />
    <div class="library-list">
      <div v-if="!store.recent.length" class="library-empty">
        <UIcon name="i-lucide-music-2" />
        <p>{{ $t('shell.emptyTitle') }}</p><span>{{ $t('shell.emptyHint') }}</span>
      </div>
      <div v-for="item in recent" :key="item.id" class="library-item"
        :class="{ active: store.active?.id === item.id }">
        <UButton color="neutral" variant="ghost" class="library-open"
          :aria-label="$t('shell.openScore', { name: item.name })"
          :ui="{ base: 'w-full justify-start font-normal hover:bg-transparent' }" @click="emit('select', item)"><span
            class="file-mini">
            <UIcon name="i-lucide-music-2" />
          </span><span class="file-text"><strong>{{ item.name.replace(/\.[^.]+$/, '') }}</strong><small>{{
              $t('shell.scoreSubtitle') }}</small></span></UButton>
        <UButton icon="i-lucide-x" class="forget-score" size="xs" color="neutral" variant="ghost"
          :aria-label="$t('shell.removeScore', { name: item.name })" @click="store.forget(item.id)" />
      </div>
    </div>
    <div class="sidebar-bottom">
      <div class="quiet-note"><span class="quiet-lines">♩</span>
        <p>{{ $t('shell.quietNote') }}<small>{{ $t('shell.quietHint') }}</small></p>
      </div><UButton color="neutral" variant="ghost" class="help-link"
        :ui="{ base: 'w-full justify-start font-normal hover:bg-transparent' }" @click="emit('help')">
        <UIcon name="i-lucide-circle-help" /><span>{{ $t('shell.helpLink') }}</span><span>Ctrl / ⌘ K</span>
      </UButton>
      <p class="local-note">
        <UIcon name="i-lucide-lock-keyhole" /> {{ $t('shell.localNote') }}
      </p>
    </div>
  </aside>
</template>
