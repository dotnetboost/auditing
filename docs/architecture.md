# Architecture

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Your application                                                        │
│  db.SaveChanges() — plain EF Core            IAuditManager               │
└──────────────┬───────────────────────────────────────┬───────────────────┘
               │                                       │
    AuditingSaveChangesInterceptor            For(entity) / For(name, id)
    (reads the change tracker, appends        RecordAsync · HistoryAsync
     inside the caller's own save)            QueryAsync · PruneAsync · Erase…
               │                                       │
               └──────────────────┬────────────────────┘
                                  │
              AuditRecorder   ·   AuditReader   ·   AuditPruner
              (the rules)         (the clamp)      (the cutoffs)
                                  │
                              IAuditStore
                   (EF Core / Dapper / MongoDB / your own)
                                  │
                   AuditEntries          AuditPropertyChanges
          (EntityName, EntityId,       (PropertyName, OldValue,
           Action, Actor, OccurredAt,   NewValue, IsRedacted,
           CorrelationId, Metadata)     IsTruncated)
```

`IAuditManager` is the one service application code injects — recording, reading and retention on a
single interface, as `ISettingManager`, `IMediaManager` and `IMultiLingualManager` are in the sibling
libraries. Behind it the engine is three internal classes that take different dependencies and have
nothing to say to each other: `AuditRecorder` holds the model's rules, `AuditReader` the page clamp,
`AuditPruner` the retention cutoffs. `AuditManager` forwards to them with no logic of its own, so
there is exactly one implementation of redaction and exactly one of the clamp.
[scope.md §10](scope.md#10-one-manager-and-what-it-cost) has what that single surface cost, and it
is not nothing.

**The rules are applied in the engine, never in a capture source.** `[AuditRedact]`, `[AuditIgnore]`,
opt-in mode, the kill switch and value truncation all live in `AuditRecorder`, which every source
goes through — the EF Core interceptor, an explicit `For(entity)` call, a future bridge from a
sibling library. A new capture source therefore cannot forget them, and a caller cannot opt out of
them by addressing a write itself. For an auditing library that is the whole game: it is the
likeliest place in an application for a secret to be written down in plain text and kept for years.

**What the engine declines to do is reported, not just returned.** A suppressed write returns
`null`, which the EF Core interceptor has no caller to hand back — so `AuditRecorder` also tells
`IAuditObserver`, with one of five reasons. The interceptor reports the skips it decides for itself
before the engine is reached, taking the reason from `AuditRecorder.SuppressionFor` so the rule
still lives in one place; the `IsAudited` the discovery endpoint calls stays silent, because
reporting from there would describe an HTTP read as a suppressed write. The default observer does
nothing and the `ILogger` one lives in the API package, which is how Core keeps its single NuGet
dependency while still having something to say
([limitations.md](limitations.md#nothing-tells-you-what-was-not-recorded-unless-you-ask)).

`AuditPlan` works out once per type which properties are recorded and under which rule, cached per
type and read through `AuditSchema.Describe`. `AuditEntityNames` and `EntityKey` resolve the
`(EntityName, EntityId)` pair that every entry is filed against — Entity Addressing v1, the same
spec `.Media` and `.MultiLingual` implement, so an application can join their rows to these
([scope.md §4](scope.md#4-entity-addressing-v1)). `AuditModelRegistry` is what `DeclareEntitiesFrom`
fills: the name → type map that lets the engine apply a type's rules to a write addressed by name,
and lets a prune find a per-entity `[AuditRetention]`.

**`IAuditStore` is deliberately dumb, and has no update verb.** Seven methods — `AppendAsync`,
`AppendRangeAsync`, `GetAsync`, `QueryAsync`, `DeleteOlderThanAsync`, `DeleteForEntityAsync`,
`DeleteForActorAsync` — with no validation, no redaction and no clamping, so a store inherits every
rule by having none of its own. There is no `UpdateAsync` and there never will be: an entry that can
be edited is not an entry. `DeleteOlderThanAsync` takes an `AuditPruneScope` rather than a nullable
entity name, because an entity with a longer retention than the global default has to be *excluded*
from the global sweep — without that, `[AuditRetention]` would silently mean the opposite of what it
says.

**Where durability differs between stores is the one thing the contract cannot hide.** The EF Core
store joins the caller's `SaveChanges`, so an appended entry becomes durable when the application
saves; the Dapper and MongoDB stores write when they are called. The conformance suite makes that
difference a `FlushAsync` hook each store implements rather than a difference in behaviour nobody
wrote down — see [stores.md](stores.md) and
[limitations.md](limitations.md#an-explicit-entry-is-durable-when-you-save).

## Packages

| Package | Depends on | Contents |
|---|---|---|
| `…Auditing.Core` | `Microsoft.Extensions.DependencyInjection.Abstractions` — nothing else | `IAuditManager`, the engine, the attributes, addressing, `IAuditStore`. No EF Core, no ASP.NET Core |
| `…Auditing.EntityFrameworkCore` | Core, `Microsoft.EntityFrameworkCore{,.Relational}` | The save interceptor, the store, the two tables, a key accessor that reads the model |
| `…Auditing.Dapper` | Core, `Dapper`, `Microsoft.Extensions.{Hosting,Logging}.Abstractions` | The same two tables by hand, DDL per dialect, transaction-scoped writes, the optional schema-creating hosted service |
| `…Auditing.MongoDb` | Core, `MongoDB.Driver`, the same two abstractions | One collection with the changes embedded, index creation, session-scoped writes |
| `…Auditing.API` | Core, `FrameworkReference` on `Microsoft.AspNetCore.App` | The `/api/audit` endpoints, the discovery endpoints, `UseHttpContextActor()` |

All five target `net8.0` and `net10.0`, and no package references another DotNetBoost library —
there is no shared kernel and none of their names appear in the source. What is shared is the
addressing spec, implemented independently and asserted by a test.

The engine classes behind `IAuditManager` are `internal`. Everything a store needs is public, so a
store does not have to live in this repository: implement `IAuditStore`, register it with
`.UseCustomStore<T>()`, and inherit `StoreConformance` to prove it behaves like the ones that ship.

## Repository layout

```
dotnetboost-auditing/
├── src/                                          # Library projects (each = 1 NuGet package)
│   ├── DotNetBoost.Auditing.Core/
│   ├── DotNetBoost.Auditing.EntityFrameworkCore/
│   ├── DotNetBoost.Auditing.Dapper/
│   ├── DotNetBoost.Auditing.MongoDb/
│   └── DotNetBoost.Auditing.API/
├── tests/
│   ├── DotNetBoost.Auditing.UnitTests/           # Engine rules, over the in-memory store
│   ├── DotNetBoost.Auditing.ProviderTests/       # Store contracts + the interceptor, on SQLite
│   ├── DotNetBoost.Auditing.ApiTests/            # Endpoints and authorization over TestServer
│   ├── DotNetBoost.Auditing.IntegrationTests/    # The same contracts on real engines (needs Docker)
│   └── DotNetBoost.Auditing.TestSupport/         # InMemoryAuditStore + the two conformance suites
├── samples/
│   ├── SampleApp/                                # EF Core + PostgreSQL; the interceptor doing the work
│   ├── SampleApp.Dapper/                         # The same API, every entry written by hand
│   └── SampleApp.MongoDb/                        # The same API again, one document per entry
├── clients/
│   └── dashboard/                                # Nuxt 4 trail browser
├── aspire/
│   ├── DotNetBoost.Auditing.AppHost/             # Both databases, all three APIs, the dashboard
│   └── DotNetBoost.Auditing.ServiceDefaults/     # OTel, health checks, service discovery
├── docs/
├── .github/workflows/ci.yml
├── Directory.Build.props                         # Nullable, warnings-as-errors, analyzers
├── Directory.Packages.props                      # Central package version management
├── src/Directory.Build.props                     # net8.0;net10.0 and XML docs, for shipped packages
├── DotNetBoost.Auditing.sln
├── aspire.config.json                            # Points `aspire run` at the AppHost
├── CHANGELOG.md
└── README.md
```

The three samples serve the same routes over the same two entities — the DTOs and the actor accessor
are literally the same files, compiled into all three — so the dashboard can be pointed at any of
them by changing one line in `AppHost.cs`. That is the clearest evidence that the store is invisible
from outside, and the three `POST /api/products/{id}/fail` endpoints are the clearest evidence of
where it is not.

## Testing

```bash
dotnet test                                              # everything (integration tests need Docker)
dotnet test tests/DotNetBoost.Auditing.UnitTests         # engine rules, in-memory store
dotnet test tests/DotNetBoost.Auditing.ProviderTests     # store contracts + interceptor, on SQLite
dotnet test tests/DotNetBoost.Auditing.ApiTests          # endpoints, authorization, wire shapes
dotnet test tests/DotNetBoost.Auditing.IntegrationTests  # PostgreSQL, SQL Server, MongoDB — needs Docker
dotnet test --collect:"XPlat Code Coverage"              # with coverage
```

Two suites in `TestSupport` are inherited rather than copied:

- **`StoreConformance`** — 28 contract tests, run once per store and engine, including over
  `InMemoryAuditStore` so the double the engine's own tests are built on is held to the same
  standard as a database. A new store implements four hooks (`PrepareAsync`, `OpenAsync`,
  `FlushAsync`, `CountChangesAsync`) and inherits the contract.
- **`EntityAddressingConformance`** — 10 tests an application derives from to prove two DotNetBoost
  libraries address entities identically, rather than trusting that they do.

The integration suite **fails without Docker rather than skipping**, because a container suite that
skips silently reports green and the stores it exists to prove would go on looking tested.
[testing.md](testing.md) has what each suite proves, the three defects containers found that SQLite
could not, and what is still not covered.

Warnings are errors (`TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`), nullable
reference types are on everywhere, and every public member of a shipped package needs XML docs
(`GenerateDocumentationFile`).
