<script setup lang="ts">
defineProps<{
  collapsed?: boolean
}>()
</script>

<template>
  <NuxtLink to="/" class="flex items-center gap-2 px-1.5 py-2 font-semibold text-highlighted">
    <UIcon name="i-lucide-images" class="size-5 shrink-0 text-primary" />

    <span v-if="!collapsed" class="truncate">
      DotNetBoost
      <span class="block text-xs font-normal text-muted">Auditing</span>
    </span>
  </NuxtLink>
</template>
