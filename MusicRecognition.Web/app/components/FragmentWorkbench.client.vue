<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import type { FragmentCrop, FragmentSelection } from '~~/shared/api/generated/types.gen'
const store=useFragmentsStore(), server=useFragmentProject(), cache=useQueryClient()
const fileInput=ref<HTMLInputElement>(), cropOpen=ref(false), cropIndex=ref(0), dragIndex=ref(-1)
const localError=ref(''), tab=ref<'musicxml'|'pdf'>('musicxml')
const view=computed(()=>server.query.data.value)
const running=computed(()=>!!view.value && ['Queued','Processing','Cancelling'].includes(view.value.state))
const busy=computed(()=>running.value || server.create.isPending.value || server.revise.isPending.value || server.command.isPending.value)
const editable=computed(()=>!busy.value && !store.historical && (!store.active || ['Ready','Failed','Cancelled'].includes(view.value?.state ?? '')))
const sources=computed(()=>store.active ? view.value?.sources ?? [] : store.files.map((file,index)=>({index,fileName:file.name})))
const sourceName=(index:number)=>sources.value.find(s=>s.index===index)?.fileName ?? `片段 ${index+1}`
const versionItems=computed(()=>[{label:'最新版本',value:0},...Array.from({length:store.latest},(_,i)=>({label:`版本 ${store.latest-i}`,value:store.latest-i}))])
const displayError=(value:unknown)=> {
  const e=value as {detail?:string;message?:string;title?:string}|undefined
  return e?.detail ?? e?.message ?? e?.title ?? '操作暂未完成，请重试。'
}
const error=computed(()=>localError.value || (server.query.error.value ? displayError(server.query.error.value):''))
const prefix=['fragment-previews',crypto.randomUUID()]
const imageQuery=useQuery(computed(()=>({queryKey:[...prefix,'images',store.active?.id,sources.value.length],enabled:!!store.active && sources.value.length>0,
  queryFn:async()=>Promise.all(sources.value.map(async s=>({index:s.index,blob:await server.image(s.index)}))),staleTime:Infinity,retry:1})))
const urls=ref<Record<number,string>>({})
watch([()=>store.files,()=>imageQuery.data.value,()=>store.active?.id],()=>{
  Object.values(urls.value).forEach(URL.revokeObjectURL)
  urls.value=Object.fromEntries(store.active ? (imageQuery.data.value ?? []).map(i=>[i.index,URL.createObjectURL(i.blob)]) : store.files.map((file,index)=>[index,URL.createObjectURL(file)]))
},{immediate:true})
const xmlQuery=useQuery(computed(()=>({queryKey:[...prefix,'xml',store.active?.id,store.version],enabled:view.value?.state==='Ready',queryFn:({signal}:{signal:AbortSignal})=>server.artifact('score.musicxml',signal),staleTime:Infinity,retry:1})))
const pdfQuery=useQuery(computed(()=>({queryKey:[...prefix,'pdf',store.active?.id,store.version],enabled:tab.value==='pdf' && view.value?.state==='Ready' && !!view.value.plan.reviewConfirmed,
  queryFn:({signal}:{signal:AbortSignal})=>server.artifact('score.pdf',signal),staleTime:Infinity,retry:1})))
