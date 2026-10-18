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

/** `AuditEntitiesResponse` from DotNetBoost.Auditing.API — the body of `GET /api/audit/entities`. */
export interface AuditEntitiesResponse {
  /**
   * The largest page a read may ask for. Published by the API so a client does not hold its own
   * copy of a server-side limit — a limit told out of band is one a client eventually has wrong.
   */
  maxPageSize: number
  entities: AuditEntityDescriptor[]
}

/** `AuditEntityDescriptor` from DotNetBoost.Auditing.API — `GET /api/audit/entities`. */
export interface AuditEntityDescriptor {
  /**
   * The name entries are STORED under, which is what `entityName` filters on. Not necessarily the
   * class name: `[AuditEntity("…")]` sets it.
   */
  name: string
  clrType: string
  /**
   * Whether a change to it would be recorded right now. The API asks its own engine, so this also
   * accounts for the kill switch and for opt-in mode — `false` with `ignored: false` is how an
   * operator finds out one of those is on, rather than by wondering why a trail is empty.
   */
  audited: boolean
  optedIn: boolean
  /** `[AuditIgnore]` on the class: never recorded, whatever else is configured. */
  ignored: boolean
  /**
   * The effective retention in days, own or configured. `null` means NOTHING is configured, so a
   * prune leaves the entries alone — never that they expire at once.
   */
  retentionDays: number | null
  /** Whether `retentionDays` is the entity's own rather than the configured default. */
  hasOwnRetention: boolean
}

/** `AuditPropertyDescriptor` from DotNetBoost.Auditing.API. */
export interface AuditPropertyDescriptor {
  /** As named on the class — the same spelling a recorded change uses for `propertyName`. */
  name: string
  type: string
  nullable: boolean
  /** `Record`, `Redact` or `Ignore` — a name, not a number, like `action`. */
  rule: 'Record' | 'Redact' | 'Ignore'
}

/** `AuditEntitySchema` from DotNetBoost.Auditing.API — `GET /api/audit/entities/{entityName}`. */
export interface AuditEntitySchema {
  name: string
  clrType: string
  audited: boolean
  retentionDays: number | null
  hasOwnRetention: boolean
  /** Every public property, the ignored ones included — listed AS ignored rather than omitted. */
  properties: AuditPropertyDescriptor[]
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
