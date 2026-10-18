# SampleApp

A small product catalogue that happens to keep a complete audit trail. The interesting thing about
it is how little of it is about auditing: three attributes on `Product`, two lines at startup, and
nothing in any endpoint.

## Running it

The easy way is the Aspire AppHost, which starts PostgreSQL, this API and the dashboard together:

```bash
dotnet run --project ../../aspire/DotNetBoost.Auditing.AppHost
```

On its own, against a PostgreSQL you already have:

```bash
ConnectionStrings__auditing="Host=localhost;Database=auditing;Username=postgres;Password=…" \
  dotnet run
```

For no container at all, switch the SQLite block in `Program.cs` and `SampleApp.csproj`.

## The actor

`DemoAuthentication.cs` signs you in as whoever the `X-Demo-User` header names. It is a stand-in
and nothing to copy — it exists because the actor is the point of an audit trail, and without any
authentication every entry's actor would be honestly `null`. Send the header and the trail shows
who did what:

```bash
curl -X POST localhost:5280/api/products \
  -H 'Content-Type: application/json' -H 'X-Demo-User: alice@example.com' \
  -d '{"name":"Widget","price":24.99,"category":"tools","supplierCost":12.50}'
```

## What each endpoint demonstrates

| | |
|---|---|
| `POST /api/products` | A **creation**, recorded with the key the database assigned — which did not exist when `SavingChanges` ran, so the entry is written by a second save inside a transaction the interceptor opened |
| `PUT /api/products/{id}` | An **update**, recording only the property that actually moved. The entity is loaded first, so EF has a before-image |
| `GET /api/products/{id}` | Bumps an `[AuditIgnore]` view counter. **Nothing is recorded** — otherwise every price change would be buried under page views |
| `DELETE /api/products/{id}` | The row goes; **its trail does not**. Nothing cascades to the audit tables, so the `Deleted` entry with its final values is the only copy of what the row said |
| `GET /api/products/{id}/history` | One entity's trail — and it still answers for a product that has been deleted |
| `POST /api/products/discount` | A set-based `ExecuteUpdate` that the **change tracker never sees**, recorded explicitly through `IAuditRecorder`. This is also how a Dapper or MongoDB application records everything |
| `POST /api/products/{id}/fail` | Changes a price, saves, then throws — so you can see what the trail says afterwards. The comment explains the case that matters |
| `POST /api/stock-checks` | A noisy entity with `[AuditRetention(Days = 30)]`, kept for a month rather than the configured year |
| `POST /api/maintenance/prune` | Applies both retentions at once. The library never prunes on its own |
| `GET /api/audit…` | The library's own endpoints — **authorized by default**, with erasure and prune mapped here because the sample exists to show them |

## Two things to notice

**`SupplierCost` is `[AuditRedact]`.** Create a product and read its trail: the property is recorded
as *changed*, with both values `null`. They were never written. An auditing library is the likeliest
place in an application for a secret to sit in plain text for years.

**`DeclareEntitiesFrom` is here for one endpoint.** `/api/products/discount` records by name, and a
write addressed by hand has no type for the engine to read `[AuditRedact]` from. Without that line
the bulk discount would be the one place a redacted property leaked. An application that only ever
uses the interceptor does not need it.
