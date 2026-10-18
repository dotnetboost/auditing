<script setup lang="ts">
import type { AuditEntry } from '~/types'

const { action } = defineProps<{ action: AuditEntry['action'] }>()

// Created and Deleted are the two an eye should catch across a page of Updated rows — a creation
// because it is the start of a history, a deletion because it is the only copy of what was there.
const colour = computed(() => ({
  Created: 'success',
  Updated: 'info',
  Deleted: 'error'
}[action] ?? 'neutral') as 'success' | 'info' | 'error' | 'neutral')

const icon = computed(() => ({
  Created: 'i-lucide-plus',
  Updated: 'i-lucide-pencil',
  Deleted: 'i-lucide-trash-2'
}[action] ?? 'i-lucide-circle'))
</script>

<template>
  <UBadge
    :color="colour"
    :icon="icon"
    variant="subtle"
    size="sm"
  >
    {{ action }}
  </UBadge>
</template>
