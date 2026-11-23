<script setup lang="ts">
import type { RecentScore } from '~~/shared/types/score'
// 左侧"乐谱库"侧栏：列出最近打开过的乐谱、导入新乐谱、回到工作台、打开帮助。
// 宽屏时它固定显示在左边，窄屏时作为抽屉弹出，所以"选中后收起自己"由父页面驱动。
// 数据来自工作台状态仓库（本机保存的最近乐谱列表），这里只负责展示和转发点击。

// 四类事件：请求导入、选中某份乐谱、打开帮助、关闭侧栏（手机端用）。
const emit = defineEmits<{
  requestImport: []
  select: [item: RecentScore]
  help: []
  close: []
}>()
// 工作台状态仓库：最近乐谱列表、当前打开的乐谱、上传状态都在这里。
const store = useWorkspaceStore()
// 搜索框内容，用来在最近乐谱里按名字过滤。
const search = ref('')
// 过滤后的列表；搜索词为空时就是全部最近乐谱，按名字忽略大小写匹配。
const recent = computed(() => store.recent.filter(item => item.name.toLowerCase().includes(search.value.toLowerCase())))
</script>

<template>
<!-- 侧栏本体：半透明毛玻璃背景，读屏软件会念出"乐谱库"。 -->
  <aside class="library-sidebar glass" :aria-label="$t('shell.libraryAria')">
<!-- 顶部品牌按钮：点它回到工作台首页并收起侧栏。 -->
    <button class="brand" :aria-label="$t('shell.brandAria')" @click="store.home(); emit('close')"><img src="/favicon.svg" alt=""
        width="38" height="38"><span>{{ $t('app.name') }}<small>{{ $t('app.latin') }}</small></span></button>
<!-- 导入按钮：点击后由父页面弹出系统文件选择框，上传期间显示转圈。 -->
    <UButton icon="i-lucide-plus" :label="$t('shell.import')" size="lg" class="new-score" :loading="store.uploading"
      aria-keyshortcuts="Control+O Meta+O" :title="$t('shell.importTitle')" @click="emit('requestImport')" />
<!-- "工作台"入口：当前没有打开乐谱时高亮，表示正停在工作台。 -->
    <UButton color="neutral" variant="ghost" class="sidebar-home" :class="{ active: !store.active }"
      :aria-current="!store.active ? 'page' : undefined"
      :ui="{ base: 'w-full justify-start font-normal hover:bg-transparent' }"
      @click="store.home(); emit('close')">
      <UIcon name="i-lucide-layout-dashboard" /><span>{{ $t('shell.workspace') }}</span>
      <UIcon name="i-lucide-chevron-right" class="tiny" />
    </UButton>
<!-- "我的乐谱"标题：右侧数字是列表数量，不足两位前面补 0，例如 03。 -->
    <div class="library-heading"><span>{{ $t('shell.myScores') }}</span><span class="library-count">{{
      store.recent.length.toString().padStart(2, '0') }}</span></div>
<!-- 乐谱超过 4 份才显示搜索框，列表很短时不必多一个输入框。 -->
    <UInput v-if="store.recent.length > 4" v-model="search" icon="i-lucide-search"
      :placeholder="$t('shell.searchPlaceholder')" :aria-label="$t('shell.searchAria')" class="library-search" />
<!-- 最近乐谱列表；一条记录都没有时显示空状态提示。 -->
    <div class="library-list">
<!-- 空状态：音符图标 + 一行标题 + 一行提示。 -->
      <div v-if="!store.recent.length" class="library-empty">
        <UIcon name="i-lucide-music-2" />
        <p>{{ $t('shell.emptyTitle') }}</p><span>{{ $t('shell.emptyHint') }}</span>
      </div>
<!-- 每条记录：点名字打开乐谱，点右侧 X 把它从最近列表里移除。 -->
      <div v-for="item in recent" :key="item.id" class="library-item"
        :class="{ active: store.active?.id === item.id }">
<!-- 打开按钮：整行都可点击，当前打开的乐谱会高亮。 -->
        <UButton color="neutral" variant="ghost" class="library-open"
          :aria-label="$t('shell.openScore', { name: item.name })"
          :ui="{ base: 'w-full justify-start font-normal hover:bg-transparent' }" @click="emit('select', item)"><span
            class="file-mini">
            <UIcon name="i-lucide-music-2" />
          </span><span class="file-text"><strong>{{ item.name.replace(/\.[^.]+$/, '') }}</strong><small>{{
              $t('shell.scoreSubtitle') }}</small></span></UButton>
<!-- 移除按钮：把这枚乐谱从"最近打开"里去掉，列表立刻变短。 -->
        <UButton icon="i-lucide-x" class="forget-score" size="xs" color="neutral" variant="ghost"
          :aria-label="$t('shell.removeScore', { name: item.name })" @click="store.forget(item.id)" />
      </div>
    </div>
<!-- 侧栏底部：一句安静的话、帮助入口，以及本地存储说明。 -->
    <div class="sidebar-bottom">
<!-- 装饰性的音符和一行小字，负责侧栏底部的氛围，不承载功能。 -->
      <div class="quiet-note"><span class="quiet-lines">♩</span>
        <p>{{ $t('shell.quietNote') }}<small>{{ $t('shell.quietHint') }}</small></p>
      </div><UButton color="neutral" variant="ghost" class="help-link"
        :ui="{ base: 'w-full justify-start font-normal hover:bg-transparent' }" @click="emit('help')">
        <UIcon name="i-lucide-circle-help" /><span>{{ $t('shell.helpLink') }}</span><span>Ctrl / ⌘ K</span>
      </UButton>
<!-- 隐私说明：锁形图标配一行字，说明乐谱文件保存在本机。 -->
      <p class="local-note">
        <UIcon name="i-lucide-lock-keyhole" /> {{ $t('shell.localNote') }}
      </p>
    </div>
  </aside>
</template>