const pdfUrl=ref('')
watch(pdfQuery.data,blob=>{if(pdfUrl.value)URL.revokeObjectURL(pdfUrl.value);pdfUrl.value=blob?URL.createObjectURL(blob):''})
let pending: {identity:string;token:string;key:string}|undefined
function patch(index:number,values:Partial<FragmentSelection>){const fragment=store.draft.fragments[index];if(fragment)Object.assign(fragment,values);store.draft.reviewConfirmed=false}
function title(value:string|number){store.draft.title=String(value);store.draft.reviewConfirmed=false}
function addFiles(event:Event){
  const input=event.target as HTMLInputElement,files=[...(input.files??[])]
  if(files.some(f=>!/^image\/(png|jpeg)$/.test(f.type)) || store.files.length+files.length>12 || [...store.files,...files].reduce((n,f)=>n+f.size,0)>20*1024*1024){localError.value='请选择 PNG/JPG 图片，最多 12 张，合计不超过 20 MB。';input.value='';return}
  localError.value='';store.add(files);input.value=''
}
async function submit(){
  localError.value=''
  try{
    if(store.active){await server.revise.mutateAsync();return}
    const identity=JSON.stringify([store.draft,store.files.map(f=>[f.name,f.size,f.lastModified])])
    if(pending?.identity!==identity)pending={identity,token:crypto.randomUUID()+crypto.randomUUID(),key:crypto.randomUUID()}
    await server.create.mutateAsync(pending!)
  }catch(e){localError.value=displayError(e)}
}
function beginCrop(index:number){cropIndex.value=index;cropOpen.value=true}
function applyCrop(value:FragmentCrop|null){patch(cropIndex.value,{crop:value});cropOpen.value=false}
async function download(name:string){
  try{const blob=await server.artifact(name),url=URL.createObjectURL(blob),link=document.createElement('a');link.href=url;link.download=`${view.value?.plan.title ?? store.draft.title}-v${store.version}.${name.split('.').at(-1)}`;link.click();setTimeout(()=>URL.revokeObjectURL(url),1000)}
  catch(e){localError.value=displayError(e)}
}
async function command(action:'retry'|'cancel'){try{await server.command.mutateAsync(action)}catch(e){localError.value=displayError(e)}}
function start(){if((store.files.length || store.dirty && store.active) && !confirm('新建项目后，当前已上传项目仍保留在最近记录中；尚未上传的图片需重新选择。继续吗？'))return;pending=undefined;store.start();tab.value='musicxml';localError.value=''}
onBeforeUnmount(()=>{Object.values(urls.value).forEach(URL.revokeObjectURL);if(pdfUrl.value)URL.revokeObjectURL(pdfUrl.value);void cache.cancelQueries({queryKey:prefix});cache.removeQueries({queryKey:prefix})})
</script>

