<script setup lang="ts">
import type { AuditPage } from '~/types'

// One page of the newest entries, counted, plus a count per action. Four small reads rather than
// one big one: the API has no aggregate endpoint, deliberately — a trail is read by filter, and an
// overview is a handful of filtered counts.
const { data: recent, status } = useFetch<AuditPage>('/api/audit', {
  query: { take: 8, includeTotal: 'true' },
  default: () => ({ entries: [], total: null, skip: 0, take: 0 })
})

const counts = await Promise.all((['Created', 'Updated', 'Deleted'] as const).map(async (action) => {
  const page = await $fetch<AuditPage>('/api/audit', {
    query: { action, take: 1, includeTotal: 'true' }
  }).catch(() => null)

  return { action, total: page?.total ?? null }
}))

const loading = computed(() => status.value === 'pending')
</script>

<template>
  <UDashboardPanel id="overview">
    <template #header>
      <UDashboardNavbar title="Overview">
        <template #leading>
          <UDashboardSidebarCollapse />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <div class="space-y-6">
        <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <UCard>
            <div class="text-sm text-muted">
              Entries
            </div>
            <div class="text-2xl font-semibold mt-1">
              {{ recent?.total ?? '—' }}
            </div>
          </UCard>

          <UCard v-for="count in counts" :key="count.action">
            <div class="flex items-center gap-2">
              <AuditActionBadge :action="count.action" />
            </div>
            <div class="text-2xl font-semibold mt-1">
              {{ count.total ?? '—' }}
            </div>
          </UCard>
        </div>

        <UCard>
          <template #header>
            <div class="flex items-center justify-between">
              <h2 class="font-medium">
                Latest
              </h2>
              <UButton
                to="/audit"
                variant="link"
                trailing-icon="i-lucide-arrow-right"
                size="sm"
              >
                The whole trail
              </UButton>
            </div>
          </template>

          <AuditEntryTable :entries="recent?.entries ?? []" :loading="loading" @select="() => {}" />
        </UCard>

        <UCard>
          <template #header>
            <h2 class="font-medium">
              What this is
            </h2>
          </template>

          <div class="prose prose-sm dark:prose-invert max-w-none">
            <p>
              Every row here was written by whichever sample this dashboard is pointed at, and there
              is one per store. <code>samples/SampleApp</code> records from an EF Core interceptor,
              inside the same <code>SaveChanges</code> as the change it describes, so nothing in its
              endpoints mentions auditing at all; <code>samples/SampleApp.Dapper</code> and
              <code>samples/SampleApp.MongoDb</code> write every entry by hand, because neither has
              a change tracker to read. The declaration is the same either way: three attributes on
              <code>Product</code>.
            </p>
            <p>
              Try it: change a product's price and watch one entry appear with one property in it.
              Read a product and watch <em>nothing</em> appear — the view counter is
              <code>[AuditIgnore]</code>. Look at a supplier cost and see it recorded as changed with
              its values withheld, because it is <code>[AuditRedact]</code>.
            </p>
          </div>
        </UCard>
      </div>
    </template>
  </UDashboardPanel>
</template>
