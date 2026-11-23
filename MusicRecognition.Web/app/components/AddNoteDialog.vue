<script setup lang="ts">
// 手工补录漏掉的音符：选择音名、八度和升降号，确认后由父组件立即保存为新修订。
// 位置、小节和谱表由画布点击决定，这里只负责音高。
import type { Pitch, Step } from '~~/shared/types/score'

const open = defineModel<boolean>('open', { required: true })
const props = defineProps<{ initialPitch?: Pitch | null; position?: string; loading?: boolean }>()
const emit = defineEmits<{ confirm: [pitch: Pitch] }>()
const { t } = useI18n()
// 默认值来自点击处最近的音符；没有最近音符时用 C4。
const step = ref<Step>('C')
const alter = ref(0)
const octave = ref(4)
watch(open, value => {
  if (!value) return
  step.value = props.initialPitch?.step ?? 'C'
  alter.value = props.initialPitch?.alter ?? 0
  octave.value = props.initialPitch?.octave ?? 4
})
const steps: Step[] = ['C', 'D', 'E', 'F', 'G', 'A', 'B']
const octaves = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]
// 升降号选项复用检查器里的符号文案，保证两处一致。
const alterations = computed(() => [
  { label: t('inspector.alterDoubleFlat'), value: -2 },
  { label: t('inspector.alterFlat'), value: -1 },
  { label: t('inspector.alterNatural'), value: 0 },
  { label: t('inspector.alterSharp'), value: 1 },
  { label: t('inspector.alterDoubleSharp'), value: 2 },
])
const pitch = computed<Pitch>(() => ({ step: step.value, alter: alter.value, octave: octave.value }))
</script>

<template>
  <UModal v-model:open="open" :title="$t('score.addNoteTitle')" :description="$t('score.addNoteDescription')"
    :ui="{ footer: 'justify-end' }">
    <template #body>
<!-- 显示将写入的小节和谱表，让用户确认位置来自画布点击 -->
      <p v-if="position" class="field-help mb-4">{{ position }}</p>
      <div class="pitch-fields">
        <UFormField :label="$t('inspector.step')" size="xs">
          <USelect v-model="step" :items="steps" class="w-full" />
        </UFormField>
        <UFormField :label="$t('inspector.octave')" size="xs">
          <USelect v-model="octave" :items="octaves" class="w-full" />
        </UFormField>
      </div>
      <UFormField :label="$t('inspector.accidental')" size="xs">
        <USelect v-model="alter" :items="alterations" class="w-full" />
      </UFormField>
    </template>
    <template #footer>
      <UButton color="neutral" variant="ghost" :label="$t('common.cancel')" :disabled="loading"
        @click="open = false" />
      <UButton icon="i-lucide-plus" :label="$t('score.addNoteConfirm')" :loading="loading"
        @click="emit('confirm', pitch)" />
    </template>
  </UModal>
</template>