<template>
  <div class="fragment-workbench">
    <header class="fragment-header glass"><NuxtLink to="/" class="fragment-brand"><img src="/favicon.svg" width="32" height="32" alt=""> 谱间</NuxtLink>
      <span class="fragment-header-label">片段拼谱 <small>FRAGMENT STUDIO</small></span><UButton to="/" label="原谱工作台" color="neutral" variant="ghost" /><UButton label="新建拼谱" icon="i-lucide-plus" color="neutral" variant="soft" :disabled="busy" @click="start" /></header>
    <main class="fragment-main">
      <div class="fragment-heading"><p>BRING THE PIECES TOGETHER</p><h1>把旋律，一段段收好。</h1><span>添加同一编配的完整谱面片段，按顺序识别、校对并生成 MusicXML 与 PDF。</span></div>
      <div v-if="error" role="alert" class="fragment-error">{{error}}</div>
      <div class="fragment-columns">
        <section class="fragment-editor glass" aria-label="片段整理">
          <div class="fragment-section-title"><h2>整理你的片段</h2><span>{{store.draft.fragments.length}} / 12</span></div>
          <UFormField label="乐谱标题"><UInput :model-value="store.draft.title" :maxlength="120" aria-label="拼谱标题" class="w-full" :disabled="!editable" @update:model-value="title" /></UFormField>
          <input ref="fileInput" type="file" accept="image/png,image/jpeg" multiple class="sr-only" aria-label="添加谱面图片" @change="addFiles">
          <UButton v-if="!store.active" label="添加图片片段" icon="i-lucide-images" color="neutral" variant="soft" class="fragment-add" :disabled="busy" @click="fileInput?.click()" />
          <p class="fragment-hint">最多 12 张图片、合计 20 MB。拖动卡片或使用箭头调整顺序；默认只自动去除明确的黑边。</p>
          <div v-if="!store.draft.fragments.length" class="fragment-empty">从视频截图或乐谱片段开始。<br>请保留完整小节、谱号与上下谱表。</div>
          <ol class="fragment-list">
            <li v-for="(fragment,index) in store.draft.fragments" :key="fragment.sourceIndex" :draggable="editable" @dragstart="dragIndex=index" @dragover.prevent @drop.prevent="store.move(dragIndex,index);dragIndex=-1">
              <div class="fragment-card-head"><strong>{{String(index+1).padStart(2,'0')}}</strong><span :title="sourceName(fragment.sourceIndex)">{{sourceName(fragment.sourceIndex)}}</span>
                <UButton icon="i-lucide-arrow-up" :aria-label="`片段 ${index+1} 上移`" size="xs" color="neutral" variant="ghost" :disabled="!editable || index===0" @click="store.move(index,index-1)" />
                <UButton icon="i-lucide-arrow-down" :aria-label="`片段 ${index+1} 下移`" size="xs" color="neutral" variant="ghost" :disabled="!editable || index===store.draft.fragments.length-1" @click="store.move(index,index+1)" />
              </div>
              <img v-if="urls[fragment.sourceIndex]" :src="urls[fragment.sourceIndex]" :alt="`片段 ${index+1} 原图`" class="fragment-thumb">
              <div class="fragment-card-actions"><span>{{fragment.crop?'已手动框选':'自动去黑边'}}</span><UButton label="框选谱面" icon="i-lucide-crop" size="xs" color="neutral" variant="soft" :disabled="!editable || !urls[fragment.sourceIndex]" @click="beginCrop(index)" />
                <UButton icon="i-lucide-x" :aria-label="`移除片段 ${index+1}`" size="xs" color="neutral" variant="ghost" :disabled="!editable || store.draft.fragments.length<2" @click="store.remove(index)" /></div>
              <div class="fragment-range"><UFormField label="从本片段第几小节"><UInput type="number" :min="1" :max="5000" :model-value="fragment.startMeasure" :aria-label="`片段 ${index+1} 起始小节`" :disabled="!editable" @update:model-value="patch(index,{startMeasure:Number($event)})" /></UFormField>
                <UFormField label="到第几小节（留空至末尾）"><UInput type="number" :min="fragment.startMeasure" :max="5000" :model-value="fragment.endMeasure==null?'':String(fragment.endMeasure)" :aria-label="`片段 ${index+1} 结束小节`" :disabled="!editable" @update:model-value="patch(index,{endMeasure:$event===''?null:Number($event)})" /></UFormField></div>
            </li>
          </ol>
          <UCheckbox :model-value="store.draft.reviewConfirmed" label="我已确认片段顺序与保留范围，了解识谱草稿仍需逐音校对" :disabled="!editable" @update:model-value="store.draft.reviewConfirmed=$event===true" />
          <p class="fragment-hint">先识别可查看每张片段的小节数和校对提示。确认后保存新版本，即可下载 PDF。小节范围用于去掉重复截图，不会自动补齐缺失音符。</p>
          <UButton :label="store.active?'应用修改并生成新版本':'识别并拼接'" icon="i-lucide-sparkles" size="lg" class="w-full" :loading="busy" :disabled="!editable || !store.draft.fragments.length || !!store.active && !store.dirty" @click="submit" />
          <UButton v-if="!store.historical && (view?.state==='Failed' || view?.state==='Cancelled')" label="重试拼谱" class="mt-3" color="neutral" variant="soft" @click="command('retry')" />
          <UButton v-if="running && !store.version" label="取消识别" class="mt-3" color="neutral" variant="ghost" @click="command('cancel')" />
          <div v-if="store.recent.length" class="fragment-recent"><h3>最近的拼谱</h3><UButton v-for="session in store.recent" :key="session.id" :label="session.title" color="neutral" variant="ghost" class="w-full justify-start" :disabled="busy" @click="store.open(session)" /></div>
        </section>
        <section class="fragment-result glass" aria-label="拼谱结果">
          <div class="fragment-section-title"><h2>整份乐谱</h2><USelect v-if="store.latest" v-model="store.selectedVersion" :items="versionItems" aria-label="拼谱版本" class="w-36 shrink-0" :disabled="busy" /></div>
          <div v-if="!view" class="fragment-result-empty"><UIcon name="i-lucide-music-4" /><h3>让片段，成为一份乐谱。</h3><p>识别完成后，可以在这里查看 MusicXML、对照原图并下载。</p></div>
          <template v-else>
            <div class="fragment-status" role="status"><UIcon :name="running?'i-lucide-loader-circle':'i-lucide-file-music'" :class="{spin:running}" /><span>{{view.state==='Failed'?'拼谱暂未完成':view.state==='Cancelled'?'拼谱已取消':view.stage}}</span><small v-if="store.historical">历史版本，只读</small></div>
            <p v-if="view.errorCode" role="alert" class="fragment-error">{{view.errorMessage || '请检查裁剪与小节范围，或查看服务器诊断日志。'}}（{{view.errorCode}}）</p>
            <template v-if="view.result && view.state==='Ready'">
              <div class="fragment-stats"><span><strong>{{view.result.measureCount}}</strong> 小节</span><span><strong>{{view.result.noteCount}}</strong> 已识别音符</span><span><strong>{{view.result.pdfPages}}</strong> PDF 页</span></div>
              <div class="fragment-review"><strong>请对照原图校对</strong><p v-for="warning in view.result.warnings" :key="warning">{{warning}}</p></div>
              <ul class="fragment-summary"><li v-for="part in view.result.fragments" :key="part.index">片段 {{part.index+1}}：识别 {{part.measureCount}} 小节，保留第 {{part.startMeasure}}–{{part.endMeasure}} 小节</li></ul>
              <div class="fragment-downloads"><UButton label="下载 MusicXML" icon="i-lucide-download" color="neutral" variant="soft" @click="download('score.musicxml')" /><UButton label="下载 MXL" color="neutral" variant="ghost" @click="download('score.mxl')" />
                <UButton label="下载重排 PDF" icon="i-lucide-file-down" :disabled="!view.plan.reviewConfirmed || store.dirty" @click="download('score.pdf')" /></div>
              <p v-if="!view.plan.reviewConfirmed || store.dirty" class="fragment-hint">确认左侧顺序与校对提示并生成新版本后，可下载该版本的 PDF。</p>
              <div class="fragment-tabs"><UButton label="MusicXML 预览" :variant="tab==='musicxml'?'soft':'ghost'" color="neutral" @click="tab='musicxml'" /><UButton label="PDF 预览" :variant="tab==='pdf'?'soft':'ghost'" color="neutral" :disabled="!view.plan.reviewConfirmed" @click="tab='pdf'" /></div>
              <p class="fragment-hint">这里是保存版本 v{{view.version}} 的识谱重排草稿，尚未包含简谱数字。网页预览与 PDF 可能有不同的分页。需要修改音符、时值或声部时，请下载 MusicXML 到记谱软件继续校对。</p>
              <FragmentScorePreview v-if="tab==='musicxml'" :blob="xmlQuery.data.value ?? null" />
              <iframe v-else-if="pdfUrl" :src="pdfUrl" title="拼谱 PDF 预览" class="fragment-pdf" />
              <p v-if="xmlQuery.error.value || pdfQuery.error.value" role="alert" class="fragment-error">预览加载失败，请重新打开项目或下载文件。</p>
            </template>
          </template>
        </section>
      </div>
    </main>
    <UModal v-model:open="cropOpen" title="框选有效谱面" description="保留完整小节及必要的音乐上下文。" :ui="{content:'max-w-4xl'}"><template #body><FragmentCrop v-if="cropOpen && store.draft.fragments[cropIndex] && urls[store.draft.fragments[cropIndex]!.sourceIndex]" :key="cropIndex" :src="urls[store.draft.fragments[cropIndex]!.sourceIndex]!" :initial="store.draft.fragments[cropIndex]!.crop" @apply="applyCrop" @automatic="applyCrop(null)" /></template></UModal>
  </div>
