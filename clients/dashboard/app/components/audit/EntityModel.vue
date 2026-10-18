<script setup lang="ts">
import type { AuditEntitySchema } from '~/types'

/**
 * What the model says about one entity — read from `GET /api/audit/entities/{entityName}`.
 *
 * It answers the two questions a trail provokes and cannot answer itself: "why is this property
 * never here?" and "why does this one arrive with no values?". Both are facts about the class, so
 * before this the only way to learn them was to read the source — or to wait for a change to a
 * property and see what turned up.
 *
 * Keyed by name by the parent, so a different entity remounts and refetches rather than showing
 * the previous one's properties under a new heading.
 */
const { name } = defineProps<{ name: string }>()

const { data: schema, status, error } = useFetch<AuditEntitySchema>(
  () => `/api/audit/entities/${encodeURIComponent(name)}`,
  { key: `audit-entity-${name}` }
)

const rules = {
  Record: { color: 'neutral', icon: 'i-lucide-check', hint: 'Recorded with its values.' },
  Redact: { color: 'warning', icon: 'i-lucide-eye-off', hint: 'Recorded as changed, values withheld — [AuditRedact].' },
  Ignore: { color: 'neutral', icon: 'i-lucide-minus', hint: 'Never recorded, not even its name — [AuditIgnore].' }
} as const

// Ordered by how much they are worth knowing, not alphabetically: a redacted property changes how
// a reader must interpret two nulls, an ignored one explains an absence, and the rest are the
// ordinary case somebody is already expecting to see.
const order = { Redact: 0, Ignore: 1, Record: 2 } as const

const properties = computed(() =>
  [...(schema.value?.properties ?? [])].sort((a, b) =>
    order[a.rule] - order[b.rule] || a.name.localeCompare(b.name)))

const retention = computed(() => {
  const schema_ = schema.value
  if (!schema_) return null

  // null is NOT zero: nothing configured means a prune leaves these entries alone. Saying "0 days"
  // here would be the opposite of the truth.
  if (schema_.retentionDays === null) return 'Kept until something deletes them — no retention configured'

  const days = schema_.retentionDays % 1 === 0 ? schema_.retentionDays : schema_.retentionDays.toFixed(2)
  return schema_.hasOwnRetention
    ? `Kept ${days} days — its own [AuditRetention]`
    : `Kept ${days} days — the configured default`
})
</script>

<template>
  <UCard v-if="!error">
    <template #header>
      <div class="flex flex-wrap items-center gap-2">
        <UIcon name="i-lucide-shapes" class="size-4 text-dimmed" />
        <h2 class="font-medium">
          {{ name }}
        </h2>

        <!-- The class behind the stored name, where [AuditEntity("…")] made them differ. -->
        <code v-if="schema && schema.clrType !== schema.name" class="text-xs text-muted">
          {{ schema.clrType }}
        </code>

        <UBadge
          v-if="schema && !schema.audited"
          color="warning"
          variant="subtle"
          size="sm"
          icon="i-lucide-eye-off"
        >
          not recorded
        </UBadge>
      </div>
    </template>

    <div v-if="status === 'pending'" class="text-sm text-muted">
      Reading the model…
    </div>

    <div v-else-if="schema" class="space-y-3">
      <div class="flex flex-wrap gap-2">
        <UTooltip
          v-for="property in properties"
          :key="property.name"
          :text="rules[property.rule].hint"
        >
          <UBadge
            :color="rules[property.rule].color"
            :variant="property.rule === 'Record' ? 'outline' : 'subtle'"
            :icon="rules[property.rule].icon"
            :class="property.rule === 'Ignore' ? 'line-through decoration-dimmed' : undefined"
          >
            {{ property.name }}
            <span class="opacity-60 ml-1">{{ property.type }}{{ property.nullable ? '?' : '' }}</span>
          </UBadge>
        </UTooltip>
      </div>

      <p class="text-xs text-muted">
        {{ retention }}. Nothing is deleted until a prune runs.
      </p>
    </div>
  </UCard>
</template>
