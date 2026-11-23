<script setup lang="ts">
const emit = defineEmits<{ requestImport: [] }>()
const store = useWorkspaceStore()
const { t } = useI18n()
const dragOver = ref(false)
const engineItems = computed(() => [
  { label: t('welcome.engineAudiveris'), value: 'Audiveris' as const },
  { label: t('welcome.engineHomr'), value: 'Homr' as const },
])
async function drop(event: DragEvent) {
  dragOver.value = false
  const file = event.dataTransfer?.files[0]
  if (file) await store.upload(file)
}
</script>

<template>
  <main class="welcome" @dragover.prevent="dragOver = true" @dragleave.prevent="dragOver = false" @drop.prevent="drop">
    <div class="welcome-heading">
      <div>
        <p class="eyebrow"><span /> {{ $t('welcome.eyebrow') }}</p>
        <h1>{{ $t('welcome.headingLead') }}<em>{{ $t('welcome.headingAccent') }}</em></h1>
        <p class="welcome-description">{{ $t('welcome.description1') }}<br>{{ $t('welcome.description2') }}</p>
      </div><span class="welcome-edition">YOUR QUIET<br>MUSIC SPACE <i>01 —</i></span>
    </div>
    <div class="welcome-grid">
      <section class="import-card glass" :class="{ 'is-dragging': dragOver }" :aria-label="$t('welcome.importAria')">
        <span class="card-eyebrow">{{ $t('welcome.cardEyebrow') }}</span>
        <div class="upload-symbol">
          <UIcon name="i-lucide-file-music" /><span>
            <UIcon name="i-lucide-plus" />
          </span>
        </div>
        <h2>{{ $t('welcome.uploadHeading') }}</h2>
        <p>{{ $t('welcome.uploadHint1') }}<br>{{ $t('welcome.uploadHint2') }}</p>
        <UButton icon="i-lucide-upload" :label="$t('welcome.chooseScore')" size="xl" class="upload-cta"
          :loading="store.uploading" @click="emit('requestImport')" />
        <span class="upload-formats">PDF · JPG · PNG <i /> {{ $t('welcome.formatHint') }}</span>
        <div class="scan-enhancement scan-engine">
          <span>{{ $t('welcome.engine') }}</span>
          <USelect v-model="store.recognitionEngine" :items="engineItems" :disabled="store.uploading"
            :aria-label="$t('welcome.engine')" size="xs" variant="ghost" class="scan-engine-select"
            :ui="{ base: 'text-[12px]/4 px-1.5 pe-7 py-0.5' }" />
        </div>
        <p v-if="store.recognitionEngine === 'Homr'" class="upload-formats">{{ $t('welcome.homrHint') }}</p>
        <UCheckbox v-model="store.enhanceScan" :disabled="store.uploading" size="sm" class="scan-enhancement"
          :ui="{ root: 'gap-2.5', label: 'text-[12px] font-normal', description: 'mt-1 text-[11px] leading-[1.55] text-muted' }">
          <template #label>{{ $t('welcome.enhance') }} <em>{{ $t('welcome.experimental') }}</em></template>
          <template #description>{{ $t('welcome.enhanceHint') }}</template>
        </UCheckbox>
        <div class="sample-divider"><span /> {{ $t('welcome.divider') }} <span /></div>
        <UButton color="neutral" variant="ghost" class="sample-button" :disabled="store.uploading"
          :ui="{ base: 'w-full justify-start font-normal hover:bg-transparent disabled:opacity-100' }"
          @click="store.example()"><span class="sample-icon">
            <UIcon name="i-lucide-play" />
          </span><span><strong>{{ $t('welcome.sampleTitle') }}</strong><small>{{ $t('welcome.sampleHint')
            }}</small></span>
          <UIcon name="i-lucide-arrow-up-right" />
        </UButton>
      </section>

      <section class="preview-scene" :aria-label="$t('welcome.previewAria')">
        <div class="scene-glow" />
        <div class="scene-orbit orbit-one" />
        <div class="scene-orbit orbit-two" />
        <div class="floating-note note-one">♩</div>
        <div class="floating-note note-two">♫</div>
        <div class="sample-paper">
          <div class="sample-paper-header"><span>PIANO COLLECTION</span>
            <UIcon name="i-lucide-music-2" />
          </div>
          <h3>Minuet</h3>
          <p>{{ $t('welcome.caption') }}</p><img src="/samples/minuet-preview.png" :alt="$t('welcome.sampleAlt')">
          <div class="sample-paper-footer"><span>ORIGINAL SCORE, WITH A LITTLE HELP.</span><span>01</span></div>
        </div>
        <div class="floating-badge glass"><span class="check-orb">
            <UIcon name="i-lucide-check" />
          </span><span><strong>{{ $t('welcome.badgeTitle') }}</strong><small>{{ $t('welcome.badgeHint')
            }}</small></span></div>
        <span class="scene-caption">A FAMILIAR NOTE. A NEW BEGINNING.</span>
      </section>
    </div>
    <section class="feature-strip" :aria-label="$t('welcome.processAria')">
      <div><span class="feature-icon">
          <UIcon name="i-lucide-scan-line" />
        </span>
        <p><strong>{{ $t('welcome.feature1Title') }}</strong><small>{{ $t('welcome.feature1Hint') }}</small></p><span
          class="feature-number">01</span>
      </div>
      <div><span class="feature-icon">
          <UIcon name="i-lucide-pencil-line" />
        </span>
        <p><strong>{{ $t('welcome.feature2Title') }}</strong><small>{{ $t('welcome.feature2Hint') }}</small></p><span
          class="feature-number">02</span>
      </div>
      <div><span class="feature-icon">
          <UIcon name="i-lucide-printer" />
        </span>
        <p><strong>{{ $t('welcome.feature3Title') }}</strong><small>{{ $t('welcome.feature3Hint') }}</small></p><span
          class="feature-number">03</span>
      </div>
    </section>
    <footer class="welcome-footer"><span>{{ $t('welcome.footerBrand') }}</span><span>{{ $t('welcome.footerNote')
      }}</span></footer>
  </main>
</template>
