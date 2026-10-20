<script setup lang="ts">
import type { Product } from '~/types'

const toast = useToast()

const { data: products, status, refresh } = useFetch<Product[]>('/api/products', {
  default: () => []
})

const creating = ref(false)
const form = ref({ name: '', price: 0, category: 'tools', supplierCost: 0 })

const create = async () => {
  try {
    await $fetch('/api/products', { method: 'POST', body: form.value })
    creating.value = false
    form.value = { name: '', price: 0, category: 'tools', supplierCost: 0 }
    await refresh()
    toast.add({ title: 'Created', description: 'A Created entry is now in the trail.', color: 'success' })
  } catch (error) {
    toast.add({ title: 'Could not create', description: problemMessage(error), color: 'error' })
  }
}

const remove = async (product: Product) => {
  try {
    await $fetch(`/api/products/${product.id}`, { method: 'DELETE' })
    await refresh()
    // Worth saying out loud: this is the behaviour people are surprised by, and it is the point.
    toast.add({
      title: 'Deleted',
      description: 'The product is gone. Its trail is not — nothing cascades to it.',
      color: 'success'
    })
  } catch (error) {
    toast.add({ title: 'Could not delete', description: problemMessage(error), color: 'error' })
  }
}
</script>

<template>
  <UDashboardPanel id="products">
    <template #header>
      <UDashboardNavbar title="Products">
        <template #leading>
          <UDashboardSidebarCollapse />
        </template>

        <template #right>
          <UButton icon="i-lucide-plus" @click="creating = true">
            New
          </UButton>
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <div class="space-y-4">
        <p class="text-sm text-muted">
          The sample's own entity. Every change made here is recorded — by the interceptor or by
          hand, depending on which sample is behind this dashboard — so open a product to see its
          history alongside it.
        </p>

        <UCard :ui="{ body: 'p-0 sm:p-0' }">
          <div class="overflow-x-auto">
            <table class="w-full text-sm">
              <thead class="text-left text-muted border-b border-default">
                <tr>
                  <th class="py-2 px-4 font-medium">
                    Name
                  </th>
                  <th class="py-2 px-4 font-medium">
                    Category
                  </th>
                  <th class="py-2 px-4 font-medium">
                    Price
                  </th>
                  <th class="py-2 px-4 font-medium">
                    Views
                  </th>
                  <th class="py-2 px-4" />
                </tr>
              </thead>

              <tbody>
                <tr v-for="product in products" :key="product.id" class="border-b border-default/60">
                  <td class="py-2 px-4">
                    <ULink :to="`/products/${product.id}`" class="font-medium">{{ product.name }}</ULink>
                  </td>
                  <td class="py-2 px-4 text-muted">
                    {{ product.category }}
                  </td>
                  <td class="py-2 px-4 font-mono">
                    {{ product.price }}
                  </td>
                  <td class="py-2 px-4 text-dimmed" title="[AuditIgnore] — never recorded">
                    {{ product.viewCount }}
                  </td>
                  <td class="py-2 px-4 text-right">
                    <UButton
                      icon="i-lucide-trash-2"
                      color="error"
                      variant="ghost"
                      size="sm"
                      @click="remove(product)"
                    />
                  </td>
                </tr>

                <tr v-if="!products.length && status !== 'pending'">
                  <td colspan="5" class="py-10 text-center text-muted">
                    No products yet. Create one and watch the trail.
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </UCard>
      </div>
    </template>
  </UDashboardPanel>

  <UModal v-model:open="creating" title="New product">
    <template #body>
      <div class="space-y-4">
        <UFormField label="Name">
          <UInput v-model="form.name" class="w-full" />
        </UFormField>

        <UFormField label="Category">
          <UInput v-model="form.category" class="w-full" />
        </UFormField>

        <UFormField label="Price">
          <UInput
            v-model.number="form.price"
            type="number"
            step="0.01"
            class="w-full"
          />
        </UFormField>

        <UFormField label="Supplier cost" help="[AuditRedact] — recorded as changed, never with its value.">
          <UInput
            v-model.number="form.supplierCost"
            type="number"
            step="0.01"
            class="w-full"
          />
        </UFormField>
      </div>
    </template>

    <template #footer>
      <UButton color="neutral" variant="ghost" @click="creating = false">
        Cancel
      </UButton>
      <UButton :disabled="!form.name" @click="create">
        Create
      </UButton>
    </template>
  </UModal>
</template>
