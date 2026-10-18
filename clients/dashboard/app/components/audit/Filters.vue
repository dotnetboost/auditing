<script setup lang="ts">
import type { AuditFilters } from '~/types'

const filters = defineModel<AuditFilters>({ required: true })

defineProps<{ loading?: boolean }>()
const emit = defineEmits<{ reset: [] }>()

// Reka's SelectItem refuses an empty-string value — an empty string is how a Select clears itself
// — so "any action" carries a sentinel instead of ''. It never leaves this component: `action`
// below maps it back to an absent filter, which is what the API reads as no filter at all.
const ANY = 'any'

const actions = [
  { label: 'Any action', value: ANY },
  { label: 'Created', value: 'Created' },
  { label: 'Updated', value: 'Updated' },
  { label: 'Deleted', value: 'Deleted' }
]

const action = computed({
  get: () => filters.value.action || ANY,
  set: (value) => {
    filters.value.action = value === ANY ? undefined : value
  }
})

const active = computed(() =>
  Object.entries(filters.value).filter(([, v]) => v !== undefined && v !== '' && v !== false).length)
</script>

<template>
  <div class="space-y-3">
    <div class="flex flex-wrap items-end gap-3">
      <UFormField label="Entity" class="w-40">
        <UInput v-model="filters.entityName" placeholder="Product" icon="i-lucide-box" />
      </UFormField>

      <UFormField label="Key" class="w-28">
        <UInput v-model="filters.entityId" placeholder="42" />
      </UFormField>

      <UFormField label="Action" class="w-40">
        <USelect v-model="action" :items="actions" value-key="value" />
      </UFormField>

      <UFormField label="Actor" class="w-56">
        <UInput
          v-model="filters.actor"
          :disabled="filters.actorMissing"
          placeholder="alice@example.com"
          icon="i-lucide-user"
        />
      </UFormField>

      <UFormField label="From" class="w-44">
        <UInput v-model="filters.from" type="datetime-local" />
      </UFormField>

      <UFormField label="To" class="w-44">
        <UInput v-model="filters.to" type="datetime-local" />
      </UFormField>

      <UButton
        v-if="active"
        icon="i-lucide-x"
        color="neutral"
        variant="ghost"
        :loading="loading"
        @click="emit('reset')"
      >
        Clear {{ active }}
      </UButton>
    </div>

    <div class="flex items-center gap-4">
      <!--
        A separate control, not "leave the actor box empty". The API treats a blank `actor` as no
        filter at all, so "entries nobody is recorded for" needs a question of its own — and those
        entries are real: a background job, a migration, a webhook.
      -->
      <UCheckbox
        v-model="filters.actorMissing"
        label="Only entries with no actor recorded"
        :ui="{ label: 'text-sm' }"
      />

      <p class="text-xs text-muted">
        A window includes its start and excludes its end, so consecutive ranges never double-count.
      </p>
    </div>
  </div>
</template>
