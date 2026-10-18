<script setup lang="ts">
import type { AuditEntry } from '~/types'

const route = useRoute()

const {
  filters, page, pageSize, includeTotal,
  entries, total, served, maxPageSize,
  status, error, refresh, reset
} = useAuditTrail()

// Linked to from an entry's detail panel, so one edit can be opened as one thing. Watched rather
// than read once at setup: that link is usually followed from this very page, and the router
// reuses the component rather than remounting it — a one-time read would change the address bar
// and leave the trail showing everything.
watch(() => route.query, (query) => {
  if (query.correlationId) {
    filters.value.correlationId = String(query.correlationId)
  }
  if (query.entityName) {
    filters.value.entityName = String(query.entityName)
  }
  if (query.entityId) {
    filters.value.entityId = String(query.entityId)
  }
}, { immediate: true })

const selected = ref<AuditEntry | null>(null)
const open = ref(false)

const select = (entry: AuditEntry) => {
  selected.value = entry
  open.value = true
}

const sizes = [10, 25, 50, 100, 200].filter(n => n <= maxPageSize).map(n => ({ label: `${n} per page`, value: n }))

const loading = computed(() => status.value === 'pending')

// `total` is null when the count was not asked for — which is NOT zero. Paging has to fall back to
// "there might be more" rather than claiming the end of the trail.
const hasMore = computed(() =>
  total.value === null ? served.value === pageSize.value : page.value * pageSize.value < total.value)
</script>

<template>
  <UDashboardPanel id="audit">
    <template #header>
      <UDashboardNavbar title="Trail">
        <template #leading>
          <UDashboardSidebarCollapse />
        </template>

        <template #right>
          <UButton
            icon="i-lucide-refresh-cw"
            color="neutral"
            variant="ghost"
            :loading="loading"
            @click="refresh()"
          />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <div class="space-y-4">
        <AuditFilters v-model="filters" :loading="loading" @reset="reset" />

        <UAlert
          v-if="error"
          color="error"
          variant="subtle"
          icon="i-lucide-triangle-alert"
          title="The trail could not be read"
          :description="problemMessage(error)"
        />

        <UCard :ui="{ body: 'p-0 sm:p-0' }">
          <AuditEntryTable :entries="entries" :loading="loading" @select="select" />
        </UCard>

        <div class="flex flex-wrap items-center justify-between gap-3">
          <div class="flex items-center gap-3">
            <USelect
              v-model="pageSize"
              :items="sizes"
              value-key="value"
              class="w-40"
            />

            <!--
              Off is a real choice, not a nicety: the count is a second pass over the index, and a
              trail only grows. The page works without it.
            -->
            <UCheckbox v-model="includeTotal" label="Count matches" :ui="{ label: 'text-sm' }" />
          </div>

          <div class="flex items-center gap-3">
            <span class="text-sm text-muted">
              <template v-if="total !== null">
                {{ (page - 1) * pageSize + 1 }}–{{ (page - 1) * pageSize + entries.length }}
                of {{ total }}
              </template>
              <template v-else>
                Page {{ page }} · {{ entries.length }} shown · not counted
              </template>
            </span>

            <UFieldGroup>
              <UButton
                icon="i-lucide-chevron-left"
                color="neutral"
                variant="subtle"
                :disabled="page === 1 || loading"
                @click="page--"
              />
              <UButton
                icon="i-lucide-chevron-right"
                color="neutral"
                variant="subtle"
                :disabled="!hasMore || loading"
                @click="page++"
              />
            </UFieldGroup>
          </div>
        </div>
      </div>
    </template>
  </UDashboardPanel>

  <AuditDetailSlideover v-model:open="open" :entry="selected" />
</template>
