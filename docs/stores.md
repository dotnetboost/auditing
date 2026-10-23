# Stores

All three are built. The two relational ones keep the same two tables in the same shape — same
names, same column widths, same index names — so **one database serves either**, which a test proves
by writing with one store and reading with the other. MongoDB is a different shape on purpose, and
its section says why.

What they do not share is *when a write becomes durable*, and that difference is the whole of the
choice between them. There is a sample per store, serving the same routes over the same two
entities, so the difference is something to run rather than read: `samples/SampleApp`,
[`samples/SampleApp.Dapper`](../samples/SampleApp.Dapper/README.md) and
[`samples/SampleApp.MongoDb`](../samples/SampleApp.MongoDb/README.md), all three started together by
[the AppHost](aspire.md).

| | EF Core | Dapper | MongoDB |
|---|---|---|---|
| Status | **Built**, proven on PostgreSQL + SQL Server | **Built**, proven on PostgreSQL + SQL Server | **Built**, proven on MongoDB 7 |
| Shape | Two tables | Two tables | **One collection**, changes embedded |
| Capture | Automatic, from the change tracker | Explicit, through `IAuditManager` | Explicit |
| `AppendAsync` | **Tracks**; your `SaveChanges` commits | **Writes**, there and then | **Writes**, there and then |
| An entry is complete in one write | With a transaction | With a transaction | **Always** — it is one document |
| Atomic with the change | **By default** | **With your transaction** | With a session, on a replica set |
| Before-images | Read from the change tracker | Supplied by you; compared by `AuditDiff` | Supplied by you; compared by `AuditDiff` |
| Redaction rules | From the entity type | **Needs `DeclareEntitiesFrom`** | **Needs it too** |
| Timestamp precision | 100ns ticks | 100ns ticks | **Milliseconds** |

## EF Core

```csharp
services.AddDbContext<AppDbContext>((sp, o) => o.UseNpgsql(cs).UseAuditing(sp));
services.AddAuditing().UseEntityFrameworkCore<AppDbContext>().Build();
// + modelBuilder.ApplyAuditing() in OnModelCreating
```

Nothing in a repository or handler changes: the entity's attributes and one line on the context are
the whole opt-in. An update or delete is recorded inside the caller's own `SaveChanges`, in the same
batch and transaction. A creation with a store-generated key costs a second save, because its id
does not exist until the INSERT has run; the interceptor opens a transaction around both when the
caller has none. [limitations.md](limitations.md) has the two cases it cannot make atomic.

`samples/SampleApp` is this store, and the thing to notice there is that no endpoint mentions
auditing at all.

## Dapper

```csharp
services.AddAuditing()
        .UseDapper(_ => new NpgsqlConnection(cs), migrateSchema: true)
        .DeclareEntitiesFrom(typeof(Product).Assembly)   // not optional here
        .Build();
```

**Recording is explicit**, because there is no change tracker to read:

```csharp
await audit.For("Product", "42").UpdatedAsync(
[
    new AuditChange(nameof(Product.Price), 24.99m, 19.99m)
], ct);
```

**The comparison does not have to be.** `AuditDiff` is Core's stand-in for the change tracker: it
reports what moved between two instances, so the one part of the explicit path that is easy to get
wrong by hand is read off the type instead.

```csharp
var changes = AuditDiff.Between(before, after);     // only what moved
if (changes.Count == 0) return;                     // nothing did

await audit.For(after).UpdatedAsync(changes, ct);

await audit.For(product).CreatedAsync(AuditDiff.InitialValues(product), ct: ct);
await audit.For(product).DeletedAsync(AuditDiff.FinalValues(product), ct: ct);
```

It compares the properties the engine reasons about and skips three kinds: the key, which is already
the entry's address; `[AuditIgnore]` properties, so an update that moved only those comes back empty
rather than as an entry that says a change happened and never which; and anything that is not a
simple value — a navigation property, a collection, a nested object — because comparing those by
reference reports a change on every write. Those are skipped **silently**, so an entity whose
interesting state lives in a nested object still reports it by hand. `[AuditRedact]` properties *are*
compared: the comparison is in memory, and withholding the values stays the engine's job.

