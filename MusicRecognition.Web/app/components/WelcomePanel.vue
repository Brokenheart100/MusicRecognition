<script setup lang="ts">
// 首页的欢迎面板：用户还没打开乐谱时看到的"上传入口 + 示例 + 产品介绍"。
// 左栏负责上传（选择文件、拖拽文件、选识别引擎、增强扫描），右栏是效果示意图。
// 底部三条横幅说明"识别 → 校对 → 导出"的完整流程。
// 向父页面发出"请打开文件选择框"的请求；真正选文件由外层负责。
const emit = defineEmits<{ requestImport: [] }>()
// 工作台状态仓库：上传、识别引擎、扫描增强等操作都调它。
const store = useWorkspaceStore()
const { t } = useI18n()
// 文件是否正被拖到页面上，用来显示高亮边框。
const dragOver = ref(false)
// 识别引擎（engine）下拉选项：Audiveris 和 Homr，用户可按谱面类型选择。
const engineItems = computed(() => [
  { label: t('welcome.engineAudiveris'), value: 'Audiveris' as const },
  { label: t('welcome.engineHomr'), value: 'Homr' as const },
])
// 用户把文件拖进页面后：先取消高亮，再取第一个文件交给状态仓库上传。
async function drop(event: DragEvent) {
  dragOver.value = false
  const file = event.dataTransfer?.files[0]
// 上传成功后由状态仓库负责切换到工作台，这里不处理界面跳转。
  if (file) await store.upload(file)
}
</script>

<template>
<!-- 整个首页都能接收拖拽：拖着文件进来会高亮，松手就开始上传。 -->
  <main class="welcome" @dragover.prevent="dragOver = true" @dragleave.prevent="dragOver = false" @drop.prevent="drop">
<!-- 标题区：一句口号 + 右侧的装饰性期刊编号。 -->
    <div class="welcome-heading">
      <div>
        <p class="eyebrow"><span /> {{ $t('welcome.eyebrow') }}</p>
<!-- 主标题：前半句常规字重，后半句用斜体强调。 -->
        <h1>{{ $t('welcome.headingLead') }}<em>{{ $t('welcome.headingAccent') }}</em></h1>
<!-- 两行产品说明，中间强制换行。 -->
        <p class="welcome-description">{{ $t('welcome.description1') }}<br>{{ $t('welcome.description2') }}</p>
      </div><span class="welcome-edition">YOUR QUIET<br>MUSIC SPACE <i>01 —</i></span>
    </div>
<!-- 主体分左右两栏：左边上传，右边看效果。 -->
    <div class="welcome-grid">
<!-- 左栏上传卡片：核心入口，可以点按钮选文件，也可以直接拖文件进来。 -->
      <section class="import-card glass" :class="{ 'is-dragging': dragOver }" :aria-label="$t('welcome.importAria')">
        <span class="card-eyebrow">{{ $t('welcome.cardEyebrow') }}</span>
<!-- 上传图标（音符 + 加号），纯装饰。 -->
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
<!-- 识别引擎选择：不同引擎的识别效果和速度不同。 -->
        <div class="scan-enhancement scan-engine">
          <span>{{ $t('welcome.engine') }}</span>
<!-- 引擎下拉框：小尺寸、无边框，嵌在卡片里不抢视觉；上传期间不可改。 -->
          <USelect v-model="store.recognitionEngine" :items="engineItems" :disabled="store.uploading"
            :aria-label="$t('welcome.engine')" size="xs" variant="ghost" class="scan-engine-select"
            :ui="{ base: 'text-[12px]/4 px-1.5 pe-7 py-0.5' }" />
        </div>
<!-- 只有选了 Homr 才显示这行说明，提醒用户它的使用特点。 -->
        <p v-if="store.recognitionEngine === 'Homr'" class="upload-formats">{{ $t('welcome.homrHint') }}</p>
<!-- "扫描增强"开关：先对拍照/扫描的图片做处理再识别，目前标为实验性功能。 -->
        <UCheckbox v-model="store.enhanceScan" :disabled="store.uploading" size="sm" class="scan-enhancement"
          :ui="{ root: 'gap-2.5', label: 'text-[12px] font-normal', description: 'mt-1 text-[11px] leading-[1.55] text-muted' }">
