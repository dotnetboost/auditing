# SampleApp.MongoDb

The EF Core sample (`samples/SampleApp`) over the MongoDB store. Same `Product` and `StockCheck`,
same attributes, same routes, same request and response bodies — the DTOs and the actor accessor are
*the same files*, compiled into all three sample projects — so `clients/dashboard` can be pointed at
any of the three APIs without a change.

The recording is the same explicit recording as `samples/SampleApp.Dapper`'s, line for line in
places — including `AuditDiff.Between(before, after)` for the comparison, which reads the entity's
properties and so knows nothing about either store. That is the honest summary of the explicit path:
it costs the same whatever the engine, and its two halves are a read for the before-image and a call
per change. What is specific to *this* store is the shape of an entry, and where the atomicity goes.

## What differs, and where to look

**One collection, with the changes embedded.** The relational stores keep the property changes in a
table of their own; this store embeds them in the entry document. Their reason is indexability — in
SQL, a JSON column makes "every change anyone ever made to `Price`" an unindexable scan — and it
does not carry over, because MongoDB indexes the fields of an array natively. `UseMongoDb()` creates
a multikey index on `Changes.PropertyName` for exactly that question. Two things follow, both
visible from the endpoints: `POST /api/products` writes a **complete** entry in one insert, with no
window in which the property rows have not arrived, and a prune's delete **takes the changes with
it**, with no second statement and no cascade.

Open mongo-express from the Aspire dashboard and look at one document in `auditEntries`: the id is a
**standard** UUID rather than the driver's legacy binary subtype, and `Action` is stored as its name
rather than its number — a document is read by people as often as by code.

**`DeclareEntitiesFrom` does the same three jobs as in the Dapper sample**: it gives the engine a
type to read `[AuditRedact]` and `[AuditIgnore]` from for a write addressed **by name**, it is how a
prune finds `StockCheck`'s own 30-day retention, and it is what `GET /api/audit/entities` answers
with. Undeclared, a name-addressed write puts a redacted property into the entry document in full —
silently, and permanently.

**Two details are this sample's, not the library's.** Keys are `int` from a counters collection
(`Data/IdSequence.cs`), because MongoDB generates no integers and these documents keep `int` keys so
the routes, the responses and the dashboard are unchanged — a greenfield model would use an
`ObjectId` and delete that file. One consequence favours this store: the key exists *before* the
insert, so recording never waits for the write, which is the problem the EF Core store solves with a
deferred second save. And `Price` and `SupplierCost` carry `[BsonRepresentation(BsonType.Decimal128)]`,
because the driver's default for `decimal` is a *string* that sorts lexicographically — a query for
"everything under ten" would answer nonsense.

## Atomicity: the window this sample shows instead of hiding

A single-document write in MongoDB is atomic by itself, and nothing wraps the two writes together.
So `POST /api/products/{id}/fail` changes a price, commits it, and then throws *before* recording:
afterwards the price is higher and the trail says nothing happened.

That gap is the explicit path's real failure mode, and it is the one thing the EF Core sample cannot
demonstrate — there the capture is inside the save and cannot be forgotten. The Dapper sample closes
it with a transaction per request; this sample cannot, because the AppHost runs a **standalone
`mongod`** and transactions there need a replica set. The fix, where you have one, is a session:

```csharp
using var session = await client.StartSessionAsync(ct);
session.StartTransaction();

var store = new MongoAuditStore(database, options).WithSession(session);
// … write the document, record the change …
await session.CommitTransactionAsync(ct);
```

Registering an `IClientSessionHandle` in the request scope does the same for the container-resolved
store, exactly as the Dapper sample registers an `IDbTransaction`. There is an integration test that
commits and aborts one against a real replica set — see [docs/stores.md](../../docs/stores.md).

To try it here, run a one-node replica set instead of the AppHost's `mongod`:

```bash
docker run -d --name auditing-mongo -p 27017:27017 mongo:7 --replSet rs0 --bind_ip_all
```

```bash
docker exec auditing-mongo mongosh --quiet --eval 'rs.initiate()'
```

## Run it

Through the Aspire AppHost, which starts PostgreSQL, MongoDB, all three APIs and the dashboard
together:

```bash
dotnet run --project aspire/DotNetBoost.Auditing.AppHost
```

Or on its own, against any MongoDB:

```bash
docker run -d --name auditing-mongo -p 27017:27017 mongo:7
```

```bash
ConnectionStrings__auditingmongo="mongodb://localhost:27017/auditing" dotnet run --project samples/SampleApp.MongoDb
```

An empty database is enough: collections appear on first write and the library creates its own
indexes at startup. It listens on <http://localhost:5282> and opens Scalar at `/scalar`.

## Try it

```bash
curl -s -X POST localhost:5282/api/products \
  -H 'Content-Type: application/json' \
  -d '{"name":"Widget","price":24.99,"category":"tools","supplierCost":12.50}'
```

```bash
curl -s localhost:5282/api/products/1/history
```

The `Created` entry, with `SupplierCost` recorded as **changed and both values `null`** — it was
never written. In mongo-express that is one document in `auditEntries`, changes and all.

Then the one that matters here:

```bash
curl -s -X POST localhost:5282/api/products/1/fail
```

A `500`. Read the product and its trail afterwards: **the price is higher and the trail is silent.**
Nothing is wrong with the library — the entry was never asked for — and that is exactly why the
pairing is worth arranging, with a session here or a transaction in the Dapper sample.

And the one that looks like nothing:

```bash
curl -s localhost:5282/api/products/1 && curl -s localhost:5282/api/products/1/history
```

The view count went up and the trail is unchanged. The endpoint *did* report the change;
`[AuditIgnore]` dropped the only property, which makes an `Updated` write of nothing, and the call
returned `null`.

## With the dashboard

In `aspire/DotNetBoost.Auditing.AppHost/AppHost.cs`, change the dashboard's `NUXT_AUDIT_API_URL`
from `api` to `mongoApi`. Nothing in `clients/dashboard` changes: it sees the same routes and the
same bodies.

## Endpoints

Identical to the EF Core sample's, and each one records what the interceptor would have.

| | |
|---|---|
| `POST /api/products` | Key from the counter, one insert for the document and one for the entry |
| `PUT /api/products/{id}` | A read for the before-image, a replace, then `UpdatedAsync` with what `AuditDiff.Between` found |
| `GET /api/products/{id}` | Bumps the `[AuditIgnore]` view count **and reports it** — the engine returns `null` |
| `DELETE /api/products/{id}` | `findOneAndDelete`, so the final values come back in the same round trip |
| `GET /api/products/{id}/history` | One entity's trail — and it still answers for a deleted product |
| `POST /api/products/discount` | One bulk write, one entry per product. Not a special case here |
| `POST /api/products/{id}/fail` | Changes a price, then throws **before** recording: the gap, shown |
| `POST /api/stock-checks` | The noisy entity, kept 30 days by its `[AuditRetention]` |
| `POST /api/maintenance/prune` | Applies both retentions. Deleting an entry takes its changes with it |
| `GET /api/audit…` | The library's own endpoints, unchanged — reading a trail knows nothing about the shape it is stored in |
