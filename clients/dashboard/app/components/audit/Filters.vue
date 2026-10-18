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
  <UCard :ui="{ body: 'space-y-3' }">
    <!--
      A 12-column grid rather than a row of boxes with hand-picked widths: the controls then share
      edges at every width instead of reflowing into a ragged stack, and each one is as wide as its
      content needs — an entity name is long, a key is short.
    -->
    <div class="grid gap-x-3 gap-y-3 sm:grid-cols-2 lg:grid-cols-12">
      <UFormField label="Entity" class="lg:col-span-4">
        <USelect
          v-if="haveEntities"
          v-model="entityName"
          :items="entityItems"
          value-key="value"
          icon="i-lucide-box"
          class="w-full"
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
          class="w-full"
        />
      </UFormField>

      <UFormField label="Key" class="lg:col-span-2">
        <UInput v-model="filters.entityId" placeholder="42" class="w-full" />
      </UFormField>

      <UFormField label="Action" class="lg:col-span-2">
        <USelect
          v-model="action"
          :items="actions"
          value-key="value"
          class="w-full"
        />
      </UFormField>

      <UFormField label="Actor" class="lg:col-span-4">
        <UInput
          v-model="filters.actor"
          :disabled="filters.actorMissing"
          placeholder="alice@example.com"
          icon="i-lucide-user"
          class="w-full"
        />
      </UFormField>

      <UFormField label="Period" class="lg:col-span-4">
        <AuditDateRange v-model:from="filters.from" v-model:to="filters.to" />
      </UFormField>

      <div
        class="sm:col-span-2 lg:col-span-8 flex flex-wrap items-center justify-between gap-3 lg:self-end lg:pb-1"
      >
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

        <div class="flex items-center gap-2">
          <!--
            Followed from an entry's detail panel, which links the other halves of one edit. It
            narrows the trail more than anything else here and has no box of its own, so without
            this chip the page would be showing one correlation with nothing on screen saying so.
          -->
          <UTooltip v-if="filters.correlationId" :text="filters.correlationId">
            <UButton
              icon="i-lucide-link"
              trailing-icon="i-lucide-x"
              color="neutral"
              variant="subtle"
              size="sm"
              @click="filters.correlationId = undefined"
            >
              One edit · {{ shortId(filters.correlationId) }}
            </UButton>
          </UTooltip>

          <UButton
            v-if="active"
            icon="i-lucide-filter-x"
            color="neutral"
            variant="ghost"
            size="sm"
            :loading="loading"
            @click="emit('reset')"
          >
            Clear {{ active }}
          </UButton>
        </div>
      </div>
    </div>

    <div v-if="notAudited" class="flex items-start gap-2 text-xs text-warning">
      <UIcon name="i-lucide-eye-off" class="size-4 shrink-0 mt-px" />
      <p>
        <strong>{{ notAudited.name }}</strong> is not being recorded
        {{ notAudited.ignored ? 'because the class carries [AuditIgnore]' : 'under the current configuration' }}.
        Entries from before that are still here; nothing new will arrive.
      </p>
    </div>
  </UCard>
</template>
