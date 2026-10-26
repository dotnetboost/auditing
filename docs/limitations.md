# Limitations

What this library deliberately does not do, and what it cannot do. Each of these is a thing an
application will meet in practice; none of them is a bug report.

## A creation with a store-generated key costs a second save

An `Added` entity's id does not exist until its INSERT has run, and the entry's `EntityId` needs it.
So `SavingChanges` captures what to write, and `SavedChanges` — where every key is populated — does
the writing, in a second `SaveChanges`.

To keep that second write atomic with the first, the interceptor **opens a transaction** before the
primary save when the caller has none, and commits it only once the deferred entries are in. A
failure anywhere rolls the entity and its trail back together, which is tested.

An `Updated` or `Deleted` entity already has its key, so its entry goes in the caller's own single
save with no transaction opened at all. **Assign keys in code** — a `Guid`, HiLo, a sequence — and
creations join them.

### The context does not know the rollback happened

When that deferred write fails, the database is rolled back correctly — and the `DbContext` is
left believing it saved. EF accepts the primary save's changes before the interceptor's
`SavedChanges` runs, so the entity ends up `Unchanged`, holding the store-generated key the INSERT
gave it, while the table has no such row. Measured, not inferred: `state=Unchanged`, `key=1`,
`rowsInDb=0`.

The `SaveChangesAsync` call still throws, so an application that lets the exception travel is
fine — the write failed and it knows. The trap is catching it and carrying on with that context:
the entity now looks saved, so a later `SaveChanges` writes nothing and the row is quietly lost.
There is nothing the interceptor can do about it, because by then EF has already accepted the
changes. **Do not reuse a `DbContext` after a failed save** — which is EF's own guidance, and here
is one more reason for it.

## Two cases cannot be made atomic

Both still record, rather than refusing. They are the holes in the headline guarantee:

- **A retrying execution strategy with no caller transaction.** `EnableRetryOnFailure` refuses a
  transaction it did not start, because it could not replay it. The deferred entries are then
  written in their own transaction, so a failure between the two leaves the creation committed and
  its `Created` entry missing. Wrap the unit of work in
  `Database.CreateExecutionStrategy().ExecuteAsync(...)` with your own transaction and it closes.
- **A non-relational provider**, which has no transaction to open.

Neither applies when nothing is deferred — see above.

## `db.Update(entity)` has no before-image

`Update(entity)` and `Entry(entity).State = Modified` on an entity the context never loaded give EF
nothing to diff against: it marks every property modified and sets each original value equal to the
current one. There is no before-image anywhere in the process, and the only way to get one is a
round trip per entity, which a library must not do behind an application's back.

So such an entry records **the new values with no old ones**, listing every property rather than the
ones that moved. That is the honest report of what the change tracker knows. An unchanged entity
saved this way still produces an entry, because from outside the two are the same observation.

Set `RecordUpdatesWithoutBeforeImage = false` to skip these instead — and prefer loading the entity
before changing it, which brings the before-image back.

## `AuditDiff` compares simple values only

The explicit path's stand-in for a change tracker reflects over the entity's properties, and it
compares the ones that hold a value type, a string or a `byte[]`. A navigation property, a
collection or a nested object is **skipped silently**: comparing those by reference would report a
change on every write, and a trail column is not a place to put an object graph.

So an entity whose interesting state lives in a nested object gets no entry for a change to it from
`AuditDiff` alone. The list it returns is a plain `List<AuditChange>` for exactly this reason — add
the change yourself and record it with the rest. It also leaves out the key, which is already the
entry's address, and `[AuditIgnore]` properties, which the engine would drop anyway.

Nothing about this applies to the EF Core capture: it reads the model, which knows what a navigation
is.

## Owned types are not recorded

An owned entity shares its owner's key, so it has no address of its own: its entries would be filed
under the owner's key but the owned type's name, splitting one row's history across two names.
Recording them under the owner instead means merging two tracker entries into one, which the capture
cannot do without knowing which owner property reached it.

So changing only an owned property produces **no entry**. Promote it to its own entity with its own
key if its history matters.

## Composite keys are refused

An entry records a single key. An entity with a composite primary key throws when it is saved, with
the ways out in the message: a surrogate key, `[AuditIgnore]` — or `.IgnoreEntity<T>()` at
registration for a type you cannot annotate — or recording by hand with `audit.For(name, id)` and a
key you compose yourself.

