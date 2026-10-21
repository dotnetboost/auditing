# SampleApp

A small product catalogue that happens to keep a complete audit trail. The interesting thing about
it is how little of it is about auditing: three attributes on `Product`, two lines at startup, and
nothing in any endpoint.

## Running it

The easy way is the Aspire AppHost, which starts PostgreSQL, MongoDB, this API, the two sibling
samples and the dashboard together:

```bash
dotnet run --project ../../aspire/DotNetBoost.Auditing.AppHost
```

On its own, against a PostgreSQL you already have:

```bash
ConnectionStrings__auditing="Host=localhost;Database=auditing;Username=postgres;Password=…" \
  dotnet run
```

Either way it listens on <http://localhost:5280> and opens Scalar at `/scalar`. An empty database is
enough — the schema is created on first start. For no container at all, switch the SQLite block in
`Program.cs` and `SampleApp.csproj`.

## What each endpoint demonstrates

| Endpoint | What it shows |
|---|---|
| `POST /api/products` | A **creation**, recorded with the key the database assigned — which did not exist when `SavingChanges` ran, so the entry is written by a second save inside a transaction the interceptor opened |
| `PUT /api/products/{id}` | An **update**, recording only the property that actually moved. The entity is loaded first, so EF has a before-image |
| `GET /api/products/{id}` | Bumps an `[AuditIgnore]` view counter. **Nothing is recorded** — otherwise every price change would be buried under page views |
| `DELETE /api/products/{id}` | The row goes; **its trail does not**. Nothing cascades to the audit tables, so the `Deleted` entry with its final values is the only copy of what the row said |
| `GET /api/products/{id}/history` | One entity's trail — and it still answers for a product that has been deleted |
| `POST /api/products/discount` | A set-based `ExecuteUpdate` that the **change tracker never sees**, recorded explicitly through `IAuditManager`. This is the one endpoint `DeclareEntitiesFrom` is here for: it records by name, and a write addressed by hand has no type for the engine to read `[AuditRedact]` from |
| `POST /api/products/{id}/fail` | Changes a price, saves, then throws — so you can see what the trail says afterwards. The comment explains the case that matters |
| `POST /api/stock-checks` | A noisy entity with `[AuditRetention(Days = 30)]`, kept for a month rather than the configured year |
| `POST /api/maintenance/prune` | Applies both retentions at once. The library never prunes on its own |
| `GET /api/audit…` | The library's own endpoints. **Authorized by default** — turned off here (see the last section), with erasure and prune mapped because the sample exists to show them |

## Trying it by hand

Against the AppHost's port. Create a product, then read its trail:

```bash
curl -s -X POST localhost:5280/api/products \
  -H 'Content-Type: application/json' \
  -d '{"name":"Widget","price":24.99,"category":"tools","supplierCost":12.50}'
```

```bash
curl -s localhost:5280/api/products/1/history
```

Four property changes, and `SupplierCost` is one of them — recorded as **changed, with both values
`null` and `isRedacted: true`**. It is `[AuditRedact]`, so the number was never written down,
although the product endpoint itself returns it quite happily. An auditing library is the likeliest
place in an application for a secret to sit in plain text for years.

Change one property, and read a product:

```bash
curl -s -X PUT localhost:5280/api/products/1 -H 'Content-Type: application/json' -d '{"price":19.99}'
curl -s localhost:5280/api/products/1            # bumps the [AuditIgnore] view count
curl -s localhost:5280/api/products/1/history
```

Two entries, not three: the update recorded `Price` alone, `24.99 → 19.99`, and the read recorded
nothing at all.

Now the two that are worth running in order. A bulk discount the change tracker never sees, and a
failure after the save:

```bash
curl -s -X POST localhost:5280/api/products/discount \
  -H 'Content-Type: application/json' -d '{"category":"tools","percent":10}'
curl -s -X POST localhost:5280/api/products/1/fail
curl -s localhost:5280/api/products/1/history
```

The discount is an `Updated` entry like any other, carrying the metadata the endpoint passed
(`{"reason":"bulk discount","percent":10}`) — written by hand because no interceptor could have
seen it. The `fail` endpoint answers `500`, and its price change **is** in the trail: it was
committed before the throw, so it happened, and the trail saying otherwise would be the bug. The
case that matters is the other way round, and there is a test for it against PostgreSQL.

Finally, delete the product and read its history again:

```bash
curl -s -X DELETE localhost:5280/api/products/1
curl -s localhost:5280/api/products/1            # 404
curl -s localhost:5280/api/products/1/history    # every entry, newest first
curl -s localhost:5280/api/audit/entities
```

The product is gone and its trail is not, ending in a `Deleted` entry with the final values —
`SupplierCost` still withheld. The last call is the library's own discovery endpoint: `Product`
keeps its entries for the configured 365 days, `StockCheck` for its own 30.

## The same API over the other two stores

[`samples/SampleApp.Dapper`](../SampleApp.Dapper/README.md) and
[`samples/SampleApp.MongoDb`](../SampleApp.MongoDb/README.md) serve these same routes over the other
two stores — the DTOs and `SystemActorAccessor.cs` are *this project's files*, compiled into theirs
— so the dashboard can be pointed at any of the three without a change.

What differs there is that **every entry is written by hand**, because neither store has a change
tracker to read. The three `POST /api/products/{id}/fail` endpoints are the shortest way to see what
that costs and what it does not: here the change and its entry are already committed; in the Dapper
sample a transaction takes both back; in the MongoDB sample the price moves and the trail stays
silent. [docs/stores.md](../../docs/stores.md) is the page on it.

## Not production-ready in two respects

There is **no authentication and no authorization** here, and both consequences are deliberate and
neither is worth copying.

**Every entry is recorded against `"system"`.** `SystemActorAccessor.cs` is a one-line
`IAuditActorAccessor` that says so. With no signed-in user to read it is the literal truth — the
API changed its own catalogue and nobody asked it to — but in an application with users a constant
actor is a lie dressed as an answer, which is why the library's own default returns `null` instead.
A real application registers `UseHttpContextActor()` from `DotNetBoost.Auditing.API` and gets the
real actor from one `NameIdentifier` claim.

**`MapAuditEndpoints` is opened with `RequireAuthorization = false`.** Its default is *on*, unlike
the sibling libraries', so without that line every read of the trail here would answer `401`. It is
one line in a sample with no users and no real data; in an application it serves the whole trail,
and through its actors personal data about staff, to anyone who can reach the port. See
[docs/rest-api.md](../../docs/rest-api.md).
