# SampleApp.Dapper

The EF Core sample (`samples/SampleApp`) over the Dapper store. Same `Product` and `StockCheck`,
same attributes, same routes, same request and response bodies — the DTOs and the actor accessor are
*the same files*, compiled into both projects — so `clients/dashboard` can be pointed at either API
without a change.

What differs is **who notices a change**. There is no change tracker, so nothing notices: every
entry in this sample is written by a line in `Program.cs`.

## The two things that change, and where to look

**The before-image comes from a `SELECT`.** `PUT /api/products/{id}` loads the row first — not so
that an interceptor has something to diff, but because there is no other way to know what the values
were. A blind `UPDATE` is one round trip cheaper and records nothing worth reading: a trail that
cannot say what a price changed *from* is most of the value gone.

**The comparison is `AuditDiff.Between(before, after)`**, not a property-by-property `if` chain. The
chain this replaced was correct, and it would have stayed correct only until somebody added a
property to `Product`: nothing fails when a diff written by hand misses one, the trail just stops
mentioning it. `AuditDiff` reads the type instead, skips the key and the `[AuditIgnore]` view count,
and still leaves `[AuditRedact]` to the engine — `AuditDiff.InitialValues` / `FinalValues` do the
same for the creation and the deletion. See [docs/stores.md](../../docs/stores.md).

**Atomicity is arranged, not inherited.** `AppendAsync` issues its `INSERT`s when it is called, so
without a transaction each entry commits by itself. `Data/UnitOfWork.cs` puts an `IDbTransaction` in
the request scope and `UseDapper(...)` resolves one when it builds its store, so a row's `UPDATE`
and its entry's `INSERT`s land in the same transaction with nothing in the endpoints saying so. The
only trace of it in the endpoints is that each Dapper call carries `unitOfWork.Current`, which
ADO.NET requires once a connection has an open transaction.

`Data/UnitOfWorkMiddleware.cs` commits it, and it is middleware rather than an endpoint filter on
purpose: it has to cover the library's own `MapAuditEndpoints()` too, or a `POST /api/audit/prune`
would delete entries inside a transaction nobody commits, report how many it deleted, and leave them
all in place.

[docs/stores.md](../../docs/stores.md) is the one page on this.

## What `DeclareEntitiesFrom` is doing here

The EF Core sample needs that line for one endpoint. This sample needs it the way every Dapper
application does:

- A write addressed **by name** — `audit.For("Product", "42")`, which is what a composite key or a
  row nothing loaded forces — has no type for the engine to read `[AuditRedact]` from. Undeclared,
  a redacted property is written to the table in full. That is the one failure in this library that
  is both silent and permanent.
- A **prune** finds `StockCheck`'s own 30-day retention through the declared types. Undeclared, the
  global sweep would take its entries with everything else's.
- `GET /api/audit/entities` answers with them, so the dashboard's model page is empty without it.

Every write in this sample goes through `audit.For(entity)`, which carries the type, so the
redaction here survives the line being commented out — the second and third bullets do not, and
`curl localhost:5281/api/audit/entities` shows it in one command. The first bullet is the one to
take seriously anyway: it is one refactor away, and it cannot be noticed afterwards.

## Run it

Through the Aspire AppHost, which starts PostgreSQL, MongoDB, all three APIs and the dashboard
together:

```bash
dotnet run --project aspire/DotNetBoost.Auditing.AppHost
```

It takes its own database (`auditingdapper`) on the same PostgreSQL container the EF Core sample
uses, so pgweb reaches both and the two schemas can be compared side by side. They are identical —
that is the claim the Dapper store makes, and there is a test that writes with one store and reads
with the other.

Or on its own, against any PostgreSQL:

```bash
docker run -d --name auditing-pg -e POSTGRES_PASSWORD=postgres -p 5432:5432 postgres:17-alpine
```

```bash
ConnectionStrings__auditingdapper="Host=localhost;Database=postgres;Username=postgres;Password=postgres" dotnet run --project samples/SampleApp.Dapper
```

An empty database is enough: `migrateSchema: true` creates the library's two tables and
`Data/DbInitializer.cs` creates the sample's. It listens on <http://localhost:5281> and opens Scalar
at `/scalar`.

To run it on SQL Server or SQLite instead, swap the driver package in `SampleApp.Dapper.csproj` and
the factory in `Program.cs`. The store detects the dialect from the connection type; the DDL in
`Data/DbInitializer.cs` is the only PostgreSQL-specific code in the sample, and the library's own DDL
already covers all three.

## Try it

```bash
curl -s -X POST localhost:5281/api/products \
  -H 'Content-Type: application/json' \
  -d '{"name":"Widget","price":24.99,"category":"tools","supplierCost":12.50}'
```

```bash
curl -s localhost:5281/api/products/1/history
```

The `Created` entry, with `SupplierCost` recorded as **changed and both values `null`** — it was
never written. Then:

```bash
curl -s -X PUT localhost:5281/api/products/1 \
  -H 'Content-Type: application/json' -d '{"price":19.99}'
```

```bash
curl -s localhost:5281/api/products/1/history
```

One `Updated` entry with one property change, `24.99 → 19.99`. Nothing else is in it, because
`AuditDiff.Between` compared the before-image with the after-image and sent only what moved.

Now the one that matters:

```bash
curl -s -X POST localhost:5281/api/products/1/fail
```

A `500`, and afterwards **the price is unchanged and the trail says nothing happened** — the
`UPDATE` and the entry's `INSERT`s were in one transaction that was rolled back. That is the same
guarantee the EF Core store gives, bought with the registration in `Program.cs`. Remove it and this
endpoint leaves an entry for a change that never happened.

And the one that looks like nothing:

```bash
curl -s localhost:5281/api/products/1 && curl -s localhost:5281/api/products/1/history
```

The view count went up and the trail is unchanged. The endpoint *did* report the change to the
recorder; `[AuditIgnore]` dropped the only property, which makes an `Updated` write of nothing, and
the call returned `null`. The model's rules are applied in the engine, so an application cannot opt
out of them by addressing the write itself.

## With the dashboard

In `aspire/DotNetBoost.Auditing.AppHost/AppHost.cs`, change the dashboard's `NUXT_AUDIT_API_URL`
from `api` to `dapperApi`. Nothing in `clients/dashboard` changes: it sees the same routes and the
same bodies.

## Endpoints

Identical to the EF Core sample's, and each one records what the interceptor would have.

| | |
|---|---|
| `POST /api/products` | `INSERT … RETURNING "Id"`, then `CreatedAsync` with `AuditDiff.InitialValues` |
| `PUT /api/products/{id}` | `SELECT` for the before-image, `UPDATE`, then `UpdatedAsync` with what `AuditDiff.Between` found |
| `GET /api/products/{id}` | Bumps the `[AuditIgnore]` view count **and reports it** — the engine returns `null` |
| `DELETE /api/products/{id}` | `DELETE`, then `DeletedAsync` with `AuditDiff.FinalValues`. Nothing cascades to the trail |
| `GET /api/products/{id}/history` | One entity's trail — and it still answers for a deleted product |
| `POST /api/products/discount` | One set-based `UPDATE`, one entry per product. Not a special case here |
| `POST /api/products/{id}/fail` | Writes both, then throws: **neither** is committed |
| `POST /api/stock-checks` | The noisy entity, kept 30 days by its `[AuditRetention]` |
| `POST /api/maintenance/prune` | Applies both retentions at once. The library never prunes on its own |
| `GET /api/audit…` | The library's own endpoints, unchanged — reading a trail knows nothing about how it was written |
