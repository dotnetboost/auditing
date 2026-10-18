import type { AuditFilters, AuditPage } from '~/types'

/**
 * Reads `GET /api/audit`.
 *
 * Paging is **offset-based with an opt-in count**, because that is what the API offers and why: the
 * total is a second pass over the index, so it is requested only when something is going to show
 * it. `total` comes back `null` when it was not counted, which is not zero — the UI has to tell
 * those apart or it will claim an empty trail.
 */
export function useAuditTrail() {
  const configured = useRuntimeConfig().public.maxPageSize
  const { maxPageSize: published } = useAuditEntities()

  // The API publishes its own bound, so the dashboard asks rather than holds a copy. The
  // configured number stays as the fallback for the moment before the API has answered, and for an
  // API with discovery turned off — not as a second source of truth.
  const maxPageSize = computed(() => published.value || configured)

  const filters = ref<AuditFilters>({})
  const page = ref(1)
  const pageSize = ref(25)
  const includeTotal = ref(true)

  const query = computed(() => {
    const q: Record<string, string> = {
      skip: String((page.value - 1) * pageSize.value),
      take: String(Math.min(pageSize.value, maxPageSize.value)),
      includeTotal: String(includeTotal.value)
    }

    const f = filters.value

    // Only what was actually set. An empty `?actor=` is not a filter — the API reads a blank
    // parameter as absent, and sending one anyway would make "any actor" look like a choice.
    if (f.entityName) q.entityName = f.entityName
    if (f.entityId) q.entityId = f.entityId
    if (f.action) q.action = f.action
    if (f.actor) q.actor = f.actor
    if (f.actorMissing) q.actorMissing = 'true'
    if (f.correlationId) q.correlationId = f.correlationId
    if (f.from) q.from = new Date(f.from).toISOString()
    if (f.to) q.to = new Date(f.to).toISOString()

    return q
  })

  const { data, status, error, refresh } = useFetch<AuditPage>('/api/audit', {
    query,
    default: () => ({ entries: [], total: null, skip: 0, take: 0 })
  })

  // Any change to the filters starts again from the first page: page 3 of a different trail is a
  // different question, and silently showing it is how a filter looks broken.
  watch(filters, () => {
    page.value = 1
  }, { deep: true })

  watch(pageSize, () => {
    page.value = 1
  })

  const reset = () => {
    filters.value = {}
    page.value = 1
  }

  return {
    filters,
    page,
    pageSize,
    includeTotal,
    entries: computed(() => data.value?.entries ?? []),
    total: computed(() => data.value?.total ?? null),
    served: computed(() => data.value?.take ?? 0),
    maxPageSize,
    status,
    error,
    refresh,
    reset
  }
}
