/** `AuditChangeResponse` from DotNetBoost.Auditing.API. */
export interface AuditChange {
  propertyName: string
  oldValue: string | null
  newValue: string | null
  /**
   * The model marks the property sensitive, so the two nulls mean "not disclosed" rather than
   * "was empty". The UI has to say which — a blank would be a lie.
   */
  isRedacted: boolean
  /** Either value was cut short at the API's configured limit. */
  isTruncated: boolean
}

/** `AuditEntryResponse` from DotNetBoost.Auditing.API. */
export interface AuditEntry {
  id: string
  entityName: string
  entityId: string
  /** `Created`, `Updated` or `Deleted` — a name, not a number. */
  action: 'Created' | 'Updated' | 'Deleted'
  /** `null` means the actor was never captured. It is not a claim about anybody. */
  actor: string | null
  correlationId: string
  occurredAt: string
  metadata: string | null
  changes: AuditChange[]
}

/** `AuditPageResponse` from DotNetBoost.Auditing.API. */
export interface AuditPage {
  entries: AuditEntry[]
  /** `null` means NOT COUNTED, never zero — do not paginate against it. */
  total: number | null
  skip: number
  /** The page size actually served, after the engine's clamp. */
  take: number
}

/** The filters `GET /api/audit` accepts. */
export interface AuditFilters {
  entityName?: string
  entityId?: string
  action?: string
  actor?: string
  actorMissing?: boolean
  correlationId?: string
  from?: string
  to?: string
}

/** A product from the sample API. */
export interface Product {
  id: number
  name: string
  price: number
  category: string
  supplierCost: number
  viewCount: number
}

/** RFC 9457 problem details, as the API answers a bad request with. */
export interface ProblemDetails {
  title?: string
  detail?: string
  status?: number
  errors?: Record<string, string[]>
}
