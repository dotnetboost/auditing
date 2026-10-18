<script setup lang="ts">
import type { AuditChange } from '~/types'

const { changes } = defineProps<{ changes: AuditChange[] }>()
</script>

<template>
  <div v-if="!changes.length" class="text-sm text-muted italic">
    No property values recorded.
    <span class="not-italic">
      Legal and meaningful: a deletion recorded without its values says the entity went and nothing
      more, and a page can be read without them on purpose.
    </span>
  </div>

  <div v-else class="space-y-2">
    <div
      v-for="change in changes"
      :key="change.propertyName"
      class="rounded-md border border-default p-3"
    >
      <div class="flex items-center gap-2 mb-2">
        <span class="font-medium text-sm">{{ change.propertyName }}</span>

        <UBadge
          v-if="change.isRedacted"
          color="warning"
          variant="subtle"
          size="sm"
          icon="i-lucide-eye-off"
        >
          redacted
        </UBadge>

        <UBadge
          v-if="change.isTruncated"
          color="neutral"
          variant="subtle"
          size="sm"
          icon="i-lucide-scissors"
        >
          truncated
        </UBadge>
      </div>

      <div class="flex items-center gap-3 text-sm font-mono">
        <span class="text-muted line-through decoration-error/50">
          {{ formatValue(change.oldValue, change.isRedacted) }}
        </span>

        <UIcon name="i-lucide-arrow-right" class="size-4 shrink-0 text-dimmed" />

        <span class="text-highlighted">
          {{ formatValue(change.newValue, change.isRedacted) }}
        </span>
      </div>

      <p v-if="change.isRedacted" class="text-xs text-muted mt-2">
        The model marks this property sensitive, so the values were never recorded — not hidden
        here. Nothing in the database holds them.
      </p>
    </div>
  </div>
</template>