<!-- 开关标签文字后面跟一个"实验性"小标记。 -->
          <template #label>{{ $t('welcome.enhance') }} <em>{{ $t('welcome.experimental') }}</em></template>
          <template #description>{{ $t('welcome.enhanceHint') }}</template>
        </UCheckbox>
<!-- 分隔线下面是"用示例乐谱试试"的快捷入口。 -->
        <div class="sample-divider"><span /> {{ $t('welcome.divider') }} <span /></div>
<!-- 示例按钮：点一下直接加载内置示例乐谱，方便新用户先体验再上传自己的谱。 -->
        <UButton color="neutral" variant="ghost" class="sample-button" :disabled="store.uploading"
          :ui="{ base: 'w-full justify-start font-normal hover:bg-transparent disabled:opacity-100' }"
          @click="store.example()"><span class="sample-icon">
            <UIcon name="i-lucide-play" />
          </span><span><strong>{{ $t('welcome.sampleTitle') }}</strong><small>{{ $t('welcome.sampleHint')
            }}</small></span>
          <UIcon name="i-lucide-arrow-up-right" />
        </UButton>
      </section>

<!-- 右栏是纯装饰的预览场景：光晕、轨道、漂浮音符、示例纸张和成功徽章。 -->
      <section class="preview-scene" :aria-label="$t('welcome.previewAria')">
<!-- 下面几层光晕和圆环只负责好看，不承载任何功能。 -->
        <div class="scene-glow" />
        <div class="scene-orbit orbit-one" />
        <div class="scene-orbit orbit-two" />
        <div class="floating-note note-one">♩</div>
        <div class="floating-note note-two">♫</div>
<!-- 中间那张"示例纸"：标题 Minuet（小步舞曲）和谱面缩略图。 -->
        <div class="sample-paper">
<!-- 纸张顶部的小标题栏和音符图标。 -->
          <div class="sample-paper-header"><span>PIANO COLLECTION</span>
            <UIcon name="i-lucide-music-2" />
          </div>
          <h3>Minuet</h3>
<!-- 示例谱面图片；alt 文案也来自翻译文件，读屏软件能念出来。 -->
          <p>{{ $t('welcome.caption') }}</p><img src="/samples/minuet-preview.png" :alt="$t('welcome.sampleAlt')">
          <div class="sample-paper-footer"><span>ORIGINAL SCORE, WITH A LITTLE HELP.</span><span>01</span></div>
        </div>
<!-- 右侧成功徽章，用打勾图标暗示"标注完成"。 -->
        <div class="floating-badge glass"><span class="check-orb">
            <UIcon name="i-lucide-check" />
          </span><span><strong>{{ $t('welcome.badgeTitle') }}</strong><small>{{ $t('welcome.badgeHint')
            }}</small></span></div>
<!-- 场景底部的英文装饰文案。 -->
        <span class="scene-caption">A FAMILIAR NOTE. A NEW BEGINNING.</span>
      </section>
    </div>
<!-- 底部三条流程说明：识别、校对、导出。 -->
    <section class="feature-strip" :aria-label="$t('welcome.processAria')">
<!-- 流程 1：扫描识别。 -->
      <div><span class="feature-icon">
          <UIcon name="i-lucide-scan-line" />
        </span>
        <p><strong>{{ $t('welcome.feature1Title') }}</strong><small>{{ $t('welcome.feature1Hint') }}</small></p><span
          class="feature-number">01</span>
      </div>
<!-- 流程 2：逐音校对。 -->
      <div><span class="feature-icon">
          <UIcon name="i-lucide-pencil-line" />
        </span>
        <p><strong>{{ $t('welcome.feature2Title') }}</strong><small>{{ $t('welcome.feature2Hint') }}</small></p><span
          class="feature-number">02</span>
      </div>
<!-- 流程 3：导出 PDF。 -->
      <div><span class="feature-icon">
          <UIcon name="i-lucide-printer" />
        </span>
        <p><strong>{{ $t('welcome.feature3Title') }}</strong><small>{{ $t('welcome.feature3Hint') }}</small></p><span
          class="feature-number">03</span>
      </div>
    </section>
<!-- 页脚：品牌名和一句说明。 -->
    <footer class="welcome-footer"><span>{{ $t('welcome.footerBrand') }}</span><span>{{ $t('welcome.footerNote')
      }}</span></footer>
  </main>
</template>
