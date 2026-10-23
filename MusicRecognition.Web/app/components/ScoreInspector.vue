<script setup lang="ts">
import type { Numbering, Pitch, Step } from '~~/shared/types/score'
const emit = defineEmits<{ place: []; pending: []; close: [] }>()
const store = useWorkspaceStore()
const steps: Step[] = ['C', 'D', 'E', 'F', 'G', 'A', 'B']
const alterations = [{ label: '重降 ♭♭', value: -2 }, { label: '降音 ♭', value: -1 }, { label: '本位 ♮', value: 0 }, { label: '升音 ♯', value: 1 }, { label: '重升 ♯♯', value: 2 }]
const modes = [{ label: '固定 C', value: 'FixedC' }, { label: '首调', value: 'MovableDo' }]
const scales = [{ label: '大调 · 主音为 1', value: 'Major' }, { label: '自然小调 · 主音为 6', value: 'NaturalMinor' }]
const fontSizes = [{ label: '自动 · 随谱线大小', value: 0 }, ...[3, 4, 5, 6, 7, 8, 10, 12, 16].map(value => ({ label: `${value} pt`, value }))]
const colors = [{ name: '黑色', value: '#000000' }, { name: '深灰', value: '#475569' }, { name: '蓝色', value: '#2563EB' }, { name: '红色', value: '#DC2626' }, { name: '绿色', value: '#15803D' }]
const colorError = ref('')
const colorInput = ref('')
// Style controls edit the marquee selection when there is one, otherwise the whole page.
const targets = computed(() => store.styleTargets)
const scope = computed(() => targets.value.length ? store.styleOf(targets.value[0]!) : store.draft.annotationStyle)
watch(scope, value => { colorInput.value = value.color; colorError.value = '' }, { immediate: true })
function fontSize(value: number) {
  if (targets.value.length) store.setStyle({ fontSizePoints: value || null })
  else store.annotationStyle({ ...store.draft.annotationStyle, fontSizePoints: value || null })
}
function color(value: string | undefined) {
  if (value?.length !== 7 || !/^#[0-9a-f]{6}$/i.test(value)) return
  if (targets.value.length) store.setStyle({ color: value.toUpperCase() })
  else store.annotationStyle({ ...store.draft.annotationStyle, color: value.toUpperCase() })
}
function commitColor() {
  const value = colorInput.value
  if (value.length !== 7 || !/^#[0-9a-f]{6}$/i.test(value)) { colorError.value = '请输入 #RRGGBB 格式的颜色'; return }
  colorError.value = ''; color(value)
}
const currentPitch = computed(() => store.selectedId ? store.pitch(store.selectedId) : null)
const a = computed(() => store.annotations?.annotations.find(n => n.noteId === store.selectedId))
const unresolvedRhythm = computed(() => store.revision?.score.warnings.filter(warning => warning.startsWith('unresolved-rhythm:')).length ?? 0)
const incompleteKeyHeads = computed(() => store.revision?.score.warnings.filter(warning => warning.startsWith('invalid-key-signature:')).length ?? 0)
function pitch(field: keyof Pitch, value: string | number) {
  if (!store.selectedId || !currentPitch.value) return
  store.correct(store.selectedId, { ...currentPitch.value, [field]: value })
}
function mode(value: string) {
  store.numbering({ mode: value as Numbering['mode'], keys: value === 'MovableDo' ? (store.draft.numbering.keys.length ? store.draft.numbering.keys : [{ startMeasure: 1, tonic: { step: 'C', alter: 0, octave: 4 }, scale: 'Major' }]) : [] })
}
function key(index: number, field: string, value: string | number) {
  const numbering = JSON.parse(JSON.stringify(store.draft.numbering)) as Numbering
  const change = numbering.keys[index]; if (!change) return
  if (field === 'startMeasure') change.startMeasure = Number(value)
  else if (field === 'scale') change.scale = value as 'Major' | 'NaturalMinor'
  else change.tonic = { ...change.tonic, [field]: value }
  store.numbering(numbering)
}
function addKey() {
  const numbering = JSON.parse(JSON.stringify(store.draft.numbering)) as Numbering
  numbering.keys.push({ startMeasure: (numbering.keys.at(-1)?.startMeasure ?? 1) + 1, tonic: { step: 'G', alter: 0, octave: 4 }, scale: 'Major' })
  store.numbering(numbering)
}
function removeKey(index: number) { store.numbering({ ...store.draft.numbering, keys: store.draft.numbering.keys.filter((_, i) => i !== index) }) }
</script>

<template>
  <aside class="inspector glass" aria-label="音符校对面板">
    <div class="inspector-title"><span><UIcon name="i-lucide-sliders-horizontal" /> 校对与标注</span><UButton class="inspector-close" icon="i-lucide-x" color="neutral" variant="ghost" aria-label="关闭校对面板" @click="emit('close')" /></div>
    <div class="inspector-content">
      <section class="inspector-section annotation-appearance">
        <div class="section-label">标注样式 <span>APPEARANCE</span>
          <span v-if="targets.length" class="style-scope">已选 {{ targets.length }} 个</span>
        </div>
        <UFormField label="字号" name="annotationFontSize">
          <USelect :model-value="scope.fontSizePoints ?? 0" :items="fontSizes" aria-label="标注字号" :disabled="!store.editable" class="w-full" @update:model-value="fontSize(Number($event))" />
        </UFormField>
        <UFormField label="颜色" name="annotationColor" :error="colorError || false" class="mt-3">
          <div class="flex items-center gap-2">
            <button v-for="swatch in colors" :key="swatch.value" type="button" class="size-7 rounded-full border-2 border-default disabled:opacity-40" :style="{ backgroundColor: swatch.value, outline: scope.color === swatch.value ? '2px solid var(--ui-primary)' : 'none', outlineOffset: '2px' }" :aria-label="`标注${swatch.name}`" :aria-pressed="scope.color === swatch.value" :disabled="!store.editable" @click="color(swatch.value)" />
            <UPopover>
              <UButton icon="i-lucide-palette" color="neutral" variant="soft" aria-label="自定义标注颜色" :disabled="!store.editable" />
              <template #content><UColorPicker :model-value="scope.color" format="hex" class="p-3" :disabled="!store.editable" @update:model-value="color" /></template>
            </UPopover>
          </div>
          <UInput v-model="colorInput" aria-label="标注颜色十六进制" class="mt-3 w-full" :maxlength="7" :disabled="!store.editable" @change="commitColor" />
        </UFormField>
        <UButton v-if="targets.length && targets.some(id => store.styleOverridden(id))" icon="i-lucide-undo-2" size="xs" color="neutral" variant="ghost" label="恢复跟随全页样式" class="mt-2" :disabled="!store.editable" @click="store.resetStyle()" />
        <p class="field-help">{{ targets.length ? '只作用于选中的标注；在谱面上按住左键框选可批量修改。' : '作用于当前页所有标注，预览即时更新。' }}保存后重新检查留白；较大的字号可能产生待放置标注。</p>
      </section>
      <section class="inspector-section">
        <div class="section-label">简谱方式 <span>NUMBERING</span></div>
        <USelect :model-value="store.draft.numbering.mode" :items="modes" aria-label="简谱方式" :disabled="!store.editable" @update:model-value="mode(String($event))" />
        <p class="field-help">{{ store.draft.numbering.mode === 'FixedC' ? '以 C4 = 1 为基准，保留升降音与八度点。' : '主音与大小调由你确认，不自动猜测调性。' }}</p>
        <div v-for="(change, index) in store.draft.numbering.keys" :key="index" class="key-change">
          <div class="key-title"><span>{{ index ? '段落转调' : '起始调性' }}</span><UButton v-if="index" icon="i-lucide-x" color="neutral" variant="ghost" size="xs" aria-label="移除转调" @click="removeKey(index)" /></div>
          <label v-if="index" class="field"><span>从第几小节开始</span><UInput type="number" :min="2" :model-value="change.startMeasure" aria-label="转调起始小节" :disabled="!store.editable" @update:model-value="key(index, 'startMeasure', Number($event))" /></label>
          <div class="tonic-row"><USelect :model-value="change.tonic.step" :items="steps" aria-label="调性主音" :disabled="!store.editable" @update:model-value="key(index, 'step', String($event))" /><USelect :model-value="change.tonic.alter" :items="alterations" aria-label="主音变化音" :disabled="!store.editable" @update:model-value="key(index, 'alter', Number($event))" /><USelect :model-value="change.tonic.octave" :items="[2, 3, 4, 5, 6]" aria-label="主音参考八度" :disabled="!store.editable" @update:model-value="key(index, 'octave', Number($event))" /></div>
          <USelect :model-value="change.scale" :items="scales" aria-label="大小调" :disabled="!store.editable" @update:model-value="key(index, 'scale', String($event))" />
        </div>
        <UButton v-if="store.draft.numbering.mode === 'MovableDo'" icon="i-lucide-plus" size="xs" color="neutral" variant="ghost" label="添加段落转调" :disabled="!store.editable" @click="addKey" />
      </section>

      <section class="inspector-section note-editor">
        <div class="section-label">音符校对 <span>NOTE DETAILS</span></div>
        <template v-if="store.selected && currentPitch">
          <div class="pitch-display"><span>{{ currentPitch.step }}<small>{{ currentPitch.alter > 0 ? '♯'.repeat(currentPitch.alter) : '♭'.repeat(-currentPitch.alter) }}</small><sub>{{ currentPitch.octave }}</sub></span><span class="note-meta">第 {{ store.selected.measure }} 小节<br>谱表 {{ store.selected.staff }} · 声部 {{ store.selected.voice || '未分配' }}</span></div>
          <div class="pitch-fields"><label class="field"><span>音名</span><USelect :model-value="currentPitch.step" :items="steps" aria-label="音名" :disabled="!store.editable" @update:model-value="pitch('step', String($event))" /></label><label class="field"><span>八度</span><USelect :model-value="currentPitch.octave" :items="[0, 1, 2, 3, 4, 5, 6, 7, 8, 9]" aria-label="八度" :disabled="!store.editable" @update:model-value="pitch('octave', Number($event))" /></label></div>
          <label class="field"><span>变化音</span><USelect :model-value="currentPitch.alter" :items="alterations" aria-label="变化音" :disabled="!store.editable" @update:model-value="pitch('alter', Number($event))" /></label>
          <p v-if="store.draft.pitches[store.selected.id]" class="field-help accent-text">已修改音高，保存后更新简谱字形。</p>
          <div class="note-actions"><UButton icon="i-lucide-move" color="neutral" variant="soft" label="放置到空白处" :disabled="!store.editable || !a" @click="emit('place')" /><UButton :icon="store.placement(store.selected.id)?.hidden ? 'i-lucide-eye' : 'i-lucide-eye-off'" color="neutral" variant="ghost" :label="store.placement(store.selected.id)?.hidden ? '显示标注' : '隐藏标注'" :disabled="!store.editable" @click="store.hide(store.selected.id)" /><UButton icon="i-lucide-rotate-ccw" color="neutral" variant="ghost" label="恢复自动布局" :disabled="!store.editable" @click="store.automatic(store.selected.id)" /></div>
          <p class="field-help">拖动数字可调整位置；方向键微调，Shift 加速。保存时会重新检查留白，遮挡原谱的位置不会写入 PDF。</p>
        </template>
        <div v-else class="select-note-hint"><div class="hint-symbol"><UIcon name="i-lucide-mouse-pointer-2" /></div><p>每个音符，都可以更清晰</p><span>点击原谱中的音符<br>在这里校正音高与标注</span></div>
      </section>

      <section class="review-summary">
        <div><UIcon name="i-lucide-scan-eye" /><span>留一点时间，听一听</span></div>
        <p>自动识别需要人工校对。数字提示音高，不代表指法。</p>
        <p v-if="unresolvedRhythm" role="status">本页有 {{ unresolvedRhythm }} 个音符的节奏声部尚未确定，音高标注已保留，请重点校对。</p>
        <p v-if="incompleteKeyHeads" role="status">本页有 {{ incompleteKeyHeads }} 个音符的调号识别不完整，请重点核对转调处的升降号。</p>
        <button v-if="store.pending" class="pending-button" @click="emit('pending')"><span class="status-dot amber" /> {{ store.pending }} 个标注待放置 <UIcon name="i-lucide-arrow-up-right" /></button>
        <span v-if="store.hiddenCount" class="hidden-count">{{ store.hiddenCount }} 个标注已隐藏</span>
      </section>
    </div>
  </aside>
</template>
