<script setup lang="ts">
import type { AuditEntry } from '~/types'

const open = defineModel<boolean>('open', { required: true })
const { entry } = defineProps<{ entry: AuditEntry | null }>()

const metadata = computed(() => {
  if (!entry?.metadata) return null

  // Shown as the application stored it when it is not JSON. The library never looks inside this
  // column, and a dashboard that insisted on parsing it would break the moment somebody put
  // something else there.
  try {
    return JSON.stringify(JSON.parse(entry.metadata), null, 2)
  } catch {
    return entry.metadata
  }
})
</script>

<template>
  <USlideover v-model:open="open" :title="entry ? `${entry.entityName}/${entry.entityId}` : 'Entry'">
    <template #body>
      <div v-if="entry" class="space-y-6">
        <div class="flex items-center gap-2">
          <AuditActionBadge :action="entry.action" />
          <span class="text-sm text-muted">{{ formatDateTime(entry.occurredAt) }}</span>
        </div>

        <dl class="grid grid-cols-3 gap-y-3 text-sm">
          <dt class="text-muted">
            Actor
          </dt>
          <dd class="col-span-2">
            <span v-if="entry.actor">{{ entry.actor }}</span>
            <span v-else class="text-dimmed italic">not captured</span>
          </dd>

          <dt class="text-muted">
            Entity
          </dt>
          <dd class="col-span-2 font-mono text-xs">
            {{ entry.entityName }}/{{ entry.entityId }}
          </dd>

          <dt class="text-muted">
            Edit
          </dt>
          <dd class="col-span-2 font-mono text-xs break-all">
            {{ entry.correlationId }}
          </dd>

          <dt class="text-muted">
            Entry
          </dt>
          <dd class="col-span-2 font-mono text-xs break-all">
            {{ entry.id }}
          </dd>
        </dl>

        <div>
          <h3 class="text-sm font-medium mb-2">
            Changes
          </h3>
          <AuditChangeList :changes="entry.changes" />
        </div>

        <div v-if="metadata">
          <h3 class="text-sm font-medium mb-2">
            Metadata
          </h3>
          <pre class="rounded-md bg-elevated/50 p-3 text-xs overflow-x-auto">{{ metadata }}</pre>
        </div>

        <!-- The correlation id is the only way to see one edit as one thing, so it is a link
             rather than a value to copy by hand. -->
        <UButton
          :to="`/audit?correlationId=${entry.correlationId}`"
          icon="i-lucide-link"
          variant="subtle"
          block
          @click="open = false"
        >
          Everything else in this edit
        </UButton>
      </div>
    </template>
  </USlideover>
</template>
