<script setup lang="ts">
// 全局"确认/取消"小弹窗的界面。业务代码只要调用状态仓库的 ask()，
// 就会弹出这个框；用户点哪个按钮，结果就回传给当初发起询问的地方。
// 它自己不写业务逻辑，只负责显示标题、说明和两个按钮。
const store = useConfirmStore()
</script>

<template>
<!-- 是否显示、标题和说明都来自共享状态仓库，任何页面发起询问都用这一个框。 -->
<!-- 用户点关闭按钮或点遮罩时也按"取消"处理，避免发起方一直等不到答复。 -->
<!-- 关闭动画播完再清空内容，防止文字在收起过程中突然消失。 -->
  <UModal
    :open="store.open"
    :title="store.request?.title"
    :description="store.request?.description"
    :ui="{ footer: 'justify-end' }"
    @update:open="value => { if (!value) store.answer(false) }"
    @after:leave="store.clear"
  >
    <template #footer>
<!-- 左边灰色按钮固定是取消；右边确认按钮的文字可以由发起询问的页面指定。 -->
      <UButton color="neutral" variant="ghost" :label="$t('common.cancel')" @click="store.answer(false)" />
      <UButton color="primary" :label="store.request?.confirmLabel ?? $t('common.continue')" @click="store.answer(true)" />
    </template>
  </UModal>
</template>
