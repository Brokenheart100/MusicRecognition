<script setup lang="ts">
import type { Numbering, Pitch, Step } from '~~/shared/types/score'
// 右侧检查器（inspector）：校对时最常用的面板，显示并修改当前选中音符的相关信息。
// 它能改标注字号和颜色、设置简谱换算方式（固定 C 调或首调），并汇总校对提醒。
// 所有修改都写进 store 的草稿，点保存后才提交成新修订。
// 检查器只读 store，通过事件把放置、待处理、关闭等画布操作交给父组件。

const { t } = useI18n()
const emit = defineEmits<{ place: []; pending: []; close: [] }>()
const store = useWorkspaceStore()
// 七个音名（白键）和五种升降记号：-2 重降、-1 降、0 还原、+1 升、+2 重升。
const steps: Step[] = ['C', 'D', 'E', 'F', 'G', 'A', 'B']
const alterations = computed(() => [{ label: t('inspector.alterDoubleFlat'), value: -2 }, { label: t('inspector.alterFlat'), value: -1 }, { label: t('inspector.alterNatural'), value: 0 }, { label: t('inspector.alterSharp'), value: 1 }, { label: t('inspector.alterDoubleSharp'), value: 2 }])
// 简谱换算的两种模式：固定 C 调（FixedC）和首调（MovableDo）。
const modes = computed(() => [{ label: t('inspector.modeFixedC'), value: 'FixedC' }, { label: t('inspector.modeMovableDo'), value: 'MovableDo' }])
// 音阶种类：大调（Major）和自然小调（NaturalMinor）。
const scales = computed(() => [{ label: t('inspector.major'), value: 'Major' }, { label: t('inspector.naturalMinor'), value: 'NaturalMinor' }])
// 字号选项：0 表示自动（按五线谱行距推荐），其余是具体点数（pt）。
const fontSizes = computed(() => [{ label: t('inspector.fontAuto'), value: 0 }, ...[3, 4, 5, 6, 7, 8, 10, 12, 16].map(value => ({ label: value + ' pt', value }))])
// 预设五种常用颜色，方便快速统一一份谱子的标注风格。
const colors = computed(() => [{ name: t('inspector.colorBlack'), value: '#000000' }, { name: t('inspector.colorGray'), value: '#475569' }, { name: t('inspector.colorBlue'), value: '#2563EB' }, { name: t('inspector.colorRed'), value: '#DC2626' }, { name: t('inspector.colorGreen'), value: '#15803D' }])
// 手动输入颜色的文本内容，以及校验失败时的错误提示。
const colorError = ref('')
const colorInput = ref('')
// Style controls edit the marquee selection when there is one, otherwise the whole page.
const targets = computed(() => store.styleTargets)
// 面板当前显示的样式：多选时取第一个目标的样式，否则取整页默认值。
const scope = computed(() => targets.value.length ? store.styleOf(targets.value[0]!) : store.draft.annotationStyle)
// 样式切换时同步十六进制输入框，并清掉上一次的报错。
watch(scope, value => { colorInput.value = value.color; colorError.value = '' }, { immediate: true })
// 修改字号：0 存成 null 表示自动；有选中目标就逐个覆盖，否则改整页默认。
function fontSize(value: number) {
  if (targets.value.length) store.setStyle({ fontSizePoints: value || null })
  else store.annotationStyle({ ...store.draft.annotationStyle, fontSizePoints: value || null })
}
// 校验并应用颜色：必须是 7 位 #RRGGBB，非法值直接忽略。
function color(value: string | undefined) {
  if (value?.length !== 7 || !/^#[0-9a-f]{6}$/i.test(value)) return
  if (targets.value.length) store.setStyle({ color: value.toUpperCase() })
  else store.annotationStyle({ ...store.draft.annotationStyle, color: value.toUpperCase() })
}
// 提交手动输入的色值：格式不对就在输入框下方提示错误。
function commitColor() {
  const value = colorInput.value
// 长度或字符不符合就报错；合法值再交给上面的 color 应用。
  if (value.length !== 7 || !/^#[0-9a-f]{6}$/i.test(value)) { colorError.value = t('inspector.colorError'); return }
  colorError.value = ''; color(value)
}
// 选中音符的当前音高：草稿里修正过的优先，否则用服务器识别值。
const currentPitch = computed(() => store.selectedId ? store.pitch(store.selectedId) : null)
// 选中音符对应的标注条目；没有标注时，放置等按钮要禁用。
const a = computed(() => store.annotations?.annotations.find(n => n.noteId === store.selectedId))
// 三类校对提醒：节奏无法确定、调号信息不完整、页眉区域的音高被拒绝。
// 分别按警告文本的前缀或关键字统计数量，显示在面板底部。
const unresolvedRhythm = computed(() => store.revision?.score.warnings.filter(warning => warning.startsWith('unresolved-rhythm:')).length ?? 0)
const incompleteKeyHeads = computed(() => store.revision?.score.warnings.filter(warning => warning.startsWith('invalid-key-signature:')).length ?? 0)
const rejectedHeaderOctaves = computed(() => store.revision?.score.warnings.filter(warning => warning.includes(':header-outside-staff-region')).length ?? 0)
// 人工修正选中音符的音名、八度或升降号，写入草稿。
function pitch(field: keyof Pitch, value: string | number) {
// 没有选中音符或拿不到音高时直接忽略。
  if (!store.selectedId || !currentPitch.value) return
  store.correct(store.selectedId, { ...currentPitch.value, [field]: value })
}
// 切换简谱换算模式。第一次切到首调时，默认补一条从第 1 小节起 C 大调的记录。
function mode(value: string) {
  store.numbering({ mode: value as Numbering['mode'], keys: value === 'MovableDo' ? (store.draft.numbering.keys.length ? store.draft.numbering.keys : [{ startMeasure: 1, tonic: { step: 'C', alter: 0, octave: 4 }, scale: 'Major' }]) : [] })
}
// 修改第 index 条调号记录：起始小节、主音音名、升降、八度或大小调。
// 先深拷贝一份再改，保证撤销历史拿到的是完整的新草稿。
function key(index: number, field: string, value: string | number) {
  const numbering = JSON.parse(JSON.stringify(store.draft.numbering)) as Numbering
// 找不到这一条记录就忽略，避免数组越界改错数据。
  const change = numbering.keys[index]; if (!change) return
  if (field === 'startMeasure') change.startMeasure = Number(value)
  else if (field === 'scale') change.scale = value as 'Major' | 'NaturalMinor'
  else change.tonic = { ...change.tonic, [field]: value }
  store.numbering(numbering)
}
// 追加一条转调记录：默认从上一段的下一小节开始，主音是 G 大调。
function addKey() {
  const numbering = JSON.parse(JSON.stringify(store.draft.numbering)) as Numbering
// 新记录的起始小节取上一条的下一个小节，避免两段调号重叠。
  numbering.keys.push({ startMeasure: (numbering.keys.at(-1)?.startMeasure ?? 1) + 1, tonic: { step: 'G', alter: 0, octave: 4 }, scale: 'Major' })
  store.numbering(numbering)
}
// 删除第 index 条转调记录，其余记录保持原顺序。
function removeKey(index: number) { store.numbering({ ...store.draft.numbering, keys: store.draft.numbering.keys.filter((_, i) => i !== index) }) }
</script>

<template>
<!-- 检查器面板：从上到下是标注外观、简谱换算、音符详情、校对摘要 -->
  <aside class="inspector glass" :aria-label="$t('inspector.aria')">
    <div class="inspector-title"><span><UIcon name="i-lucide-sliders-horizontal" /> {{ $t('inspector.title') }}</span><UButton class="inspector-close" icon="i-lucide-x" color="neutral" variant="ghost" :aria-label="$t('inspector.close')" @click="emit('close')" /></div>
    <div class="inspector-content">
<!-- 标注外观：字号和颜色；有选中音符时只改选中的，否则改整页默认 -->
      <section class="inspector-section annotation-appearance">
        <div class="section-label">{{ $t('inspector.appearance') }} <span class="section-label-en">APPEARANCE</span>
          <UBadge v-if="targets.length" color="neutral" variant="soft" size="sm" class="section-count ms-auto" :label="$t('inspector.selected', { count: targets.length })" />
        </div>
<!-- 字号选择：自动或 3~16 pt；谱面密集时用小字号 -->
        <UFormField :label="$t('inspector.size')" name="annotationFontSize" size="xs">
          <USelect :model-value="scope.fontSizePoints ?? 0" :items="fontSizes" :aria-label="$t('inspector.sizeAria')" :disabled="!store.editable" class="w-full" @update:model-value="fontSize(Number($event))" />
        </UFormField>
<!-- 颜色：预设色板、自定义取色器和手动输入十六进制 -->
        <UFormField :label="$t('inspector.color')" name="annotationColor" :error="colorError || false" class="mt-3" size="xs">
          <div class="flex items-center gap-2">
            <button v-for="swatch in colors" :key="swatch.value" type="button" class="color-swatch size-7 rounded-full border-2 border-default disabled:opacity-40" :style="{ backgroundColor: swatch.value, boxShadow: scope.color === swatch.value ? '0 0 0 2px var(--ui-bg-elevated), 0 0 0 4px var(--ui-primary)' : 'none' }" :aria-label="$t('inspector.swatchAria', { name: swatch.name })" :aria-pressed="scope.color === swatch.value" :disabled="!store.editable" @click="color(swatch.value)" />
            <UPopover>
              <UButton icon="i-lucide-palette" color="neutral" variant="soft" :aria-label="$t('inspector.customColor')" :disabled="!store.editable" />
              <template #content><UColorPicker :model-value="scope.color" format="hex" class="p-3" :disabled="!store.editable" @update:model-value="color" /></template>
            </UPopover>
          </div>
<!-- 手动输入 #RRGGBB；格式错误时显示校验提示 -->
          <UInput v-model="colorInput" :aria-label="$t('inspector.hexAria')" class="mt-3 w-full" :maxlength="7" :disabled="!store.editable" @change="commitColor" />
        </UFormField>
<!-- 选中的音符单独改过样式时，可以一键恢复成整页默认 -->
        <UButton v-if="targets.length && targets.some(id => store.styleOverridden(id))" icon="i-lucide-undo-2" size="xs" color="neutral" variant="ghost" :label="$t('inspector.resetStyle')" class="mt-2" :disabled="!store.editable" @click="store.resetStyle()" />
        <p class="field-help">{{ targets.length ? $t('inspector.scopeSelected') : $t('inspector.scopePage') }}{{ $t('inspector.sizeHelp') }}</p>
<!-- 自动字号下请求服务端重新排版，重新计算每个数字的大小和位置 -->
        <UButton v-if="!targets.length && scope.fontSizePoints === null" class="mt-2" icon="i-lucide-refresh-cw" size="xs" color="neutral" variant="soft"
          :label="$t('inspector.recalc')" :loading="store.saving" :disabled="!store.editable || store.dirty" @click="store.save(true)" />
        <p v-if="!targets.length && scope.fontSizePoints === null" class="field-help">{{ $t('inspector.recalcHint') }}</p>
      </section>
<!-- 简谱换算（numbering）：决定 do 固定为 C 还是跟着调号移动 -->
      <section class="inspector-section">
        <div class="section-label">{{ $t('inspector.numbering') }} <span class="section-label-en">NUMBERING</span></div>
<!-- 两种换算模式；切换后整页数字立即按新规则重算 -->
        <USelect :model-value="store.draft.numbering.mode" :items="modes" :aria-label="$t('inspector.modeAria')" :disabled="!store.editable" @update:model-value="mode(String($event))" />
        <p class="field-help">{{ store.draft.numbering.mode === 'FixedC' ? $t('inspector.fixedHelp') : $t('inspector.movableHelp') }}</p>
<!-- 首调模式下可以列出多条转调记录：从第几小节起换成什么调 -->
        <div v-for="(change, index) in store.draft.numbering.keys" :key="index" class="key-change">
          <div class="key-title"><span>{{ index ? $t('inspector.keyChange') : $t('inspector.startKey') }}</span><UButton v-if="index" icon="i-lucide-x" color="neutral" variant="ghost" size="xs" :aria-label="$t('inspector.removeKeyChange')" @click="removeKey(index)" /></div>
<!-- 转调从第几小节开始；第 1 小节那条不需要填 -->
          <UFormField v-if="index" :label="$t('inspector.keyStartMeasure')" size="xs">
            <UInput type="number" :min="2" :model-value="change.startMeasure" :aria-label="$t('inspector.keyStartMeasureAria')" :disabled="!store.editable" class="w-full" @update:model-value="key(index, 'startMeasure', Number($event))" />
          </UFormField>
<!-- 每条调号记录：主音音名、升降、八度，以及大调或自然小调 -->
          <div class="tonic-row"><USelect :model-value="change.tonic.step" :items="steps" :aria-label="$t('inspector.tonic')" :disabled="!store.editable" @update:model-value="key(index, 'step', String($event))" /><USelect :model-value="change.tonic.alter" :items="alterations" :aria-label="$t('inspector.tonicAlter')" :disabled="!store.editable" @update:model-value="key(index, 'alter', Number($event))" /><USelect :model-value="change.tonic.octave" :items="[2, 3, 4, 5, 6]" :aria-label="$t('inspector.tonicOctave')" :disabled="!store.editable" @update:model-value="key(index, 'octave', Number($event))" /></div>
          <USelect :model-value="change.scale" :items="scales" :aria-label="$t('inspector.scaleAria')" :disabled="!store.editable" @update:model-value="key(index, 'scale', String($event))" />
        </div>
<!-- 增加一段转调 -->
        <UButton v-if="store.draft.numbering.mode === 'MovableDo'" icon="i-lucide-plus" size="xs" color="neutral" variant="ghost" :label="$t('inspector.addKeyChange')" :disabled="!store.editable" @click="addKey" />
      </section>

<!-- 选中音符详情：显示音高和所在小节、谱表、声部，允许人工修正 -->
      <section class="inspector-section note-editor">
        <div class="section-label">{{ $t('inspector.noteDetails') }} <span class="section-label-en">NOTE DETAILS</span></div>
        <template v-if="store.selected && currentPitch">
<!-- 手工补录的音符带标记，便于和识别结果区分 -->
          <div v-if="store.selected.manual" class="mb-2"><UBadge color="neutral" variant="soft" size="sm" :label="$t('inspector.manualBadge')" /></div>
<!-- 当前音高写法：音名 + 升降号 + 八度，旁边是它的小节和声部信息 -->
          <div class="pitch-display"><span>{{ currentPitch.step }}<small>{{ currentPitch.alter > 0 ? '♯'.repeat(currentPitch.alter) : '♭'.repeat(-currentPitch.alter) }}</small><sub>{{ currentPitch.octave }}</sub></span><span class="note-meta">{{ $t('inspector.measure', { n: store.selected.measure }) }}<br>{{ $t('inspector.staffVoice', { staff: store.selected.staff, voice: store.selected.voice || $t('inspector.unassigned') }) }}</span></div>
<!-- 音名、八度、升降号三个下拉；改动记入草稿，保存后生成新修订 -->
          <div class="pitch-fields">
            <UFormField :label="$t('inspector.step')" size="xs">
              <USelect :model-value="currentPitch.step" :items="steps" :aria-label="$t('inspector.step')" :disabled="!store.editable" class="w-full" @update:model-value="pitch('step', String($event))" />
            </UFormField>
<!-- 八度范围 0~9，和领域层音高的合法范围一致 -->
            <UFormField :label="$t('inspector.octave')" size="xs">
              <USelect :model-value="currentPitch.octave" :items="[0, 1, 2, 3, 4, 5, 6, 7, 8, 9]" :aria-label="$t('inspector.octave')" :disabled="!store.editable" class="w-full" @update:model-value="pitch('octave', Number($event))" />
            </UFormField>
          </div>
          <UFormField :label="$t('inspector.accidental')" size="xs">
            <USelect :model-value="currentPitch.alter" :items="alterations" :aria-label="$t('inspector.accidental')" :disabled="!store.editable" class="w-full" @update:model-value="pitch('alter', Number($event))" />
          </UFormField>
<!-- 提示这个音符的音高被人工改过，和识别结果不同 -->
          <p v-if="store.draft.pitches[store.selected.id]" class="field-help accent-text">{{ $t('inspector.pitchChanged') }}</p>
<!-- 三个动作：手动放置数字、隐藏或显示数字、恢复自动排版位置 -->
          <div class="note-actions"><UButton icon="i-lucide-move" color="neutral" variant="soft" :label="$t('inspector.place')" :disabled="!store.editable || !a" aria-keyshortcuts="P" :title="$t('inspector.placeShortcut')" @click="emit('place')" /><UButton :icon="store.placement(store.selected.id)?.hidden ? 'i-lucide-eye' : 'i-lucide-eye-off'" color="neutral" variant="ghost" :label="store.placement(store.selected.id)?.hidden ? $t('inspector.show') : $t('inspector.hide')" :disabled="!store.editable" @click="store.hide(store.selected.id)" /><UButton icon="i-lucide-rotate-ccw" color="neutral" variant="ghost" :label="$t('inspector.auto')" :disabled="!store.editable" @click="store.automatic(store.selected.id)" /><UButton v-if="store.selected.manual" icon="i-lucide-trash-2" color="error" variant="ghost" :label="$t('inspector.removeNote')" :disabled="!store.editable" @click="store.removeManualNote(store.selected.id)" /></div>
          <p class="field-help">{{ $t('inspector.dragHelp') }}</p>
        </template>
<!-- 没选中音符时的占位提示，告诉用户点画布上的音符 -->
        <UEmpty v-else variant="naked" size="sm" icon="i-lucide-mouse-pointer-2" :title="$t('inspector.emptyTitle')"
          :ui="{ root: 'gap-2 p-0 pb-2', header: 'gap-1', title: 'text-[11px] font-normal text-muted', description: 'text-[10px] leading-5' }">
          <template #description>
            <span>{{ $t('inspector.emptyLine1') }}<br>{{ $t('inspector.emptyLine2') }}</span>
          </template>
        </UEmpty>
      </section>

<!-- 校对摘要：汇总三类警告数量，以及待手动放置和已隐藏的数量 -->
      <section class="review-summary">
        <div><UIcon name="i-lucide-scan-eye" /><span>{{ $t('inspector.reviewTitle') }}</span></div>
        <p>{{ $t('inspector.reviewText') }}</p>
        <p v-if="unresolvedRhythm" role="status">{{ $t('inspector.unresolvedRhythm', { count: unresolvedRhythm }) }}</p>
        <p v-if="incompleteKeyHeads" role="status">{{ $t('inspector.incompleteKey', { count: incompleteKeyHeads }) }}</p>
        <p v-if="rejectedHeaderOctaves" role="status">{{ $t('inspector.rejectedHeader', { count: rejectedHeaderOctaves }) }}</p>
<!-- 待手动放置的数字还有几个；点击跳到画布逐个处理 -->
        <UButton v-if="store.pending" color="warning" variant="soft" size="xs" class="mt-3"
          :ui="{ base: 'w-full justify-start py-2.5' }" @click="emit('pending')">
          <span class="status-dot amber" /> {{ $t('inspector.pendingButton', { count: store.pending }) }}
          <UIcon name="i-lucide-arrow-up-right" class="ms-auto size-3.5" />
        </UButton>
<!-- 被隐藏的数字数量 -->
        <UBadge v-if="store.hiddenCount" color="neutral" variant="soft" size="sm" class="mt-3"
          :label="$t('inspector.hidden', { count: store.hiddenCount })" />
      </section>
    </div>
  </aside>
</template>
