# Configuration reference

Every option in one place. The quick start in the [README](../README.md) uses four of them; this
page is for when you need the fifth.

## `AddAuditing()` builder methods

Chain them after `services.AddAuditing()` and finish with `.Build()`, which validates the options,
reads the declared entities and refuses a configuration with no store.

| Method | Package | Description |
|---|---|---|
| `.UseEntityFrameworkCore<TContext>()` | EntityFrameworkCore | The store, and the interceptor. `TContext` is any `DbContext` that calls `ApplyAuditing()` in `OnModelCreating` |
| `.UseDapper(connectionFactory, migrateSchema, configure)` | Dapper | The store, over an `IDbConnection` the factory opens; `migrateSchema: true` creates the two tables at startup if they are absent |
| `.UseMongoDb(configure)` | MongoDb | The store, over the `IMongoDatabase` your application registers |
| `.UseMongoDb(connectionString, databaseName, configure)` | MongoDb | The same, over a client this package owns |
| `.UseMongoDb(databaseFactory, configure)` | MongoDb | The same, one database per tenant |
| `.UseCustomStore<TStore>()` | Core | A store of your own. Registered scoped |
| `.UseCustomStore(factory)` | Core | The same, from a factory |
| `.DeclareEntitiesFrom(assembly)` | Core | Declares every public class in `assembly` carrying `[AuditEntity]`, `[Audited]` or `[AuditRetention]` |
| `.DeclareEntity<TEntity>()` | Core | Declares one entity, attribute or not |
| `.DeclareEntities(params Type[])` | Core | Declares several |
| `.IgnoreEntity<TEntity>()` | Core | Never records `TEntity` — a class-level `[AuditIgnore]` for a type you cannot annotate, such as MultiLingual's `Translation`. Wins over `[Audited]` and `RequireOptIn`. Not `Ignore`, which in EF Core means "do not map" |
| `.IgnoreEntities(params Type[])` | Core | Ignores several |
| `.WithRetention(TimeSpan)` | Core | Same as `AddAuditing(o => o.Retention = …)`. Deletes nothing by itself |
| `.WithPageSize(defaultSize, maxSize)` | Core | Both page sizes at once |
| `.WithMaxValueLength(characters)` | Core | Override the 4000-character value limit |
| `.RequireOptIn()` | Core | Record only entities marked `[Audited]` |
| `.WithoutCreatedAndDeletedValues()` | Core | Record that a row appeared or went, without its values |
| `.DisableRecording()` | Core | The kill switch. Reads still work; every record call returns `null` |
| `.UseActorAccessor<TAccessor>()` | Core | Replace how the actor is read. Registered **scoped** — an accessor worth having reads something per request |
| `.UseEntityKeyAccessor<TAccessor>()` | Core | Replace how entity keys are read. Both EF Core providers register one that reads the model instead |
| `.UseObserver<TObserver>()` | Core | Be told what the engine decides **not** to record. Registered **singleton**; the default does nothing |
| `.UseHttpContextActor()` | API | The actor from the request's `NameIdentifier` claim; `null` outside a request or unauthenticated |
| `.UseLoggingObserver()` | API | Logs what was not recorded — warnings for the two suppressions that are usually a misconfiguration, Debug for the rest |
| `AuditingTelemetry.MeterName` | Core | Not a builder method: the meter to pass to `AddMeter(…)` for the three counters. See [Observability](observability.md) |
| `.Build()` | Core | Validate, declare, return `IServiceCollection` |

**One store per application.** A second `Use…` store call throws and names the first: entries
written to one are invisible to the other, and a trail split across two stores is not a trail.

`Build()` also refuses two combinations the setters cannot catch on their own —
`DefaultPageSize` above `MaxPageSize`, and `MaxValueLength` above `AuditingOptions.MaxValueCeiling`
(1 MB of UTF-16) — two declared entities claiming one stored name, and an `IgnoreEntity` type that
is also declared or shares a declared entity's stored name.

## `AuditingOptions`

Set through `AddAuditing(o => …)` or the builder methods above. Registered as a singleton. Each
setter rejects its own invalid value with `ArgumentOutOfRangeException`.