The point of it is not the keystrokes. A hand-written `if` chain is correct the day it is written and
stops mentioning the property somebody adds to the entity next year, which is a gap nothing fails
over — the trail just quietly stops recording it.

**Atomicity is yours to arrange.** `AppendAsync` issues its INSERTs when it is called, so without a
transaction the entry commits by itself and a failure immediately afterwards leaves a trail of a
change that was rolled back:

```csharp
using var tx = connection.BeginTransaction();
var store    = new DapperAuditStore(connection, options).WithTransaction(tx);
// … write your row, record the change …
tx.Commit();
```

Registering an `IDbTransaction` in the scope does the same thing for the container-resolved store.
The EF Core store gets the guarantee for free; here it costs one transaction.

[`samples/SampleApp.Dapper`](../samples/SampleApp.Dapper/README.md) is this store, with that
registration in `Program.cs` and a `UnitOfWork` in the request scope — and a
`POST /api/products/{id}/fail` endpoint that writes a change and its entry, throws, and leaves
neither behind. Its `PUT` is the other half of what the explicit path costs: the before-image comes
from a `SELECT`, because nothing else has one.

**`DeclareEntitiesFrom` is not optional.** Everything is addressed by name and key, so the engine
has no type to read `[AuditRedact]` from unless the assembly is declared — and undeclared, a
redacted property is written to the table in full. That is the one failure in this library that is
both silent and permanent, so both outcomes have a test, including one that reads the column back
out of the database to prove the secret is not on disk.

### Schema

`migrateSchema: true` creates the tables at startup if they are absent. It only ever CREATEs — it
will not migrate a table whose shape has changed — so leave it off when migrations or a deployment
step own the schema, and get the DDL to check in instead:

```csharp
var ddl = DapperSchema.Script(SqlDialect.PostgreSql);
```

The dialect comes from the connection's runtime type (`SqlConnection`, `NpgsqlConnection`,
`SqliteConnection`); set `DapperOptions.Dialect` when the connection is a wrapper whose type name no
longer says which engine is behind it.

### What the store does for itself, and why

