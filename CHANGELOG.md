# Changelog

All notable changes to this project are documented here.
This project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

The first work on the library. All five packages are built and tested: 388 tests on net8.0 and
net10.0 needing nothing installed, plus 168 integration tests against PostgreSQL, SQL Server and
MongoDB in containers.

### Added
- **`DotNetBoost.Auditing.Core`** — the engine and its contracts.
  - `IAuditManager` — the one service application code injects: `For(entity)` / `For(name, id)`
    with `Created`/`Updated`/`Deleted` verbs, reads that are always clamped to a page, and
    retention and erasure. One interface, as in the suite's other three libraries; `scope.md` §10
    says what the single surface cost. `IAuditStore` for the store packages to implement.
  - `AuditEntry` + `AuditPropertyChange`: two tables, one row per property that changed, no
    foreign key to the application's own tables.
  - `[AuditEntity]`, `[Audited]`, `[AuditIgnore]`, `[AuditRedact]`, `[AuditRetention]`.
  - `AuditSchema.Describe(type)` — the properties of an audited entity and their rules, read from
    `AuditPlan` and cached per type. The rule for *which* properties count lives there too, so a
    description and the recorder cannot drift apart.
  - `AuditDiff.Between(before, after)` + `InitialValues` / `FinalValues` — the explicit path's
    stand-in for a change tracker, so a Dapper or MongoDB application reports what moved without a
    hand-written `if` per property. It leaves out the key (already the entry's address) and
    `[AuditIgnore]` properties, compares `[AuditRedact]` ones and leaves the withholding to the
    engine, and skips anything that is not a simple value — a navigation property, a collection, a
    nested object — which is documented rather than hidden, because that part is silent. Key
    discovery now lives in one place that both this and `ConventionEntityKeyAccessor` read, so the
    diff and the addressing cannot disagree about which property is the key.
  - **DotNetBoost Entity Addressing v1**, implemented independently and asserted by the test
    suite. `EntityKey.AddressingVersion` is published for a consumer to compare with
    `DotNetBoost.Media`'s and `DotNetBoost.MultiLingual`'s.
  - One NuGet dependency in total: `Microsoft.Extensions.DependencyInjection.Abstractions`.
- **Redaction applied in the engine**, not in each capture source, so no later source can forget
  it. `[AuditRedact]` records that a property changed with both values withheld, and a caller
  cannot opt out.
- **Retention that a prune actually honours.** `AuditPruneScope` exists so that an entity with a
  longer `[AuditRetention]` than the configured default survives the global sweep — without the
  exclusion, a per-entity retention would be silently the opposite of what it says.

- **`DotNetBoost.Auditing.EntityFrameworkCore`** — the capture and the store.
  - A `SaveChangesInterceptor` that records `Modified` and `Deleted` entities **in the caller's own
    save**, so the change and its trail share one transaction with nothing opened to arrange it.
  - Creations with a store-generated key are deferred to a second save, because the id does not
    exist until the INSERT has run — and the interceptor owns a transaction across both when the
    caller has none. A failure writing the deferred entry rolls the creation back with it, which is
    tested against a real database.
  - `EfCoreAuditStore`: paged reads clamped by the engine, `Id` as the ordering tiebreaker so paging
    a busy trail is stable, UTC converters so timestamps come back as UTC rather than Unspecified,
    and set-based deletes that remove the property rows explicitly rather than trusting a cascade
    the database may not be enforcing.
  - `EfCoreEntityKeyAccessor` reads keys from the model, so a fluently configured or shadow key
    works where the convention cannot see one.
  - `ApplyAuditing()` maps both tables with four indexes: the per-entity read, the actor read, the
    prune scan and the correlation lookup.
- **`DotNetBoost.Auditing.MongoDb`** — the document store, proven against MongoDB 7.
  - **One collection with the property changes embedded**, deliberately unlike the relational
    stores. Their reason for a second table was indexability, and MongoDB indexes array fields
    natively, so `Changes.PropertyName` gets a multikey index and the argument does not carry over.
    Embedding also makes one insert write a complete entry, and makes deleting an entry take its
    changes with it.
  - The entry's id is the document `_id` as a **standard** UUID, not the driver's legacy binary
    subtype, which other drivers and the shell read as a different value. `Action` is stored as its
    name rather than its number.
  - **No global class map is registered.** The driver's registry is static and process-wide, so the
    documents are this package's own attribute-annotated types rather than maps over the Core
    models — a test asserts no map exists for them.
  - `WithSession(session)` for atomicity with your own work, which needs a replica set. BSON dates
    hold milliseconds, so timestamps here are coarser than the relational stores' ticks; both are
    documented in `docs/limitations.md`.
- **`DotNetBoost.Auditing.Dapper`** — the store for applications without EF Core.
  - The same two tables in the same shape as the EF Core store, so **one database serves either** —
    proved by a test that writes with one store and reads with the other, in both directions,
    including GUID, timestamp and boolean representations on SQLite.
  - `WithTransaction(tx)`, or an `IDbTransaction` in the scope, is how a change and its trail commit
    together. Without one the entry is written when `AppendAsync` is called, which is the opposite of
    the EF Core store and is documented rather than smoothed over.
  - `DapperSchema.EnsureCreatedAsync` / `Script(dialect)` for SQL Server, PostgreSQL and SQLite, with
    the column widths taken from the same Core constants the EF Core configuration uses, so the two
    cannot drift. Only ever CREATEs.
  - **The store converts the identity columns itself.** SQLite has no GUID type and returns the
    `TEXT` it stored, and Dapper cannot convert a string to a `Guid` because `Guid` is not
    `IConvertible` — it misses the fallback that gets `DateTime` and `bool` across the same gap. The
    alternative, a global `SqlMapper` type handler, would change how every `Guid` in the host
    application is read, so the store reads those columns as they come instead.
  - Timestamps are stamped `DateTimeKind.Utc` on the way out, matching the EF Core store's value
    converter, and the property rows are deleted explicitly rather than trusting a cascade SQLite
    does not enforce by default.
- **`DotNetBoost.Auditing.API`** — the HTTP surface.
  - `MapAuditEndpoints()`: a filtered, paged read and a single-entry read, plus opt-in erasure and
    prune endpoints. Every read is bounded by the engine and the response reports the page size it
    actually served, so a client that asked for more finds out.
  - **Authorization is required by default**, unlike the sibling packages, which map anonymously and
    leave it to the application. A trail says who changed what and when; the failure mode of getting
    that wrong is a quiet disclosure rather than a broken feature, so opting out
    (`RequireAuthorization = false`) has to be written down.
  - **The erasure and prune endpoints are not mapped at all** unless asked for — absent rather than
    merely secured. `DestructivePolicy` is required on top of `Policy` rather than instead of it, so
    it is impossible to configure a destructive endpoint that is easier to reach than the reads.
  - **Discovery endpoints.** `GET /api/audit/entities` lists the declared entities and
    `GET /api/audit/entities/{entityName}` describes one entity's properties and the rule each
    falls under — `Record`, `Redact` or `Ignore`. Neither returns anything recorded, both sit
    inside the read group so the same authorization covers them, and `MapDiscovery = false` turns
    them off. Without them a client filtering by `entityName` is filtering by a name somebody typed
    out of the source, and which properties are redacted — the one thing a UI has to render
    differently — can only be learned by waiting for a change to one of them. `audited` is asked of
    the engine per request rather than worked out from the attributes, so the kill switch and
    opt-in mode show up as an entity that is not audited instead of as an unexplained empty trail.
  - The entity list also publishes **`maxPageSize`**, the bound every read is clamped to. A limit
    a client has to be told out of band is one it eventually has wrong, and the dashboard held its
    own copy of this number until now.
  - The dashboard uses all of it: the entity filter is a list of the declared entities rather than
    a text box, a filtered trail shows that entity's properties with their rules beside it, and the
    page-size selector offers what the engine will actually serve. None of it is written out in the
    client, so it cannot drift from the server.
  - `UseHttpContextActor()` records the request's `NameIdentifier` claim, preferring it to the
    display name: a name can be changed by the person it names, which is the wrong key for a record
    of who acted. Outside a request, or unauthenticated, the actor stays honestly `null`.
  - `AuditAction` is serialized by name, `total` is `null` when no count was taken rather than zero,
    and a redacted value is absent from the response body — asserted against the whole body, not the
    parsed shape.
- **`AuditWrite.CorrelationId`** — a capture source can stamp the value itself. The ambient
  `Activity` is not a good enough default on its own: outside a request there is none, so every
  entry would get an id of its own; inside one there is exactly one, so two separate saves would
  look like a single edit. The interceptor stamps one value per `SaveChanges`.
- **`AuditingOptions.RecordUpdatesWithoutBeforeImage`** (default `true`) — `db.Update(entity)` on an
  entity the context never loaded leaves EF nothing to diff against, so such an entry records the
  new values with no old ones instead of recording nothing. An auditing library that silently keeps
  no trail for a common EF pattern is worse than one that keeps a partial trail and says so.

### Fixed
- **The Dapper store was broken on PostgreSQL.** Dapper rewrites `IN @Ids` into
  `IN (@Ids1, @Ids2, …)` on SQL Server and SQLite and **does not do it at all on Npgsql**, so
  PostgreSQL was handed `IN $1` and refused it: every read that loaded property changes, and every
  delete, failed. Nothing in the C# says which engine is underneath, so the SQLite unit suite passed
  it cheerfully — the integration suite found it on its first run. `AuditStatements.AnyOf` and
  `NoneOf` now spell the predicate per dialect, using PostgreSQL's own `= ANY(@Ids)` / `<> ALL(@Ids)`,
  which is also one parameter rather than one per id and so has no 2100-parameter ceiling.

### Added — testing
- **`DotNetBoost.Auditing.IntegrationTests`** — 168 tests against **PostgreSQL 16, SQL Server 2022
  and MongoDB 7 (as a replica set)** in Testcontainers.
  - A **conformance suite**: one set of contract tests inherited once per store and engine, so all
    five combinations are shown answering the same questions the same way. The one place the stores
    legitimately differ — when an appended entry becomes durable — is a `FlushAsync` hook each
    subclass implements, so the difference is written down rather than hidden.
  - The interceptor's atomicity guarantee against PostgreSQL rather than SQLite, including forcing
    the deferred audit write to fail after the row's INSERT succeeded and asserting the row is gone.
  - Both relational DDL scripts **executed**, with their indexes, cascade and column widths asserted
    against each server's own catalogue — they had previously been checked by searching their text.
  - Cross-store compatibility on PostgreSQL, whose real `uuid` and `timestamptz` types the two
    stores reach by different routes.
  - MongoDB indexes read back from the server, the multikey query the embedding decision rests on,
    and `WithSession` committing and aborting a transaction.
  - **It fails without Docker rather than skipping**, because a container suite that skips silently
    reports green and the stores it exists to prove would go on looking tested.
- CI runs it as a job of its own, and a release now waits on it.
- **The test projects now follow the suite's layout**, organised by kind of test rather than by
  package: `TestSupport`, `UnitTests`, `ProviderTests`, `ApiTests`, `IntegrationTests` — the same
  five `DotNetBoost.Settings`, `.Media` and `.MultiLingual` use. Three per-package projects became
  one `ProviderTests`, which is what lets the stores be tested against each other.
  - **`InMemoryAuditStore` in `TestSupport` replaces the hand-written fake, and sits the contract
    suite.** The Core engine's tests are built on it, and a test built on a double is only worth
    something if the double behaves like the real thing. The old fake was held to no standard at
    all; one engine test turned out to be relying on it being unrealistic, and said so the moment
    the double started copying on write the way a database does.
  - `StoreConformance` moved to `TestSupport`, so the same 28 contract tests run over SQLite with
    nothing installed *and* over the three real engines in containers.
  - **`EntityAddressingConformance`** — the Entity Addressing v1 suite as an abstract class an
    application derives from to prove two DotNetBoost libraries address entities identically. The
    agreement had rested on three implementations happening to match, with no test anywhere.

### Added — running it
- **`samples/SampleApp`** — a product catalogue that keeps a complete trail, where the interesting
  thing is how little of it is about auditing: three attributes on `Product`, two lines at startup,
  and nothing in any endpoint. Each endpoint demonstrates one thing — the deferred key on a
  creation, only-what-moved on an update, an `[AuditIgnore]` counter that records nothing, a trail
  that outlives the row it describes, and a set-based `ExecuteUpdate` the change tracker never sees
  recorded explicitly through `IAuditManager` — rounded in the statement *and* in the entry, by the
  same rule, so the recorded new value is exactly what the column ends up holding.
  - **No authentication and no authorization.** `SystemActorAccessor.cs` records every entry
    against `"system"`, and `MapAuditEndpoints` is opened with `RequireAuthorization = false`
    because its default is *on*. Both are documented as nothing to copy: a constant actor is a lie
    dressed as an answer in any application that has users — which is why the library's own default
    returns `null` — and the opt-out serves the whole trail to anyone who can reach the port.
- **`samples/SampleApp.Dapper`** — the same API over the Dapper store. The same routes, the same
  request and response bodies (literally the same DTO files) and the same two entities, so
  `clients/dashboard` can be pointed at either; what differs is that **every entry is written by
  hand**, because there is no change tracker to read.
  - The before-image comes from a `SELECT` in the `PUT`, which is the other half of what the
    explicit path costs: a blind `UPDATE` is one round trip cheaper and records nothing worth
    reading.
  - `Data/UnitOfWork.cs` puts an `IDbTransaction` in the request scope, which `UseDapper(...)`
    resolves when it builds its store — so a row's `UPDATE` and its entry's `INSERT`s commit
    together with nothing in the endpoints saying so. `POST /api/products/{id}/fail` writes both and
    throws, and leaves **neither** behind.
  - `DeclareEntitiesFrom` is not optional here, and the sample says why in three places: redaction
    for writes addressed by name, `StockCheck`'s own retention for the pruner, and the discovery
    endpoint the dashboard's model page reads.
- **`samples/SampleApp.MongoDb`** — the same API again over the MongoDB store, with the same
  explicit recording almost line for line: the explicit path costs the same whatever the engine.
  - An entry is **one document** with its property changes embedded, so a single insert writes a
    complete entry and a prune takes the changes with it.
  - The atomicity window is **shown rather than hidden**. The AppHost runs a standalone `mongod`,
    where a transaction is unavailable, so `POST /api/products/{id}/fail` changes a price, commits
    it, and throws before recording — leaving a price that moved and a trail that says it did not.
    That is the explicit path's real failure mode; the README has the one-node replica set and the
    `WithSession` that close it.
- **`aspire/`** — an AppHost that starts PostgreSQL, MongoDB, all three sample APIs and the
  dashboard together and injects every address, plus the shared ServiceDefaults. Each sample's trail
  lives in the **same** database as that sample's own data, which is the design rather than a
  convenience: it is what lets a change and its entry commit in one transaction. The Dapper sample
  takes a second database on the same PostgreSQL server, so pgweb reaches both and the two schemas
  can be compared — they are identical, which is exactly the claim that store makes.
- **`clients/dashboard`** — a Nuxt 4 trail browser: filter by entity, key, action, actor and window;
  page through; open an entry; read one product's history beside the product.
  - The audit endpoints require authorization, so the dashboard needs an identity — and
    `server/api/[...path].ts` attaches it **server-side**. An identity shipped to the browser would
    let anyone with the page read the whole trail.
  - The UI distinguishes the three absences a trail viewer usually conflates: a redacted value
    (withheld on purpose), a `null` (never stored), and `total: null` (not counted, which is not
    zero — paging falls back to "there might be more").

### Notes
- The library depends on no other DotNetBoost package and names none of them. Composition with
  Settings, Media and MultiLingual is application code, worked out in `dotnetboost-sample`.
- Scope, and what is deliberately left out, is in [docs/scope.md](docs/scope.md); what the library
  cannot do is in [docs/limitations.md](docs/limitations.md).