| Property | Default | Description |
|---|---|---|
| `Enabled` | `true` | Whether anything is recorded. `false` leaves the services resolvable and returns `null` from every record call; **reads are unaffected**, because turning recording off must not hide the trail already collected |
| `RequireOptIn` | `false` | Whether an entity must carry `[Audited]` to be recorded. Off, because the useful default for an application that installs an auditing library is that it audits |
| `RecordCreatedValues` | `true` | Whether a `Created` entry carries the initial values |
| `RecordDeletedValues` | `true` | Whether a `Deleted` entry carries the final values. The setting to think hardest about before turning off: after the row is gone this is the only copy |
| `RecordUpdatesWithoutBeforeImage` | `true` | Whether to record an update that arrives with no before-image — `db.Update(entity)` on an entity this context never loaded. Such an entry lists every property with no old values; see [limitations](limitations.md#dbupdateentity-has-no-before-image) |
| `MaxValueLength` | `4000` | Where a recorded value is cut short and `IsTruncated` set. Capped at `MaxValueCeiling` |
| `DefaultPageSize` | `50` | The page a read uses when `AuditQuery.Take` is unset |
| `MaxPageSize` | `200` | The largest page a read can ask for. A larger `Take` is **clamped**, not refused |
| `Retention` | `null` | How long entries are kept where the entity has no `[AuditRetention]`. `null` keeps everything, and setting it deletes nothing until a prune runs |

Read-only: `AuditingOptions.MaxValueCeiling` (1 MB) and `AuditingOptions.ActorMaxLength` (256, an
alias of `AuditColumns.ActorMaxLength`, for a store building its own schema).

## The model: attributes

| Attribute | On | Effect |
|---|---|---|
| `[AuditEntity("Name")]` | class | The stored entity name, independent of the class name. At most 128 characters |
| `[Audited]` | class | Audited when `RequireOptIn` is on. Ignored otherwise |
| `[AuditIgnore]` | class | Never audited, whatever else is configured |
| `[AuditIgnore]` | property | Never recorded at all. An update of only ignored properties records nothing |
| `[AuditRedact]` | property | Recorded as **changed**, with both values withheld. Applied in the engine, so no capture source can forget it and a caller cannot opt out |
| `[AuditRetention(Days = 90)]` | class | This entity's entries are kept for 90 days, whatever `Retention` says — and a global prune excludes them rather than taking them first |

An entity addressed **by name** has no type for these to be read from, which is what
`DeclareEntitiesFrom` is for. Undeclared, a redacted property is recorded in full.

## Entity Framework Core

| Call | Description |
|---|---|
| `options.UseAuditing(sp)` | Attach the interceptor. Use the `(sp, options)` overload of `AddDbContext`. Without it the tables are still mapped and `IAuditManager` still works, but nothing records itself |
| `modelBuilder.ApplyAuditing(configure)` | Map `AuditEntries` and `AuditPropertyChanges` — four indexes on the entries (the per-entity read, the actor read, the prune scan, the correlation lookup) and, by default, one on `PropertyName`. Nothing is added to your own entities |

`AuditingModelOptions` (the `configure` argument): `EntriesTable` (`AuditEntries`), `ChangesTable`
(`AuditPropertyChanges`), `Schema` (the model's default), `IndexPropertyName` (`true` — the index
behind "every change anyone made to this property").

**Not compatible with `AddDbContextPool`**: pooled options are built once from the root provider and
cannot carry the per-request services the capture needs to know who is acting.

## Dapper

`DapperOptions`: `EntriesTable` (`AuditEntries`), `ChangesTable` (`AuditPropertyChanges`), `Schema`
(`null`), `Dialect` (`null` — inferred from the connection type; set it when the connection is a
wrapper).

| Member | Description |
|---|---|
| `DapperSchema.Script(dialect, options)` | The DDL for SQL Server, PostgreSQL or SQLite |
| `DapperSchema.EnsureCreatedAsync(connection, options, ct)` | Run it. Only ever creates — it will not migrate a table whose shape has changed |
| `store.WithTransaction(tx)` | The store enlisted in your transaction, so a row and its entry commit together |

An `IDbTransaction` in the request scope is resolved by the store the container builds, which is how
the Dapper sample pairs them without saying so in its endpoints. See [stores.md](stores.md).

## MongoDB

`MongoDbOptions`: `EntriesCollection` (`auditEntries`), `CreateIndexes` (`true`).

| Member | Description |
|---|---|
| `store.WithSession(session)` | The store inside your transaction. Needs a replica set |

The property changes are embedded in the entry document rather than kept in a second collection, so
one insert writes a complete entry. See [stores.md](stores.md).

## ASP.NET Core

| Call | Description |
|---|---|
| `app.MapAuditEndpoints(configure)` | The endpoints under `/api/audit`. Returns the route group, so an application can add its own metadata, filters or authorization — which then covers every endpoint in it |
| `app.MapAuditEndpoints(prefix, configure)` | The same under a prefix of your own, e.g. `/admin/audit` |

`AuditEndpointOptions`:

| Property | Default | Description |
|---|---|---|
| `RequireAuthorization` | **`true`** | Unlike the sibling packages. A trail is a map of an organisation's activity and, through `Actor`, personal data about its staff, so opting out has to be written down |
| `Policy` | `null` | A policy to require instead of mere authentication. Ignored when `RequireAuthorization` is off |
| `MapErasure` | **`false`** | `DELETE {prefix}/entity/…` and `DELETE {prefix}/actor/…`. Absent rather than merely secured |
| `MapPrune` | **`false`** | `POST {prefix}/prune`. Most applications should schedule `PruneAsync` instead |
| `MapDiscovery` | `true` | `GET {prefix}/entities` and `GET {prefix}/entities/{entityName}`. Neither returns anything recorded |
| `DestructivePolicy` | `null` | An **additional** policy for the destructive endpoints, required on top of `Policy` rather than instead of it — so a destructive endpoint can never be easier to reach than the reads |

Every parameter, response shape and status code is in [rest-api.md](rest-api.md).

## Other public helpers

| Member | Description |
|---|---|
| `AuditDiff.Between(before, after)`, `InitialValues(e)`, `FinalValues(e)` | What moved, for the explicit path. Simple values only — see [limitations](limitations.md#auditdiff-compares-simple-values-only) |
| `AuditSchema.Describe(type)`, `AuditableProperties(type)` | An entity's properties and the rule each falls under |
| `AuditPlan.For(type)`, `AuditPlan.Of(entity)` | The cached per-type plan the recorder reads |
| `AuditEntityNames.For(type)`, `Of(entity)`, `MaxLength` | How a stored entity name is resolved, and its 128-character limit |
| `EntityKey.ToKeyString(key)`, `IsUnset(value)`, `MaxLength`, `AddressingVersion` | Key formatting, and the addressing version to assert against a sibling library's — see [scope.md §4](scope.md#4-entity-addressing-v1) |
| `AuditColumns.ActorMaxLength`, `CorrelationIdMaxLength`, `PropertyNameMaxLength` | The stored widths, for a store or a UI that validates before writing |
| `IAuditActorAccessor`, `IEntityKeyAccessor`, `IAuditStore` | The three seams. The default actor accessor returns `null`, honestly, until one is registered |