</template>

<style scoped>
.fragment-workbench{min-height:100dvh;background:radial-gradient(ellipse at 20% 0,#e9eaf9,transparent 45%),#f5f6fa;color:#263247}.fragment-header{display:flex;align-items:center;gap:18px;padding:20px 4vw;border-bottom:1px solid #e5e8ef}.fragment-brand{display:flex;align-items:center;gap:10px;font-size:21px;font-weight:650}.fragment-header-label{margin-right:auto;font-size:14px}.fragment-header-label small{display:block;font-size:8px;letter-spacing:.13em;color:#9199ac}.fragment-main{max-width:1540px;margin:auto;padding:38px 4vw 80px}.fragment-heading{margin-bottom:30px}.fragment-heading>p{font-size:9px;letter-spacing:.18em;color:#909bb8}.fragment-heading h1{font-size:32px;font-weight:500;margin:10px 0}.fragment-heading>span{font-size:13px;color:#808b9e}.fragment-columns{display:grid;grid-template-columns:minmax(300px,400px) minmax(0,1fr);gap:24px;align-items:start}.fragment-editor,.fragment-result{border:1px solid #e4e8f2;border-radius:24px;padding:24px;min-width:0}.fragment-result{min-height:600px}.fragment-section-title{display:flex;align-items:center;justify-content:space-between;gap:10px;margin-bottom:20px}.fragment-section-title h2{font-size:16px;font-weight:600}.fragment-section-title>span{font-size:12px;color:#909bb0}.fragment-add{margin-top:16px;width:100%;justify-content:center}.fragment-hint{font-size:11px;color:#7e8a9f;line-height:1.9;margin:12px 0}.fragment-list{display:grid;gap:16px;padding:0;margin:20px 0;list-style:none}.fragment-list li{background:#fafbfe;border:1px solid #e3e7ef;border-radius:16px;overflow:hidden}.fragment-card-head{display:flex;align-items:center;gap:6px;padding:10px 12px;font-size:11px}.fragment-card-head strong{font-family:Georgia,serif;font-size:20px;color:#a3abce;margin-right:5px}.fragment-card-head>span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;flex:1;min-width:0}.fragment-thumb{display:block;width:100%;height:145px;object-fit:contain;background:#eef0f5}.fragment-card-actions{display:flex;align-items:center;gap:8px;padding:10px 12px;font-size:10px;color:#8490a6}.fragment-card-actions>span{margin-right:auto}.fragment-range{display:grid;grid-template-columns:1fr 1fr;gap:10px;padding:0 12px 14px}.fragment-empty,.fragment-result-empty{text-align:center;color:#95a0b4;font-size:12px;line-height:2;padding:55px 15px}.fragment-result-empty{padding:130px 15px}.fragment-result-empty>span{font-size:44px;color:#a8afd0}.fragment-result-empty h3{font-size:20px;font-weight:500;margin:18px 0 10px}.fragment-error{background:#fff0f1;color:#a34451;padding:12px 16px;border-radius:12px;font-size:12px;line-height:1.8;margin-bottom:16px}.fragment-status{display:flex;align-items:center;gap:10px;font-size:12px;color:#707e96}.fragment-status small{margin-left:auto}.fragment-stats{display:flex;gap:24px;border-top:1px solid #e9ecf2;border-bottom:1px solid #e9ecf2;padding:20px 0;margin:20px 0;font-size:11px;color:#8994a7}.fragment-stats strong{font-family:Georgia,serif;font-size:25px;color:#4e5b79;font-weight:400;margin-right:5px}.fragment-review{background:#fff9e9;color:#8d7953;border-radius:14px;padding:16px;font-size:11px;line-height:1.9}.fragment-review strong{font-size:12px}.fragment-review p{margin:5px 0}.fragment-summary{font-size:11px;line-height:1.9;color:#8490a6;padding-left:18px;margin:16px 0}.fragment-downloads,.fragment-tabs{display:flex;gap:8px;flex-wrap:wrap;margin:18px 0}.fragment-tabs{border-top:1px solid #e6eaf1;padding-top:18px}.fragment-pdf{width:100%;min-height:700px;border:0;border-radius:12px;background:#eef0f5}.fragment-recent{border-top:1px solid #e6eaf1;padding-top:20px;margin-top:24px}.fragment-recent h3{font-size:11px;color:#8994a8;margin-bottom:8px}@media(max-width:900px){.fragment-columns{grid-template-columns:1fr}.fragment-header-label small{display:none}.fragment-header{gap:10px;padding:15px}.fragment-header-label{font-size:12px}.fragment-main{padding:25px 16px}.fragment-heading h1{font-size:26px}.fragment-editor,.fragment-result{padding:18px;border-radius:20px}.fragment-result-empty{padding:70px 15px}.fragment-pdf{min-height:520px}}
.fragment-section-title h2{flex-shrink:0;white-space:nowrap}
</style>