- **It converts the identity columns.** SQL Server and PostgreSQL return a GUID as a `Guid`; SQLite
  has no GUID type and returns the `TEXT` it stored. Dapper cannot convert a string to a `Guid`,
  because `Guid` is not `IConvertible` and so misses the fallback that gets `DateTime` and `bool`
  across the same gap. The store reads those columns as they come and converts them itself rather
  than registering a global `SqlMapper` type handler, which would change how every `Guid` in the
  host application is read. (EF Core has no such problem: its model states the column's CLR type.)
- **It stamps `DateTimeKind.Utc` on every timestamp it reads.** SQL Server and SQLite both return
  `Unspecified`, which serializes without a `Z` and is read by every client as local time. The EF
  Core store fixes this with a value converter; this is the same fix, so the two agree about what a
  stored timestamp means.
- **It deletes the property rows itself** rather than relying on the cascade the DDL declares: a
  cascade only fires where the database enforces foreign keys, which SQLite does not do unless it
  was asked to, and an orphaned property row is invisible forever.
- **It borrows the connection, never owns it.** A closed connection is opened for the statement and
  closed again; an open one is left open.

### Tested against PostgreSQL and SQL Server

The bootstrap's DDL is executed on both, and the indexes, the cascade and the column widths are
asserted against each server's own catalogue — see [testing.md](testing.md). That suite also found
the one defect this store had: **Dapper does not expand `IN @Ids` on Npgsql**, so every read that
loaded property changes and every delete was broken for PostgreSQL users. `AuditStatements.AnyOf`
now spells the predicate per dialect, using PostgreSQL's own `= ANY(@Ids)`.

## MongoDB

```csharp
services.AddSingleton<IMongoClient>(new MongoClient(connectionString));
services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase("shop"));

services.AddAuditing()
        .UseMongoDb()
        .DeclareEntitiesFrom(typeof(Product).Assembly)   // not optional here either
        .Build();
```

Recording is explicit, exactly as with Dapper — including `AuditDiff` for the comparison, which
reads the entity's properties and so knows nothing about either store — and `DeclareEntitiesFrom`
matters for the same reason: everything is addressed by name, so without it redaction has no type to
read.

### One collection, not two

The relational stores keep the property changes in a table of their own, and the reason given there
was indexability: in SQL, a JSON column makes "every change anyone ever made to `Price`" an
unindexable scan. **MongoDB indexes the fields of an array natively**, so the same question is a
multikey seek on `Changes.PropertyName` and the argument does not carry over. What does carry over
is the shape of the data: an entry is written once, read whole and never updated, which is the
aggregate a document is for.

Embedding buys two things the relational stores need a transaction for:

- **An entry is always complete.** One insert writes the whole entry, so there is no window in which
  a trail holds an entry whose property rows have not arrived.
- **Deleting an entry takes its changes with it**, with no second delete and no cascade to depend on.

This is deliberately the opposite of what `DotNetBoost.Media` does with its attachments, which live
in a collection of their own — it has to sort by a field of the *matched* array element, and MongoDB
sorts an array field by its smallest or largest element instead. Nothing here sorts by a property
change; entries sort by when they happened.

### Atomicity needs a session, and a session needs a replica set

```csharp
using var session = await client.StartSessionAsync(ct);
session.StartTransaction();

var store = new MongoAuditStore(database, options).WithSession(session);
// … write your document, record the change …
await session.CommitTransactionAsync(ct);
```

A standalone `mongod` refuses to start a transaction, which is why this is opt-in rather than
something the store arranges. Registering an `IClientSessionHandle` in the scope does the same thing
for the container-resolved store.

[`samples/SampleApp.MongoDb`](../samples/SampleApp.MongoDb/README.md) runs against the AppHost's
standalone `mongod`, so it shows the window rather than hiding it: its
`POST /api/products/{id}/fail` changes a price, commits it, and throws *before* recording, leaving a
price that moved and a trail that says it did not. That is the explicit path's real failure mode —
the thing the interceptor cannot forget and a hand-written call can — and its README has the
one-node replica set that closes it.

### What the store does for itself, and why

- **The entry's id is the document `_id`**, as a **standard** UUID rather than the driver's legacy
  binary subtype. Left to the default, other drivers and the shell read the same bytes as a
  different value — and an audit trail whose ids change meaning when read by another tool is not
  much of a record. A test asserts the subtype.
- **`Action` is stored as its name**, not its number. The relational stores keep an integer because
  that is what their index wants; a document is read by people as often as by code, and a
  renumbering cannot change what a stored document meant.
- **It registers no global class map.** The driver's map registry is static and process-wide, so a
  library that registered one for a Core model would be deciding how every other piece of code in
  the application serializes that type. The documents are this package's own types carrying their
  serialization on attributes, and a test asserts no map exists for the Core models.
- **An absent actor is absent**, not an explicit null — and a query for a null field matches both,
  so `actorMissing` still finds exactly those.

### It is proven against a real server now

It was not when it was written — there was no MongoDB in the environment, so its documents, filters
and index definitions were asserted by serializing and rendering them, and nothing more. The
[integration suite](testing.md) closed that: the store passes the same 28 conformance tests as the
relational ones against **MongoDB 7**, and on top of them the indexes are read back from the
server's own catalogue, the multikey query that justifies embedding is run for real, and
`WithSession` is shown committing and aborting against a **replica set**.

It passed first time, which is worth saying plainly: the serialization tests had been right about
the document, and the thing they could not tell us — whether a server accepts it and does what the
query says — turned out to be fine.
