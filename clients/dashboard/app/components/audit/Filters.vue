<script setup lang="ts">
import type { AuditFilters } from '~/types'

const filters = defineModel<AuditFilters>({ required: true })

defineProps<{ loading?: boolean }>()
const emit = defineEmits<{ reset: [] }>()

// Reka's SelectItem refuses an empty-string value — an empty string is how a Select clears itself
// — so "any action" carries a sentinel instead of ''. It never leaves this component: `action`
// below maps it back to an absent filter, which is what the API reads as no filter at all.
const ANY = 'any'

// Which entities the engine knows about, so the entity filter offers the names the trail is
// actually addressed by instead of asking somebody to remember them. It can be empty — nothing
// declared, or discovery turned off — and the box falls back to free text when it is.
const { entities, available: haveEntities, byName } = useAuditEntities()

const entityItems = computed(() => {
  const items = [{ label: 'Any entity', value: ANY }]

  for (const entity of entities.value) {
    items.push({
      // The stored name is what filters, so it is what the value is. The class name is shown
      // beside it only where the two differ, which is the case [AuditEntity("…")] exists for.
      label: entity.name === entity.clrType ? entity.name : `${entity.name} · ${entity.clrType}`,
      value: entity.name
    })
  }

  // A name arriving from a link — one entry's entity, an entity nobody declared — is kept as an
  // option of its own. Dropping it would silently widen the filter the link asked for.
  const current = filters.value.entityName
  if (current && !items.some(item => item.value === current)) {
    items.push({ label: `${current} (not declared)`, value: current })
  }

  return items
})

const entityName = computed({
  get: () => filters.value.entityName || ANY,
  set: (value) => {
    filters.value.entityName = value === ANY ? undefined : value
  }
})

// Worth saying out loud: a filter that is right and a trail that is empty look identical, and this
// is the difference between "nothing has happened yet" and "nothing ever will".
const notAudited = computed(() => {
  const selected = filters.value.entityName ? byName.value.get(filters.value.entityName) : undefined
  return selected && !selected.audited ? selected : undefined
})

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
      <UFormField label="Entity" class="w-56">
        <USelect
          v-if="haveEntities"
          v-model="entityName"
          :items="entityItems"
          value-key="value"
          icon="i-lucide-box"
        />
        <!--
          No inventory to offer: the application declared no entities, or discovery is off. That is
          not "no entities are audited" — the trail is still there — so the box stays a box.
        -->
        <UInput
          v-else
          v-model="filters.entityName"
          placeholder="Product"
          icon="i-lucide-box"
        />
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

    <div v-if="notAudited" class="flex items-start gap-2 text-xs text-warning">
      <UIcon name="i-lucide-eye-off" class="size-4 shrink-0 mt-px" />
      <p>
        <strong>{{ notAudited.name }}</strong> is not being recorded
        {{ notAudited.ignored ? 'because the class carries [AuditIgnore]' : 'under the current configuration' }}.
        Entries from before that are still here; nothing new will arrive.
      </p>
    </div>
  </div>
</template>