An excluded entity's key is never read, so excluding it is enough: DotNetBoost.MultiLingual's own
`Translation` row is keyed on four columns, and `.IgnoreEntity<Translation>()` is what lets the
interceptor run in a context that saves one. Its skip is reported to the observer with an empty
`entityId`, because the library does not compose a multi-column key even for a report.

`DotNetBoost.Media` and `DotNetBoost.MultiLingual` refuse composite keys for the same reason, so an
entity that cannot be audited cannot be given media or translations either.

## An explicit entry is durable when you save

With the EF Core store, recording **tracks** the entry and does not save it. An entry
describes a change the caller is making, so it has to commit with that change — saving it on append
would commit the record of a change before the change itself.

For the interceptor this is exactly right. For an explicit call it means the entry reaches the
database when **you** call `SaveChangesAsync`, which is the next thing an EF Core application does
anyway. If you record against a context with nothing else pending, save it.

## The Dapper store is not atomic unless you make it

It has no change tracker to join, so `AppendAsync` writes when it is called. Without a transaction
the entry commits by itself, and a failure immediately afterwards leaves a trail of a change that
was rolled back. Hand the store your transaction — `WithTransaction(tx)`, or an `IDbTransaction` in
the scope — and the guarantee is the same as the EF Core store's.

## The Dapper store needs your entities declared

Everything there is addressed by name and key, so the engine has no type to read `[AuditRedact]` and
`[AuditIgnore]` from. Without `DeclareEntitiesFrom(assembly)` at `Build()`, **a redacted property is
written to the table in full** — the one failure in this library that is permanent, because the
values cannot be unwritten.

