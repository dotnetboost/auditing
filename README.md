# DotNetBoost.Auditing

[![NuGet](https://img.shields.io/nuget/vpre/DotNetBoost.Auditing.Core?label=nuget)](https://www.nuget.org/packages/DotNetBoost.Auditing.Core)
[![CI](https://github.com/dotnetboost/auditing/actions/workflows/ci.yml/badge.svg)](https://github.com/dotnetboost/auditing/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**Who changed what, when, and from what to what — for every entity in your application, in two
tables.**

You add nothing to your entities. DotNetBoost.Auditing records every create, update and delete in
the same transaction as the change itself, and serves the trail back over REST. No column on your
entities, no table per entity type, and no migration when you start auditing something new.

```csharp
[AuditEntity("Product")]                 // the stored name, independent of the class name
public class Product
{
    public int Id { get; set; }
    public decimal Price { get; set; }

    [AuditRedact] public string SupplierCode { get; set; } = "";  // changed, values withheld
    [AuditIgnore] public int ViewCount { get; set; }              // never recorded at all
}
```

```
GET /api/audit?entityName=Product&entityId=42          -> that product's history, newest first
GET /api/audit?actor=alice@example.com&from=2026-10-01
GET /api/audit/{id}                                    -> one entry with its property changes
GET /api/audit/entities                                -> which entities are audited, and what isn't
```

The trail outlives the row it describes: nothing cascades to it, so a deleted entity's history is
still there afterwards — which is the entry people actually come looking for.

Works with **.NET 8 and .NET 10**, on **SQL Server, PostgreSQL, SQLite or MongoDB**.
`DotNetBoost.Auditing.Core` has **one** NuGet dependency —
`Microsoft.Extensions.DependencyInjection.Abstractions` — and no dependency on any other
DotNetBoost package.

> **Status:** in development, and **nothing is on NuGet yet** — the quick start below references the
> projects in `src/`. All five packages are built and tested: **397 tests** needing nothing
> installed, on both target frameworks, plus **178 integration tests** against **PostgreSQL 16, SQL
> Server 2022 and MongoDB 7** in containers. See [testing.md](docs/testing.md), which also lists
> what is not covered yet.

---

## Get started in 5 minutes

This builds a minimal web app that keeps its products, and their trail, in a local SQLite file.
Nothing else to install.

**1. Create a project and reference the packages**

```bash
dotnet new web -n MyApp
cd MyApp
dotnet add package Microsoft.EntityFrameworkCore.Sqlite
dotnet add reference ../dotnetboost-auditing/src/DotNetBoost.Auditing.Core
dotnet add reference ../dotnetboost-auditing/src/DotNetBoost.Auditing.EntityFrameworkCore
dotnet add reference ../dotnetboost-auditing/src/DotNetBoost.Auditing.API
```

Once the packages are published those three lines become `dotnet add package
DotNetBoost.Auditing.Core`, and so on.

**2. Replace `Program.cs` with this**

```csharp
using DotNetBoost.Auditing.API;
using DotNetBoost.Auditing.Core.Attributes;
using DotNetBoost.Auditing.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Keep the trail in SQLite, and attach the interceptor that writes it. Without UseAuditing(sp)
// the tables are still there and IAuditManager still works, but nothing records itself.
builder.Services.AddDbContext<AppDbContext>((sp, o) => o
    .UseSqlite("Data Source=shop.db")
    .UseAuditing(sp));

builder.Services.AddAuditing(o => o.Retention = TimeSpan.FromDays(365))
    .UseEntityFrameworkCore<AppDbContext>()
    .DeclareEntitiesFrom(typeof(Product).Assembly)
    .Build();

var app = builder.Build();

// Create the database and the two audit tables on first run.
// In a real app, use EF Core migrations instead.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
}

app.MapAuditEndpoints(o => o.RequireAuthorization = false);   // reads only. DO NOT COPY THIS LINE

// Your own endpoints. Nothing in them mentions auditing.
app.MapPost("/products", async (Product product, AppDbContext db) =>
{
    db.Products.Add(product);
    await db.SaveChangesAsync();        // recorded as Created, with the key the database assigned
    return product;
});

app.MapPut("/products/{id:int}/price", async (int id, PriceUpdate body, AppDbContext db) =>
{
    var product = await db.Products.FindAsync(id);
    if (product is null)
        return Results.NotFound();

    product.Price = body.Price;         // loaded first, so EF has the old value to record
    await db.SaveChangesAsync();
    return Results.Ok(product);
});

app.MapDelete("/products/{id:int}", async (int id, AppDbContext db) =>
{
    var product = await db.Products.FindAsync(id);
    if (product is null)
        return Results.NotFound();

    db.Products.Remove(product);
    await db.SaveChangesAsync();        // the entry survives the row, with its final values
    return Results.NoContent();
});

app.Run();

// Your entity. Nothing is added to it; the attributes only say what not to record.
[AuditEntity("Product")]
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }

    [AuditIgnore] public int ViewCount { get; set; }
}

public record PriceUpdate(decimal Price);

// Your EF Core context: one line adds the two audit tables. No interface to implement.
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyAuditing();
}
```

**3. Run it, and change something**

```bash
dotnet run --urls http://localhost:5080
```

In a second terminal:

```bash
curl -X POST http://localhost:5080/products -H "Content-Type: application/json" -d '{"name":"Widget","price":24.99}'
curl -X PUT http://localhost:5080/products/1/price -H "Content-Type: application/json" -d '{"price":19.99}'
curl -X DELETE http://localhost:5080/products/1
```

**4. Read the trail**

```bash
curl "http://localhost:5080/api/audit?entityName=Product&entityId=1"
```

```json
{
  "entries": [
    { "entityName": "Product", "entityId": "1", "action": "Deleted", "occurredAt": "2026-10-21T15:32:17.827537Z",
      "changes": [ { "propertyName": "Name",  "oldValue": "Widget", "newValue": null },
                   { "propertyName": "Price", "oldValue": "19.99",  "newValue": null } ] },
    { "entityName": "Product", "entityId": "1", "action": "Updated", "occurredAt": "2026-10-21T15:32:17.815050Z",
      "changes": [ { "propertyName": "Price", "oldValue": "24.99",  "newValue": "19.99" } ] },
    { "entityName": "Product", "entityId": "1", "action": "Created", "occurredAt": "2026-10-21T15:32:17.773764Z",
      "changes": [ { "propertyName": "Name",  "oldValue": null,     "newValue": "Widget" },
                   { "propertyName": "Price", "oldValue": null,     "newValue": "24.99" } ] }
  ],
  "total": null, "skip": 0, "take": 3
}
```

Three entries, newest first, and the product they describe no longer exists. The endpoints wrote
themselves: no endpoint above mentions auditing, and `ViewCount` would never appear here. `total` is
`null` because the query did not ask for a count — that means **not counted**, never zero. Trimmed
above: every entry also carries its `id`, `actor`, `correlationId` and `metadata`, and every change
says whether it was redacted or truncated.

> ⚠️ **`RequireAuthorization = false` is for this quick start only.** Unlike the sibling packages,
> these endpoints **require authorization by default**: a trail is a map of an organisation's
> activity and, through `Actor`, personal data about its staff. Delete that line before you deploy,
> or set `o.Policy`. The erasure and prune endpoints do not exist at all unless asked for
> (`o.MapErasure`, `o.MapPrune`). See [rest-api.md](docs/rest-api.md).

---

## What it is, and what it is not

| | |
|---|---|
| **Entity change history** — who changed which property of which entity, old value → new value | **This library.** |
| **Action / request logging** — who called which endpoint, when, from where, how long it took | **Not this library.** Use OpenTelemetry, `HttpLogging` or Serilog. |

Request logging is already solved, better than a data-access library can solve it. Entity change
history is the half no observability tool gives you, because it needs the before-image of the row
and the same transaction as the write. [scope.md](docs/scope.md) has the rest of what is
deliberately left out — temporal reconstruction, event sourcing, revert verbs, tamper-proofing —
and why.

---

## Using it in your own code

Under the EF Core interceptor an application mostly only reads. Inject `IAuditManager` for that, and
for the changes a change tracker cannot see — a raw `ExecuteUpdate`, a stored procedure, a domain
event:

```csharp
public class PriceService(AppDbContext db, IAuditManager audit)
{
    public Task<AuditPage> HistoryAsync(Product product, CancellationToken ct)
        => audit.HistoryAsync(product, skip: 0, take: 50, ct);

    public async Task DiscountAsync(Product product, decimal newPrice, CancellationToken ct)
    {
        // A set-based UPDATE never reaches the change tracker, so nothing is captured for it.
        await db.Products.Where(p => p.Id == product.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.Price, newPrice), ct);

        await audit.For(product).UpdatedAsync(
            [new AuditChange(nameof(Product.Price), product.Price, newPrice)], ct: ct);

        await db.SaveChangesAsync(ct);   // the EF Core store joins this save: now it is durable
    }
}
```

| You want to… | Call |
|---|---|
| read one entity's history, newest first | `await audit.HistoryAsync(product, skip, take, ct)` |
| read a filtered page (actor, action, dates, correlation id) | `await audit.QueryAsync(new AuditQuery { Actor = "…", From = … }, ct)` |
| read one entry with its changes | `await audit.GetAsync(id, ct)` |
| record a change nothing tracked | `await audit.For(product).UpdatedAsync(changes, ct: ct)` |
| record against an entity this process has no instance of | `await audit.For("Product", "42").DeletedAsync(ct: ct)` |
| let the library work out what moved | `AuditDiff.Between(before, after)`, `InitialValues(e)`, `FinalValues(e)` |
| apply the configured retentions | `await audit.PruneAsync(batchSize: 1000, ct)` |
| erase one subject's or one actor's trail | `await audit.EraseForAsync(user, ct)`, `await audit.EraseForActorAsync(actor, ct)` |

Every read is bounded: `Take` is clamped to `MaxPageSize` and an unset `Take` becomes
`DefaultPageSize` rather than everything. There is no "read it all" verb — the predecessor in this
suite shipped an unbounded history call over a table that only grows, and that is on the short list
of why it was deleted.

`AuditDiff` is the stand-in for a change tracker: it reports what moved between two instances, so
the property list comes off the type rather than out of an `if` chain that stops mentioning the
property somebody adds next year. It skips navigation properties, collections and nested objects
silently — those still need reporting by hand. See
[limitations.md](docs/limitations.md#auditdiff-compares-simple-values-only).

---

## Using your existing database

The quick start uses EF Core with SQLite because that needs no setup. In a real app, use the
database you already have:

| You use… | Package | Register with |
|---|---|---|
| Entity Framework Core | `DotNetBoost.Auditing.EntityFrameworkCore` | `.UseEntityFrameworkCore<AppDbContext>()` |
| Dapper / plain ADO.NET | `DotNetBoost.Auditing.Dapper` | `.UseDapper(sp => new SqlConnection(cs), migrateSchema: true)` |
| MongoDB | `DotNetBoost.Auditing.MongoDb` | `.UseMongoDb("mongodb://localhost:27017", "my_app_db")` |

**EF Core captures on its own** and appends the rows inside the caller's own `SaveChanges`, so the
change and its trail land together or neither does. A creation with a store-generated key is the
exception — its id does not exist until the INSERT has run, so that entry needs a second save, and
the interceptor opens a transaction around both when the caller has none. Assign keys in code and
creations join the single-save path too; [limitations.md](docs/limitations.md) has the two cases
that cannot be made atomic.

**Dapper and MongoDB write when they are called** rather than joining a unit of work, so atomicity
is yours to arrange: hand the first your transaction, or the second a session, and the guarantee is
the same as EF Core's. Both also need `DeclareEntitiesFrom(assembly)` — a write addressed by hand
has no type for the engine to read `[AuditRedact]` from, and without a declaration a secret is
written down in full. [stores.md](docs/stores.md) covers both, and `samples/SampleApp.Dapper` and
`samples/SampleApp.MongoDb` are that page as code you can `curl`.

---

## What else it can do

The engine is configured in one place, and every option is optional:

```csharp
services.AddAuditing(o =>
    {
        o.Retention    = TimeSpan.FromDays(365);     // nothing is deleted until a prune runs
        o.MaxPageSize  = 200;                        // a read can never ask for more
        o.RequireOptIn = true;                       // record only [Audited] entities
    })
    .UseEntityFrameworkCore<AppDbContext>()
    .DeclareEntitiesFrom(typeof(Product).Assembly)   // apply the model's rules to writes by name
    .UseHttpContextActor()                           // record the signed-in user as the actor
    .Build();
```

| I want to… | Guide |
|---|---|
| Record from Dapper or MongoDB, and know when each becomes durable | [Stores](docs/stores.md) |
| Mount, expose and secure the endpoints | [REST API](docs/rest-api.md) |
| Keep a password or a key out of the trail | [Redaction](docs/scope.md#6-redaction-and-sensitive-data) |
| Record who acted, and not just what changed | [The actor](docs/scope.md#5-the-actor) |
| Keep a noisy entity for 30 days and everything else for a year | [Retention](docs/scope.md#9-retention) |
| Prune on a schedule, or erase one person's trail on request | [Retention](docs/scope.md#9-retention) |
| Capture changes a sibling library made (`DotNetBoost.Settings`) | [How changes are captured](docs/scope.md#7-how-changes-are-captured) |
| Join an audit entry to a media row or a translation row | [It stands alone](#it-stands-alone) |
| Know what this cannot do, and what to use instead | [Limitations](docs/limitations.md), [Scope](docs/scope.md) |
| Give an admin a web UI | [Dashboard](docs/dashboard.md) |
| Run the whole stack with one command | [.NET Aspire](docs/aspire.md) |
| See every builder method and option | [Configuration reference](docs/configuration-reference.md) |
| Understand how it works inside | [Architecture](docs/architecture.md) |
| See how it is tested, and what is not covered | [Testing](docs/testing.md) |

Everything is indexed in [docs/](docs/README.md), and released changes in
[CHANGELOG.md](CHANGELOG.md).

---

## It stands alone

No package reference to `DotNetBoost.Settings`, `.Media` or `.MultiLingual`, no shared kernel, and
none of their names in the source. It installs into an application that has none of them.

What it shares is a **convention**, implemented independently: **DotNetBoost Entity Addressing v1**.
This library, `.Media` and `.MultiLingual` resolve entity names and format keys the same way and
each publishes the same constant, so an application can assert the versions match at startup
instead of discovering a mismatch as rows that stopped joining:

```csharp
Assert.Equal(
    DotNetBoost.Auditing.Core.Metadata.EntityKey.AddressingVersion,
    DotNetBoost.Media.Core.Metadata.EntityKey.AddressingVersion);
```

That one equality is the whole of the relationship, and it is what lets an application join an audit
entry to a media row or a translation row on `(EntityName, EntityId)`. The rules are in
[scope.md](docs/scope.md#4-entity-addressing-v1).

---

## Try the full demo

If you have Docker running, one command starts PostgreSQL, MongoDB, **three sample APIs** — one per
store, serving the same routes over the same two entities — and the dashboard:

```bash
dotnet run --project aspire/DotNetBoost.Auditing.AppHost
```

| | |
|---|---|
| [`samples/SampleApp`](samples/SampleApp/README.md) | EF Core. **No endpoint mentions auditing**: three attributes and two lines at startup are the whole of it |
| [`samples/SampleApp.Dapper`](samples/SampleApp.Dapper/README.md) | Dapper. Every entry written by hand, the before-image from a `SELECT`, and one transaction per request |
| [`samples/SampleApp.MongoDb`](samples/SampleApp.MongoDb/README.md) | MongoDB. The same explicit recording, and the atomicity window a standalone `mongod` leaves open |

The quickest way to feel the difference is `POST /api/products/{id}/fail` on each: the change and
its entry are already committed, or neither is, or the change is and the trail is silent. See
[aspire.md](docs/aspire.md).

---

## Packages

| Package | What it's for |
|---|---|
| `DotNetBoost.Auditing.Core` | Required. The engine: `IAuditManager`, the attributes, redaction, addressing |
| `DotNetBoost.Auditing.EntityFrameworkCore` | The interceptor, the store and the two tables |
| `DotNetBoost.Auditing.Dapper` | Store only; SQL Server, PostgreSQL, SQLite |
| `DotNetBoost.Auditing.MongoDb` | Store only; one collection, changes embedded |
| `DotNetBoost.Auditing.API` | The `/api/audit` endpoints, and `UseHttpContextActor()` |

## License

MIT. See [LICENSE](LICENSE).
