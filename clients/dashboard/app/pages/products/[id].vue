<script setup lang="ts">
import type { AuditEntry, AuditPage, Product } from '~/types'

const route = useRoute()
const toast = useToast()
const id = computed(() => Number(route.params.id))

const { data: product, refresh: refreshProduct } = useFetch<Product>(() => `/api/products/${id.value}`)

const { data: history, refresh: refreshHistory, status } = useFetch<AuditPage>(
  () => `/api/products/${id.value}/history`,
  { default: () => ({ entries: [], total: null, skip: 0, take: 0 }) }
)

const price = ref<number | null>(null)

watch(product, (p) => {
  price.value = p?.price ?? null
}, { immediate: true })

const save = async () => {
  try {
    await $fetch(`/api/products/${id.value}`, { method: 'PUT', body: { price: price.value } })
    await Promise.all([refreshProduct(), refreshHistory()])
    toast.add({ title: 'Saved', description: 'One entry, one property.', color: 'success' })
  } catch (error) {
    toast.add({ title: 'Could not save', description: problemMessage(error), color: 'error' })
  }
}

const selected = ref<AuditEntry | null>(null)
const open = ref(false)

const select = (entry: AuditEntry) => {
  selected.value = entry
  open.value = true
}
</script>

<template>
  <UDashboardPanel id="product">
    <template #header>
      <UDashboardNavbar :title="product?.name ?? 'Product'">
        <template #leading>
          <UDashboardSidebarCollapse />
        </template>

        <template #right>
          <UButton
            to="/products"
            icon="i-lucide-arrow-left"
            color="neutral"
            variant="ghost"
          >
            All products
          </UButton>
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <div class="space-y-6">
        <UCard v-if="product">
          <template #header>
            <h2 class="font-medium">
              Change the price
            </h2>
          </template>

          <div class="flex items-end gap-3">
            <UFormField label="Price" class="w-40">
              <UInput v-model.number="price" type="number" step="0.01" />
            </UFormField>

            <UButton :disabled="price === product.price" @click="save">
              Save
            </UButton>
          </div>

          <p class="text-sm text-muted mt-3">
            Only what actually moved is recorded, and that takes a before-image. The EF Core sample
            reads one from its change tracker; the Dapper and MongoDB samples load the row first,
            because nothing else has one. A write with no before-image records every property with
            no old values — see <code>docs/limitations.md</code>.
          </p>
        </UCard>

        <!--
          Read from the API, not written out here: the three attributes on Product are facts the
          engine already publishes, and a hand-written copy of them in the dashboard would be wrong
          the first time somebody changed one.
        -->
        <AuditEntityModel name="Product" />

        <UCard :ui="{ body: 'p-0 sm:p-0' }">
          <template #header>
            <div class="flex items-center justify-between">
              <h2 class="font-medium">
                History
              </h2>
              <UButton
                :to="`/audit?entityName=Product&entityId=${id}`"
                variant="link"
                trailing-icon="i-lucide-arrow-right"
                size="sm"
              >
                Open in the trail
              </UButton>
            </div>
          </template>

          <AuditEntryTable
            :entries="history?.entries ?? []"
            :loading="status === 'pending'"
            @select="select"
          />
        </UCard>

        <UAlert
          v-if="!product"
          color="info"
          variant="subtle"
          icon="i-lucide-info"
          title="This product is gone"
          description="Its history is still here. Nothing cascades to the trail, so a deleted entity's
                       entries — including the Deleted one, with its final values — outlive it."
        />
      </div>
    </template>
  </UDashboardPanel>

  <AuditDetailSlideover v-model:open="open" :entry="selected" />
</template>
