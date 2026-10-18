import type { AuditEntitiesResponse } from '~/types'

/**
 * Reads `GET /api/audit/entities` — which entities the engine knows about.
 *
 * Shared across components by its fetch key, so the entity filter and anything else asking the
 * same question make one request between them. The list cannot change while the API is running:
 * it is the set of declared entities, fixed at startup.
 *
 * **It can legitimately be empty, and the UI must survive that.** An application that never called
 * `DeclareEntitiesFrom(...)` records a perfectly good trail and declares nothing, and the endpoints
 * can be turned off outright with `MapDiscovery = false` — in which case this 404s. Either way the
 * answer is "no inventory to offer", never "no entities are audited", so callers fall back to
 * asking the user to type a name rather than presenting an empty list as the truth.
 */
export function useAuditEntities() {
  const { data, status, error } = useFetch<AuditEntitiesResponse>('/api/audit/entities', {
    key: 'audit-entities',
    default: () => ({ maxPageSize: 0, entities: [] }),
    // A 404 is "discovery is off", which is a configuration, not a failure to report. The list
    // stays empty and the callers' fallback does the rest.
    getCachedData: (key, nuxtApp) => nuxtApp.payload.data[key] ?? nuxtApp.static.data[key]
  })

  const entities = computed(() => data.value?.entities ?? [])

  return {
    entities,
    /**
     * The API's own page bound, or 0 before it has answered — and 0 when discovery is off, which
     * is why callers fall back to the configured one rather than clamping to nothing.
     */
    maxPageSize: computed(() => data.value?.maxPageSize ?? 0),
    /** Whether there is an inventory to offer. False when nothing is declared or discovery is off. */
    available: computed(() => entities.value.length > 0),
    byName: computed(() => new Map(entities.value.map(e => [e.name, e]))),
    loading: computed(() => status.value === 'pending'),
    error
  }
}
