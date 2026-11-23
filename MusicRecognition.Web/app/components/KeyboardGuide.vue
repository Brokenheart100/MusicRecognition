<script setup lang="ts">
const open = defineModel<boolean>({ default: false })
const { t } = useI18n()
const shortcutGroups = computed(() => [
  { title: t('guide.groupFile'), items: [
    [t('guide.itemImport'), 'Ctrl / ⌘ O'], [t('guide.itemSave'), 'Ctrl / ⌘ S'], [t('guide.itemExportPage'), 'Ctrl / ⌘ Shift E'],
    [t('guide.itemZoom'), '+ / −'], [t('guide.itemResetZoom'), '0'], [t('guide.itemPage'), '[ / ]'],
    [t('guide.itemLabels'), 'L'], [t('guide.itemInspector'), 'I'], [t('guide.itemLibrary'), 'B'],
  ] },
  { title: t('guide.groupProofread'), items: [
    [t('guide.itemNoteNav'), 'J / K'], [t('guide.itemSelectAll'), 'Ctrl / ⌘ A'], [t('guide.itemCancelSelection'), 'Esc'],
    [t('guide.itemNudge'), t('guide.nudgeKeys')], [t('guide.itemNudgeFast'), t('guide.nudgeFastKeys')],
    [t('guide.itemToggleSelected'), 'Delete / Backspace'], [t('guide.itemNextPending'), 'N'], [t('guide.itemPlaceSelected'), 'P'],
    [t('guide.itemUndo'), 'Ctrl / ⌘ Z'], [t('guide.itemRedo'), 'Ctrl Y / Ctrl / ⌘ Shift Z'],
  ] },
  { title: t('guide.groupHelp'), items: [[t('guide.itemToggleGuide'), 'Ctrl / ⌘ K'], [t('guide.itemOpenGuide'), '?']] },
])
</script>

<template>
  <UModal v-model:open="open" :title="$t('guide.title')" :description="$t('guide.description')"
    :ui="{ content: 'guide-modal' }">
    <template #body>
      <div class="guide-body">
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
        <section v-for="group in shortcutGroups" :key="group.title" class="shortcut-section"
          :aria-label="$t('guide.shortcutsAria', { title: group.title })">
          <h3>{{ group.title }}</h3>
          <div class="shortcut-grid"><template v-for="[label, keys] in group.items" :key="label"><span>{{ label
                }}</span><kbd>{{ keys }}</kbd></template></div>
        </section>
        <p class="guide-note">{{ $t('guide.noteWindowsMac') }}</p>
        <p class="guide-note">{{ $t('guide.noteNotation') }}</p><ULink href="/samples/README.md" target="_blank"
          rel="noopener" external class="sample-credit">{{ $t('guide.sampleCredit') }}
          <UIcon name="i-lucide-arrow-up-right" />
        </ULink>
      </div>
    </template>
  </UModal>
</template>
