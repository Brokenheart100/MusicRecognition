<script setup lang="ts">
import Cropper from 'cropperjs'
import 'cropperjs/dist/cropper.css'
import type { FragmentCrop } from '~~/shared/api/generated/types.gen'
const props = defineProps<{ src: string; initial: FragmentCrop | null }>()
const emit = defineEmits<{ apply: [value: FragmentCrop]; automatic: [] }>()
const image = ref<HTMLImageElement>()
const ready = ref(false)
let cropper: Cropper | undefined
function initialize() {
  if (!image.value || cropper) return
  cropper = new Cropper(image.value, { viewMode: 1, autoCropArea: 1, rotatable: false, scalable: false, zoomable: false, checkOrientation: false,
    ready() {
      const size = image.value!
      if (props.initial) cropper?.setData({ x: props.initial.x*size.naturalWidth, y: props.initial.y*size.naturalHeight, width: props.initial.width*size.naturalWidth, height: props.initial.height*size.naturalHeight })
      ready.value=true
    },
  })
}
function apply() {
  const value = cropper?.getData(true), el = image.value
  if (!value || !el) return
  const x=Math.max(0,value.x), y=Math.max(0,value.y)
  emit('apply',{ x:x/el.naturalWidth, y:y/el.naturalHeight, width:Math.min(value.width,el.naturalWidth-x)/el.naturalWidth, height:Math.min(value.height,el.naturalHeight-y)/el.naturalHeight })
}
onMounted(() => { if (image.value?.complete) initialize() })
onBeforeUnmount(() => cropper?.destroy())
</script>
<template>
  <div><p class="crop-help">拖动边框，只保留需要的完整谱表。请保留谱号、拍号、八度线和小节边界。</p>
    <div class="crop-stage"><img ref="image" :src="src" alt="框选片段谱面" @load="initialize"></div>
    <div class="crop-actions"><UButton label="自动去黑边" color="neutral" variant="soft" @click="emit('automatic')" /><UButton label="应用裁剪" :disabled="!ready" @click="apply" /></div>
  </div>
</template>
<style scoped>
.crop-stage { height: min(55dvh,520px); background:#1c2332; }.crop-stage img { display:block;max-width:100%;max-height:100%; }.crop-actions{display:flex;justify-content:flex-end;gap:10px;margin-top:16px}.crop-help{font-size:12px;line-height:1.8;color:#667085;margin-bottom:12px}
</style>
