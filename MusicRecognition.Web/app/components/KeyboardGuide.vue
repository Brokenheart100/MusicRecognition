<script setup lang="ts">
// 键盘快捷键指南弹窗：列出所有可用快捷键，以及三步使用流程。
// 快捷键按"文件、校对、帮助"三组整理，方便用户按使用场景查找。
// 弹窗是否打开由父页面通过 v-model 控制，组件自己只负责显示内容。
// open 与父页面双向绑定；父页面把它设为 true 时弹窗打开。
const open = defineModel<boolean>({ default: false })
const { t } = useI18n()
// 三组快捷键：文件与视图、逐音校对、帮助。所有文案都从翻译文件里取。
const shortcutGroups = computed(() => [
// 第 1 组：导入、保存、导出、缩放、翻页、显隐标注等文件与视图操作。
  { title: t('guide.groupFile'), items: [
    [t('guide.itemImport'), 'Ctrl / ⌘ O'], [t('guide.itemSave'), 'Ctrl / ⌘ S'], [t('guide.itemExportPage'), 'Ctrl / ⌘ Shift E'],
    [t('guide.itemZoom'), '+ / −'], [t('guide.itemResetZoom'), '0'], [t('guide.itemPage'), '[ / ]'],
    [t('guide.itemLabels'), 'L'], [t('guide.itemInspector'), 'I'], [t('guide.itemLibrary'), 'B'],
  ] },
// 第 2 组：逐个音校对时用到的选择、微调、删除、撤销等操作。
  { title: t('guide.groupProofread'), items: [
    [t('guide.itemNoteNav'), 'J / K'], [t('guide.itemSelectAll'), 'Ctrl / ⌘ A'], [t('guide.itemCancelSelection'), 'Esc'],
    [t('guide.itemNudge'), t('guide.nudgeKeys')], [t('guide.itemNudgeFast'), t('guide.nudgeFastKeys')],
    [t('guide.itemToggleSelected'), 'Delete / Backspace'], [t('guide.itemNextPending'), 'N'], [t('guide.itemPlaceSelected'), 'P'],
    [t('guide.itemUndo'), 'Ctrl / ⌘ Z'], [t('guide.itemRedo'), 'Ctrl Y / Ctrl / ⌘ Shift Z'],
  ] },
// 第 3 组：打开/关闭这个指南弹窗的快捷键。
  { title: t('guide.groupHelp'), items: [[t('guide.itemToggleGuide'), 'Ctrl / ⌘ K'], [t('guide.itemOpenGuide'), '?']] },
])
</script>

<template>
<!-- 指南弹窗本体：标题、说明和快捷键表都来自上面整理好的分组数据。 -->
  <UModal v-model:open="open" :title="$t('guide.title')" :description="$t('guide.description')"
    :ui="{ content: 'guide-modal' }">
<!-- 内容分两块：上面是三步入门的说明，下面是按组排列的快捷键表。 -->
    <template #body>
      <div class="guide-body">
<!-- 三个步骤依次是：导入乐谱、识别音符、逐页校对并导出。 -->
        <ol>
          <li><strong>{{ $t('guide.step1Title') }}</strong>
            <p>{{ $t('guide.step1Body') }}</p>
          </li>
          <li><strong>{{ $t('guide.step2Title') }}</strong>
            <p>{{ $t('guide.step2Body') }}</p>
          </li>
          <li><strong>{{ $t('guide.step3Title') }}</strong>
            <p>{{ $t('guide.step3Body') }}</p>
          </li>
        </ol>
<!-- 快捷键表：按组循环，每组标题下面是一个两列的网格。 -->
        <section v-for="group in shortcutGroups" :key="group.title" class="shortcut-section"
          :aria-label="$t('guide.shortcutsAria', { title: group.title })">
          <h3>{{ group.title }}</h3>
          <div class="shortcut-grid"><template v-for="[label, keys] in group.items" :key="label"><span>{{ label
                }}</span><kbd>{{ keys }}</kbd></template></div>
        </section>
<!-- 两条补充说明：Windows 与 Mac 的按键叫法不同；简谱数字代表什么。 -->
        <p class="guide-note">{{ $t('guide.noteWindowsMac') }}</p>
        <p class="guide-note">{{ $t('guide.noteNotation') }}</p><ULink href="/samples/README.md" target="_blank"
          rel="noopener" external class="sample-credit">{{ $t('guide.sampleCredit') }}
          <UIcon name="i-lucide-arrow-up-right" />
        </ULink>
      </div>
    </template>
  </UModal>
</template>
