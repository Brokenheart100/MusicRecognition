<script setup lang="ts">
import type { Numbering, Pitch, Step } from '~~/shared/types/score'
const { t } = useI18n()
const emit = defineEmits<{ place: []; pending: []; close: [] }>()
const store = useWorkspaceStore()
const steps: Step[] = ['C', 'D', 'E', 'F', 'G', 'A', 'B']
const alterations = computed(() => [{ label: t('inspector.alterDoubleFlat'), value: -2 }, { label: t('inspector.alterFlat'), value: -1 }, { label: t('inspector.alterNatural'), value: 0 }, { label: t('inspector.alterSharp'), value: 1 }, { label: t('inspector.alterDoubleSharp'), value: 2 }])
const modes = computed(() => [{ label: t('inspector.modeFixedC'), value: 'FixedC' }, { label: t('inspector.modeMovableDo'), value: 'MovableDo' }])
const scales = computed(() => [{ label: t('inspector.major'), value: 'Major' }, { label: t('inspector.naturalMinor'), value: 'NaturalMinor' }])
const fontSizes = computed(() => [{ label: t('inspector.fontAuto'), value: 0 }, ...[3, 4, 5, 6, 7, 8, 10, 12, 16].map(value => ({ label: value + ' pt', value }))])
const colors = computed(() => [{ name: t('inspector.colorBlack'), value: '#000000' }, { name: t('inspector.colorGray'), value: '#475569' }, { name: t('inspector.colorBlue'), value: '#2563EB' }, { name: t('inspector.colorRed'), value: '#DC2626' }, { name: t('inspector.colorGreen'), value: '#15803D' }])
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
  if (value.length !== 7 || !/^#[0-9a-f]{6}$/i.test(value)) { colorError.value = t('inspector.colorError'); return }
  colorError.value = ''; color(value)
}
const currentPitch = computed(() => store.selectedId ? store.pitch(store.selectedId) : null)
const a = computed(() => store.annotations?.annotations.find(n => n.noteId === store.selectedId))
const unresolvedRhythm = computed(() => store.revision?.score.warnings.filter(warning => warning.startsWith('unresolved-rhythm:')).length ?? 0)
const incompleteKeyHeads = computed(() => store.revision?.score.warnings.filter(warning => warning.startsWith('invalid-key-signature:')).length ?? 0)
const rejectedHeaderOctaves = computed(() => store.revision?.score.warnings.filter(warning => warning.includes(':header-outside-staff-region')).length ?? 0)
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
  <aside class="inspector glass" :aria-label="$t('inspector.aria')">
    <div class="inspector-title"><span><UIcon name="i-lucide-sliders-horizontal" /> {{ $t('inspector.title') }}</span><UButton class="inspector-close" icon="i-lucide-x" color="neutral" variant="ghost" :aria-label="$t('inspector.close')" @click="emit('close')" /></div>
    <div class="inspector-content">
      <section class="inspector-section annotation-appearance">
        <div class="section-label">{{ $t('inspector.appearance') }} <span class="section-label-en">APPEARANCE</span>
          <UBadge v-if="targets.length" color="neutral" variant="soft" size="sm" class="section-count ms-auto" :label="$t('inspector.selected', { count: targets.length })" />
        </div>
        <UFormField :label="$t('inspector.size')" name="annotationFontSize" size="xs">
          <USelect :model-value="scope.fontSizePoints ?? 0" :items="fontSizes" :aria-label="$t('inspector.sizeAria')" :disabled="!store.editable" class="w-full" @update:model-value="fontSize(Number($event))" />
        </UFormField>
        <UFormField :label="$t('inspector.color')" name="annotationColor" :error="colorError || false" class="mt-3" size="xs">
          <div class="flex items-center gap-2">
            <button v-for="swatch in colors" :key="swatch.value" type="button" class="color-swatch size-7 rounded-full border-2 border-default disabled:opacity-40" :style="{ backgroundColor: swatch.value, boxShadow: scope.color === swatch.value ? '0 0 0 2px var(--ui-bg-elevated), 0 0 0 4px var(--ui-primary)' : 'none' }" :aria-label="$t('inspector.swatchAria', { name: swatch.name })" :aria-pressed="scope.color === swatch.value" :disabled="!store.editable" @click="color(swatch.value)" />
            <UPopover>
              <UButton icon="i-lucide-palette" color="neutral" variant="soft" :aria-label="$t('inspector.customColor')" :disabled="!store.editable" />
              <template #content><UColorPicker :model-value="scope.color" format="hex" class="p-3" :disabled="!store.editable" @update:model-value="color" /></template>
            </UPopover>
          </div>
          <UInput v-model="colorInput" :aria-label="$t('inspector.hexAria')" class="mt-3 w-full" :maxlength="7" :disabled="!store.editable" @change="commitColor" />
        </UFormField>
        <UButton v-if="targets.length && targets.some(id => store.styleOverridden(id))" icon="i-lucide-undo-2" size="xs" color="neutral" variant="ghost" :label="$t('inspector.resetStyle')" class="mt-2" :disabled="!store.editable" @click="store.resetStyle()" />
        <p class="field-help">{{ targets.length ? $t('inspector.scopeSelected') : $t('inspector.scopePage') }}{{ $t('inspector.sizeHelp') }}</p>
        <UButton v-if="!targets.length && scope.fontSizePoints === null" class="mt-2" icon="i-lucide-refresh-cw" size="xs" color="neutral" variant="soft"
          :label="$t('inspector.recalc')" :loading="store.saving" :disabled="!store.editable || store.dirty" @click="store.save(true)" />
        <p v-if="!targets.length && scope.fontSizePoints === null" class="field-help">{{ $t('inspector.recalcHint') }}</p>
      </section>
      <section class="inspector-section">
        <div class="section-label">{{ $t('inspector.numbering') }} <span class="section-label-en">NUMBERING</span></div>
        <USelect :model-value="store.draft.numbering.mode" :items="modes" :aria-label="$t('inspector.modeAria')" :disabled="!store.editable" @update:model-value="mode(String($event))" />
        <p class="field-help">{{ store.draft.numbering.mode === 'FixedC' ? $t('inspector.fixedHelp') : $t('inspector.movableHelp') }}</p>
        <div v-for="(change, index) in store.draft.numbering.keys" :key="index" class="key-change">
          <div class="key-title"><span>{{ index ? $t('inspector.keyChange') : $t('inspector.startKey') }}</span><UButton v-if="index" icon="i-lucide-x" color="neutral" variant="ghost" size="xs" :aria-label="$t('inspector.removeKeyChange')" @click="removeKey(index)" /></div>
          <UFormField v-if="index" :label="$t('inspector.keyStartMeasure')" size="xs">
            <UInput type="number" :min="2" :model-value="change.startMeasure" :aria-label="$t('inspector.keyStartMeasureAria')" :disabled="!store.editable" class="w-full" @update:model-value="key(index, 'startMeasure', Number($event))" />
          </UFormField>
          <div class="tonic-row"><USelect :model-value="change.tonic.step" :items="steps" :aria-label="$t('inspector.tonic')" :disabled="!store.editable" @update:model-value="key(index, 'step', String($event))" /><USelect :model-value="change.tonic.alter" :items="alterations" :aria-label="$t('inspector.tonicAlter')" :disabled="!store.editable" @update:model-value="key(index, 'alter', Number($event))" /><USelect :model-value="change.tonic.octave" :items="[2, 3, 4, 5, 6]" :aria-label="$t('inspector.tonicOctave')" :disabled="!store.editable" @update:model-value="key(index, 'octave', Number($event))" /></div>
          <USelect :model-value="change.scale" :items="scales" :aria-label="$t('inspector.scaleAria')" :disabled="!store.editable" @update:model-value="key(index, 'scale', String($event))" />
        </div>
        <UButton v-if="store.draft.numbering.mode === 'MovableDo'" icon="i-lucide-plus" size="xs" color="neutral" variant="ghost" :label="$t('inspector.addKeyChange')" :disabled="!store.editable" @click="addKey" />
      </section>

      <section class="inspector-section note-editor">
        <div class="section-label">{{ $t('inspector.noteDetails') }} <span class="section-label-en">NOTE DETAILS</span></div>
        <template v-if="store.selected && currentPitch">
          <div class="pitch-display"><span>{{ currentPitch.step }}<small>{{ currentPitch.alter > 0 ? '♯'.repeat(currentPitch.alter) : '♭'.repeat(-currentPitch.alter) }}</small><sub>{{ currentPitch.octave }}</sub></span><span class="note-meta">{{ $t('inspector.measure', { n: store.selected.measure }) }}<br>{{ $t('inspector.staffVoice', { staff: store.selected.staff, voice: store.selected.voice || $t('inspector.unassigned') }) }}</span></div>
          <div class="pitch-fields">
            <UFormField :label="$t('inspector.step')" size="xs">
              <USelect :model-value="currentPitch.step" :items="steps" :aria-label="$t('inspector.step')" :disabled="!store.editable" class="w-full" @update:model-value="pitch('step', String($event))" />
            </UFormField>
            <UFormField :label="$t('inspector.octave')" size="xs">
              <USelect :model-value="currentPitch.octave" :items="[0, 1, 2, 3, 4, 5, 6, 7, 8, 9]" :aria-label="$t('inspector.octave')" :disabled="!store.editable" class="w-full" @update:model-value="pitch('octave', Number($event))" />
            </UFormField>
          </div>
          <UFormField :label="$t('inspector.accidental')" size="xs">
            <USelect :model-value="currentPitch.alter" :items="alterations" :aria-label="$t('inspector.accidental')" :disabled="!store.editable" class="w-full" @update:model-value="pitch('alter', Number($event))" />
          </UFormField>
          <p v-if="store.draft.pitches[store.selected.id]" class="field-help accent-text">{{ $t('inspector.pitchChanged') }}</p>
          <div class="note-actions"><UButton icon="i-lucide-move" color="neutral" variant="soft" :label="$t('inspector.place')" :disabled="!store.editable || !a" aria-keyshortcuts="P" :title="$t('inspector.placeShortcut')" @click="emit('place')" /><UButton :icon="store.placement(store.selected.id)?.hidden ? 'i-lucide-eye' : 'i-lucide-eye-off'" color="neutral" variant="ghost" :label="store.placement(store.selected.id)?.hidden ? $t('inspector.show') : $t('inspector.hide')" :disabled="!store.editable" @click="store.hide(store.selected.id)" /><UButton icon="i-lucide-rotate-ccw" color="neutral" variant="ghost" :label="$t('inspector.auto')" :disabled="!store.editable" @click="store.automatic(store.selected.id)" /></div>
          <p class="field-help">{{ $t('inspector.dragHelp') }}</p>
        </template>
        <UEmpty v-else variant="naked" size="sm" icon="i-lucide-mouse-pointer-2" :title="$t('inspector.emptyTitle')"
          :ui="{ root: 'gap-2 p-0 pb-2', header: 'gap-1', title: 'text-[11px] font-normal text-muted', description: 'text-[10px] leading-5' }">
          <template #description>
            <span>{{ $t('inspector.emptyLine1') }}<br>{{ $t('inspector.emptyLine2') }}</span>
          </template>
        </UEmpty>
      </section>

      <section class="review-summary">
        <div><UIcon name="i-lucide-scan-eye" /><span>{{ $t('inspector.reviewTitle') }}</span></div>
        <p>{{ $t('inspector.reviewText') }}</p>
        <p v-if="unresolvedRhythm" role="status">{{ $t('inspector.unresolvedRhythm', { count: unresolvedRhythm }) }}</p>
        <p v-if="incompleteKeyHeads" role="status">{{ $t('inspector.incompleteKey', { count: incompleteKeyHeads }) }}</p>
        <p v-if="rejectedHeaderOctaves" role="status">{{ $t('inspector.rejectedHeader', { count: rejectedHeaderOctaves }) }}</p>
        <UButton v-if="store.pending" color="warning" variant="soft" size="xs" class="mt-3"
          :ui="{ base: 'w-full justify-start py-2.5' }" @click="emit('pending')">
          <span class="status-dot amber" /> {{ $t('inspector.pendingButton', { count: store.pending }) }}
          <UIcon name="i-lucide-arrow-up-right" class="ms-auto size-3.5" />
        </UButton>
        <UBadge v-if="store.hiddenCount" color="neutral" variant="soft" size="sm" class="mt-3"
          :label="$t('inspector.hidden', { count: store.hiddenCount })" />
      </section>
    </div>
  </aside>
</template>