It no longer has to be silent as well. The engine reports every such write to `IAuditObserver`, and
`.UseLoggingObserver()` turns that into a warning naming the entity and the fix — so an application
that registers one finds out on the first write rather than from the table months later. Nothing is
reported by default: the observer registered by `AddAuditing()` does nothing, which is why the line
is worth adding. See [observability.md](observability.md), and
[the section below](#nothing-tells-you-what-was-not-recorded-unless-you-ask).

## Nothing tells you what was not recorded, unless you ask

The engine declines to record a change in five cases, all of them correct: recording is disabled,
the entity is `[AuditIgnore]`, opt-in is on and the entity is not `[Audited]`, opt-in is on and
nothing is declared under the entity's name, or an update whose every changed property is ignored.
Recording returns `null` to say so.

**By default that is the only signal, and on the EF Core path there is none at all.** The
interceptor records changes nobody asked it about, so it has no return value to hand back — it
throws the `null` away. So an application that quietly stops being audited, because opt-in was
turned on or `[AuditIgnore]` reached a base class, carries on looking healthy: logs clean, health
checks green, and the trail simply stops mentioning a table. That is usually discovered by someone
looking for a change that was never recorded, and an audit trail is the one thing that cannot be
backfilled.

Registering an observer closes it:

```csharp
services.AddAuditing()
        .UseEntityFrameworkCore<AppDbContext>()
        .UseLoggingObserver()        // or .UseObserver<MyMetricsObserver>()
        .Build();
```

Every suppression is then reported with its reason, by the interceptor as well as by the explicit
path. The two that are nearly always a misconfiguration — opt-in with nothing declared, and a write
recorded with no model rules to apply — are logged as warnings; the three that are configured intent
are Debug, because a high-churn ignored entity reports one on every save.

There are also three counters — entries recorded, writes suppressed, entries deleted — and the first
is the one to alert on, because "this entity recorded nothing for an hour" is the shape of every way
a trail stops covering a table.

**But all of it is off unless you ask**, which is the limitation rather than the feature: the default
observer does nothing and no counter is emitted until something subscribes, so those two lines are
the difference between finding out on the first write and finding out from the table.
[observability.md](observability.md) has both, the levels and the reasoning.

## MongoDB timestamps are milliseconds

BSON dates hold milliseconds; the relational stores keep 100-nanosecond ticks. A timestamp that
round-trips through the MongoDB store comes back truncated. Ordering is unaffected, because the
entry id is the tiebreaker in every sort — but two trails of the same events, one in MongoDB and one
in SQL, will not compare equal to the tick.

It reaches cursors too, in one narrow way: build an `AuditCursor` from an entry the engine returned
from *recording* rather than from a read, and its timestamp has ticks the stored document does not,
so the entry can come back on the next page. `AuditPage.NextCursor` — the cursor a read hands you,
and the one the REST endpoints issue — is taken from what the store returned, so it matches.

## MongoDB atomicity needs a replica set

Multi-document transactions are a replica-set feature, so a standalone `mongod` cannot make an entry
atomic with the application's own write. A single entry is still written whole in one insert, because
its property changes are embedded.

## One connection, one operation at a time

The Dapper store borrows the scope's `IDbConnection`, which the application can inject as well — and
an ADO.NET connection cannot run two commands at once. So two audited operations sharing a
connection have to take turns, and the store now makes them: it serialises its own work per
connection, so a second write queues behind the first instead of colliding with it.

That is a fix, not a design note. Borrowing meant "open it if it is closed, close it if I opened it",
which is a check-then-act on shared state: two concurrent writes could both find the connection
closed, both open it, and then the first to finish would close it while the second was still issuing
statements. The window is as wide as a connection takes to open — a network round trip for anything
but SQLite. Against SQLite it produced `ArgumentNullException: SafeHandle cannot be null` from inside
the native driver and wrote **40 of 20,001 entries**: a meaningless error and a half-written trail.
`ProviderTests` has that scenario.

**What the fix cannot cover.** The store only knows about its own operations. If the application
runs its own commands on that same connection at the same time — a `Task.WhenAll` over two
repositories that share the scope's connection — that is still two commands on one connection, and
no amount of locking inside this library makes it legal. **Give parallel work its own scope**, and
with it its own connection.

## An erasure finishes when the trail is empty, not at a cutoff

A prune is bounded: its cutoff is computed once, so entries written during the sweep are newer than
it and the sweep is finite by construction. Erasure has no cutoff — it deletes a batch at a time
until a batch comes back short — so it finishes when that entity's or actor's trail is momentarily
empty. An actor who is *still recording* therefore extends their own erasure.

In practice that is arithmetic rather than a risk: a batch deletes a thousand entries in one round
trip, so outpacing it would take something like a hundred thousand entries per second for one actor.
The reachable cost is duration, not non-termination — a large erasure is many round trips and holds
its caller, including an HTTP request, for the whole sweep. There is deliberately **no test** for
the live-writer case: the first attempt at one was a lock-contention storm that said more about
SQLite's single writer than about this loop, and a flaky test here would be worse than the note.

## Deep paging on SQL Server is not constant-cost

A cursor is meant to cost the same at any depth, and on PostgreSQL and SQLite it does — the keyset
predicate is the row-value form `(OccurredAt, Id) < (@at, @id)`, which the engine turns into an
index seek. **SQL Server has no row-value comparison**, so it gets the portable
`a < @a OR (a = @a AND b < @b)` instead, which reaches the planner as a filter on an index scan: a
deeper page reads and discards more rows. A cursor is still the right way to walk a trail there —
it is stable where an offset is not — but the cost is not flat.

Measured on PostgreSQL, where the difference between the two forms at depth 500,000 of a
million-entry trail was 23ms against 0.1ms. The SQL Server figure is **unmeasured**: the benchmark
only runs PostgreSQL. See [testing.md](testing.md).

## MySQL and MariaDB are not supported

The Dapper store speaks SQL Server, PostgreSQL and SQLite. A fourth dialect is the work; nobody has
asked yet.

## No tamper-proofing

No hash chain, no signing, no append-only enforcement. Anyone with `DELETE` on the table can erase
the trail, and the library's own erasure verbs exist precisely so that deletion is possible. If you
need more, you need database-level controls; claiming otherwise in a `1.0` would be a security lie.

## No point-in-time reconstruction

A history of changes is not a history of states. Rebuilding "the product as it was on 1 March" from
a change list is only correct if the trail is complete from creation, which it never is for a table
that existed before the library was installed. SQL Server temporal tables do this properly.

## Context pooling is not supported

`AddDbContextPool` builds its options once, from the root provider, so they cannot carry the
per-request services the capture needs to know who is acting. Use `AddDbContext`.

## Nothing prunes on its own

There is no hosted service and no timer. The one irreversible thing the library does should be
something an application schedules — see `IAuditManager.PruneAsync`.
