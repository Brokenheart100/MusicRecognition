<script setup lang="ts">
import type { OpenSheetMusicDisplay } from 'opensheetmusicdisplay'
const props=defineProps<{ blob: Blob | null }>()
const host=ref<HTMLElement>(), error=ref(''), busy=ref(false)
let display: OpenSheetMusicDisplay | undefined
watch([()=>props.blob,host],async ([blob,element],_old,onCleanup)=>{
  let active=true;onCleanup(()=>{active=false}); display?.clear();display=undefined
  if(!blob || !element)return
  busy.value=true;error.value=''
  try {
    const {OpenSheetMusicDisplay}=await import('opensheetmusicdisplay')
    if(!active)return
    const instance=new OpenSheetMusicDisplay(element,{backend:'svg',autoResize:true,drawingParameters:'default',drawTitle:false,drawSubtitle:false})
    await instance.load(blob)
    if(!active){instance.clear();return}
    display=instance;display.render()
  } catch {if(active)error.value='网页预览暂不可用，可以下载 MusicXML 到记谱软件继续校对，或查看正式 PDF。'}
  finally{if(active)busy.value=false}
},{immediate:true,flush:'post'})
onBeforeUnmount(()=>display?.clear())
</script>
<template><div class="fragment-xml"><p v-if="busy" role="status">正在生成 MusicXML 预览…</p><p v-if="error" role="alert">{{error}}</p><div ref="host" aria-label="拼谱 MusicXML 预览" /></div></template>
<style scoped>.fragment-xml{overflow:auto;max-height:75dvh;background:white;min-width:0;padding:18px}.fragment-xml>div{min-width:640px}.fragment-xml p{color:#667085;font-size:12px}</style>
