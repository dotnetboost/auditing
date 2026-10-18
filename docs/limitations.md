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

## Owned types are not recorded

An owned entity shares its owner's key, so it has no address of its own: its entries would be filed
under the owner's key but the owned type's name, splitting one row's history across two names.
Recording them under the owner instead means merging two tracker entries into one, which the capture
cannot do without knowing which owner property reached it.

So changing only an owned property produces **no entry**. Promote it to its own entity with its own
key if its history matters.

## Composite keys are refused

An entry records a single key. An entity with a composite primary key throws when it is saved, with
the three ways out in the message: a surrogate key, `[AuditIgnore]`, or recording by hand with
`audit.For(name, id)` and a key you compose yourself.

`DotNetBoost.Media` and `DotNetBoost.MultiLingual` refuse composite keys for the same reason, so an
entity that cannot be audited cannot be given media or translations either.

## An explicit entry is durable when you save

With the EF Core store, `IAuditRecorder` **tracks** the entry and does not save it. An entry
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
written to the table in full** — the one failure in this library that is both silent and permanent.

## MongoDB timestamps are milliseconds

BSON dates hold milliseconds; the relational stores keep 100-nanosecond ticks. A timestamp that
round-trips through the MongoDB store comes back truncated. Ordering is unaffected, because the
entry id is the tiebreaker in every sort — but two trails of the same events, one in MongoDB and one
in SQL, will not compare equal to the tick.

## MongoDB atomicity needs a replica set

Multi-document transactions are a replica-set feature, so a standalone `mongod` cannot make an entry
atomic with the application's own write. A single entry is still written whole in one insert, because
its property changes are embedded.

## Not tested under concurrency or at scale

Every store is exercised against its real engine — see [testing.md](testing.md) — but nothing yet
writes from several connections at once, so behaviour under contention is untested rather than
known. Nor has anything put a million entries in a table: the prune is batched and the indexes are
chosen for the reads the library issues, but that choice is reasoning, not measurement.

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
something an application schedules — see `IAuditPruner`.
