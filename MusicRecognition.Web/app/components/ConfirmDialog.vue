<script setup lang="ts">
const store = useConfirmStore()
</script>

<template>
  <UModal
    :open="store.open"
    :title="store.request?.title"
    :description="store.request?.description"
    :ui="{ footer: 'justify-end' }"
    @update:open="value => { if (!value) store.answer(false) }"
    @after:leave="store.clear"
  >
    <template #footer>
      <UButton color="neutral" variant="ghost" :label="$t('common.cancel')" @click="store.answer(false)" />
      <UButton color="primary" :label="store.request?.confirmLabel ?? $t('common.continue')" @click="store.answer(true)" />
    </template>
  </UModal>
</template>
