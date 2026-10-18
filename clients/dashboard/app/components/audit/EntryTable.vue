<script setup lang="ts">
import type { AuditEntry } from '~/types'

const { entries, loading } = defineProps<{ entries: AuditEntry[], loading?: boolean }>()
const emit = defineEmits<{ select: [entry: AuditEntry] }>()
</script>

<template>
  <div class="overflow-x-auto">
    <table class="w-full text-sm">
      <thead class="text-left text-muted border-b border-default">
        <tr>
          <th class="py-2 pr-4 font-medium">
            When
          </th>
          <th class="py-2 pr-4 font-medium">
            Action
          </th>
          <th class="py-2 pr-4 font-medium">
            Entity
          </th>
          <th class="py-2 pr-4 font-medium">
            Actor
          </th>
          <th class="py-2 pr-4 font-medium">
            Changed
          </th>
          <th class="py-2 font-medium">
            Edit
          </th>
        </tr>
      </thead>

      <tbody>
        <tr
          v-for="entry in entries"
          :key="entry.id"
          class="border-b border-default/60 hover:bg-elevated/50 cursor-pointer"
          @click="emit('select', entry)"
        >
          <td class="py-2 pr-4 whitespace-nowrap">
            <span :title="formatDateTime(entry.occurredAt)">{{ formatRelative(entry.occurredAt) }}</span>
          </td>

          <td class="py-2 pr-4">
            <AuditActionBadge :action="entry.action" />
          </td>

          <td class="py-2 pr-4 font-mono text-xs whitespace-nowrap">
            {{ entry.entityName }}/{{ entry.entityId }}
          </td>

          <td class="py-2 pr-4 whitespace-nowrap">
            <span v-if="entry.actor">{{ entry.actor }}</span>
            <!-- Said, not blanked. A missing actor means nobody was captured, which is a fact
                 about the change and not an absence of one. -->
            <span v-else class="text-dimmed italic">not captured</span>
          </td>

          <td class="py-2 pr-4">
            <span v-if="!entry.changes.length" class="text-dimmed">—</span>
            <span v-else class="text-muted">
              {{ entry.changes.map(c => c.propertyName).join(', ') }}
            </span>
          </td>

          <td class="py-2 font-mono text-xs text-dimmed" :title="entry.correlationId">
            {{ shortId(entry.correlationId) }}
          </td>
        </tr>

        <tr v-if="!entries.length && !loading">
          <td colspan="6" class="py-10 text-center text-muted">
            Nothing recorded for these filters.
          </td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
