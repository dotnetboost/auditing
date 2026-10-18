# DotNetBoost.Auditing

[![NuGet](https://img.shields.io/nuget/vpre/DotNetBoost.Auditing.Core?label=nuget)](https://www.nuget.org/packages/DotNetBoost.Auditing.Core)
[![CI](https://github.com/dotnetboost/auditing/actions/workflows/ci.yml/badge.svg)](https://github.com/dotnetboost/auditing/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/dotnetboost/auditing/blob/main/LICENSE)

**Who changed what, when, and from what to what — for every entity in the application, in two
tables.**

No column on your entities, no table per entity type, and no migration when you start auditing
something new. The trail outlives the row it describes: nothing cascades to it, so a deleted
entity's history is still there afterwards — which is the entry people actually come looking for.

```csharp
await audit.For(product).UpdatedAsync(
[
    new AuditChange(nameof(Product.Price), 24.99m, 19.99m)
], ct);
```

```
GET /api/audit?entityName=Product&entityId=42         -> that product's history, newest first
GET /api/audit?actor=alice@example.com&from=2026-10-01
GET /api/audit/{id}                                    -> one entry with its property changes
GET /api/audit/entities                                -> which entities are audited, and what isn't
```

The endpoints **require authorization by default** and the erasure and prune endpoints are not
mapped at all unless asked for — see [rest-api.md](docs/rest-api.md).

Works with **.NET 8 and .NET 10**. `DotNetBoost.Auditing.Core` has **one** NuGet dependency —
`Microsoft.Extensions.DependencyInjection.Abstractions` — and no dependency on any other
DotNetBoost package.

> **Status:** in development. All five packages are built and tested — **367 tests** needing nothing
> installed, on both target frameworks, plus **168 integration tests** against **PostgreSQL 16, SQL
> Server 2022 and MongoDB 7** in containers, including the atomicity guarantee below. One contract
> suite is run against every store, the in-memory double included; see
> [testing.md](docs/testing.md), which also lists what is still not covered (concurrency, scale,
> MySQL).

> **Try it:** `dotnet run --project aspire/DotNetBoost.Auditing.AppHost` starts PostgreSQL, the
> sample API and the dashboard together — see [aspire.md](docs/aspire.md). Nothing is on NuGet, and there is no sample application yet.

---

## What it is, and what it is not

| | |
|---|---|
| **Entity change history** — who changed which property of which entity, old value → new value | **This library.** |
| **Action / request logging** — who called which endpoint, when, from where, how long it took | **Not this library.** Use OpenTelemetry, `HttpLogging` or Serilog. |

Request logging is already solved, better than a data-access library can solve it, and bundling the
two is most of why `Volo.Abp.AuditLogging` carries four tables and an HTTP dependency in the thing
that records a price change. Entity change history is the half no observability tool gives you,
because it needs the before-image of the row and the same transaction as the write.

[docs/scope.md](docs/scope.md) has the rest of what is deliberately left out — temporal
reconstruction, event sourcing, revert verbs, tamper-proofing — and why.

## Three ways a change gets recorded

**1. The EF Core interceptor** — reads the change tracker and appends the audit rows **inside the
same `SaveChanges`**, and therefore the same transaction. The change and its trail land together or
neither does.

```csharp
services.AddDbContext<AppDbContext>((sp, o) => o.UseNpgsql(cs).UseAuditing(sp));

services.AddAuditing().UseEntityFrameworkCore<AppDbContext>().Build();
// then modelBuilder.ApplyAuditing() in OnModelCreating
```

An update or a delete goes in the caller's own single save, with no transaction opened at all. A
**creation with a store-generated key** cannot: its id does not exist until the INSERT has run, so
that entry is written by a second save — and the interceptor opens a transaction around both when
the caller has none, so a failure still rolls the change back with its trail. Assign keys in code
and creations join the single-save path. [limitations.md](docs/limitations.md) has the two cases
that cannot be made atomic.

**2. Explicitly, through `IAuditRecorder`** — for Dapper applications, and for anything a change
tracker cannot see: a raw `ExecuteUpdate`, a stored procedure, a domain event.

```csharp
await audit.For(product).CreatedAsync([new AuditChange(nameof(Product.Name), null, "Widget")], ct);
await audit.For("Product", "42").DeletedAsync(ct: ct);   // no instance needed
```

The Dapper store writes when it is called rather than joining a unit of work, so **atomicity is
yours to arrange**: hand it your transaction and the guarantee is the same as EF Core's. And
**declare your entities** — everything there is addressed by name, so without
`DeclareEntitiesFrom(assembly)` the engine has no type to read `[AuditRedact]` from and a secret is
written down in full. [stores.md](docs/stores.md) covers both.

**3. From a sibling library** — `DotNetBoost.Settings` raises `ISettingWriteObserver` on every
completed write, carrying everything an entry needs. Turning one into audit rows is about forty
lines of **application** code; this library ships no bridge package and names no sibling. See
[docs/scope.md](docs/scope.md#7-how-changes-are-captured).

## What the model controls

```csharp
[AuditEntity("Person")]                 // the stored name, independent of the class name
public class PersonEntity
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";

    [AuditRedact] public string PasswordHash { get; set; } = "";   // changed, values withheld
    [AuditIgnore] public DateTime LastSeenAt { get; set; }         // never recorded at all
}

[AuditIgnore]                    // this entity is never audited
public class Heartbeat { }

[AuditRetention(Days = 90)]      // kept for 90 days, whatever the global retention says
public class SearchQuery { }
```

Redaction is applied **in the engine**, so every capture source gets it and a new one cannot forget
it — and a caller cannot opt out. An auditing library is the likeliest place in an application for a
secret to be written down in plain text and kept forever.

## Registering it

```csharp
services.AddAuditing(o => o.Retention = TimeSpan.FromDays(365))
        .UseEntityFrameworkCore<AppDbContext>()        // the store
        .DeclareEntitiesFrom(typeof(Product).Assembly) // see below
        .Build();
```

`DeclareEntitiesFrom` matters more than it looks. A write addressed by hand —
`audit.For("Product", "42")` — has no type to read `[AuditRedact]` from, and a Dapper application
addresses everything that way. Declaring the entity is what lets the engine apply the model's rules
to a write it could otherwise only take at face value.

## Serving it over HTTP

```csharp
services.AddAuditing()
        .UseEntityFrameworkCore<AppDbContext>()
        .UseHttpContextActor()        // records the signed-in user as the actor
        .Build();

app.MapAuditEndpoints();              // authorized; reads only
```

Two defaults differ from the sibling packages, on purpose. **Authorization is on** — a trail is a
map of an organisation's activity and, through `Actor`, personal data about its staff, so opting out
has to be written down (`o.RequireAuthorization = false`). And **the destructive endpoints do not
exist** unless asked for (`o.MapErasure`, `o.MapPrune`): a verb that deletes a trail is not
something to publish because a library happened to offer it.

`UseHttpContextActor()` reads the `NameIdentifier` claim rather than the display name — a name can
be changed by the person it names, which is the wrong key for a record of who acted.

[rest-api.md](docs/rest-api.md) has every parameter, the response shape and every status code.

## Reads are always bounded

There is no "read it all" verb. `Take` is clamped to `MaxPageSize`, and an unset `Take` becomes
`DefaultPageSize` rather than everything:

```csharp
var page = await reader.HistoryAsync(product, skip: 0, take: 50, ct);
// page.Total is null unless the query asked for a count — null means NOT COUNTED, never zero.
```

The predecessor in this suite shipped an unbounded history call over a table that only grows, with
no paging on its endpoint. That is on the short list of why it was deleted.

## Retention, because this table only grows

Nothing else in the suite has this problem, and nothing prunes on its own — there is no hosted
service and no timer, because the one irreversible thing the library does should be something an
application schedules.

```csharp
await pruner.PruneAsync(batchSize: 1000, ct);              // honours the configured retentions
await pruner.PruneOlderThanAsync(TimeSpan.FromDays(30), ct: ct);
await pruner.EraseForAsync(user, ct);                      // the erasure path for a deletion request
await pruner.EraseForActorAsync("alice@example.com", ct);
```

An entity with a longer `[AuditRetention]` than the global default survives the global sweep. That
takes a deliberate exclusion in the prune, and without it a per-entity retention would be silently
the opposite of what it says.

## It stands alone

No package reference to `DotNetBoost.Settings`, `.Media` or `.MultiLingual`, no shared kernel, and
none of their names in the source. It installs into an application that has none of them.

What it does share is a **convention**, implemented independently: **DotNetBoost Entity Addressing
v1**. All three libraries resolve entity names and format keys the same way and each publishes the
same constant, so an application can assert the versions match at startup instead of discovering a
mismatch as rows that stopped joining:

```csharp
Assert.Equal(
    DotNetBoost.Auditing.Core.Metadata.EntityKey.AddressingVersion,
    DotNetBoost.Media.Core.Metadata.EntityKey.AddressingVersion);
```

That one equality is the whole of the relationship, and it is what lets an application join an audit
entry to a media row or a translation row on `(EntityName, EntityId)`.

## Packages

| Package | |
|---|---|
| `DotNetBoost.Auditing.Core` | **Built.** Engine, contracts, attributes, addressing |
| `DotNetBoost.Auditing.EntityFrameworkCore` | **Built.** The interceptor, the store, the model |
| `DotNetBoost.Auditing.Dapper` | **Built.** Store only; SQL Server, PostgreSQL, SQLite |
| `DotNetBoost.Auditing.MongoDb` | **Built.** One collection, changes embedded |
| `DotNetBoost.Auditing.API` | **Built.** Minimal-API endpoints, `UseHttpContextActor()` |

## Documentation

- [Aspire](docs/aspire.md) — one command for the whole stack
- [Dashboard](docs/dashboard.md) — the Nuxt client
- [Scope](docs/scope.md) — what this is, what it refuses to be, and the decisions behind both
- [Stores](docs/stores.md) — EF Core, Dapper and MongoDB, and when each becomes durable
- [Testing](docs/testing.md) — the two suites, and the three defects containers found that SQLite could not
- [REST API](docs/rest-api.md) — every endpoint, parameter and status code
- [Limitations](docs/limitations.md) — what it cannot do, and what to do instead
- [Changelog](CHANGELOG.md)

## License

MIT.
