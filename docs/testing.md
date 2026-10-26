# Testing

Two kinds of suite, and the difference matters when reading a claim in these docs.

The layout is the one `DotNetBoost.Settings`, `.Media` and `.MultiLingual` use — organised by
**kind of test**, not by package:

| Project | Tests | TFMs | Needs |
|---|---|---|---|
| `TestSupport` | — | net8.0, net10.0 | The shared doubles and conformance suites. Not a test project |
| `UnitTests` | 189 | net8.0, net10.0 | Nothing. The Core engine, over the in-memory double |
| `ProviderTests` | 238 | net8.0, net10.0 | Nothing. Every store, over SQLite |
| `ApiTests` | 59 | net8.0, net10.0 | Nothing. A real ASP.NET Core host |
| `SurfaceTests` | 7 | net8.0, net10.0 | Nothing. The public surface of all five packages, against a committed baseline |
| `IntegrationTests` | 202 | net10.0 | **Docker.** PostgreSQL 16, SQL Server 2022, MongoDB 7 (replica set) |
| `ScaleTests` | 4 | net10.0 | **Docker, and opt-in.** Measurements on a million-entry trail. Skipped unless asked for, never run by CI |

```bash
dotnet test                                                   # everything — 699 tests
dotnet test tests/DotNetBoost.Auditing.IntegrationTests       # containers only
dotnet test --filter "FullyQualifiedName!~SqlServer"          # skip the slow fixture
```

## Scale is measured, not reasoned about — and it found something

`ScaleTests` builds a million-entry trail and times four things. It is **not run by CI** and is
skipped unless asked for, because every number below depends on the machine and failing a build on
them would mean failing builds nobody can act on:

```bash
DOTNETBOOST_SCALE=run dotnet test tests/DotNetBoost.Auditing.ScaleTests
```

Measured 2026-10-22, PostgreSQL 16 in Docker on an Apple M5 Pro (15 cores, 24 GB), 1,000,000
entries and 1,000,000 property rows, seeded in 12s:

| | |
|---|---|
| One entity's newest 50 (20,000 in its history) | **1.3ms** — 19.9ms with both candidate indexes dropped |
| Count a rare property's changes (500 of 1,000,000) | **0.5ms** — 13.1ms with the index dropped |
| Page 10,000 by cursor (depth 500,000) | **0.1ms** |
| Page 10,000 by offset (`skip=500000`) | **36.0ms** |
| Walk to depth 500,000 by cursor, 50 at a time | **1.4s** for 10,000 pages |
| First prune of 913,613 entries, batches of 1,000 | **7.1s**, 8ms mean per batch, **49ms slowest** |

The last number is the one the batching exists for: the longest a recording could be waiting behind
a sweep is the slowest single batch, and that is 49 milliseconds rather than the length of the
sweep.

**It found a real defect in the keyset paging**, which is the argument for having written it. The
first run measured 23ms for a page at depth and 116 seconds to walk there — nothing like the
constant cost the documentation claimed. `EXPLAIN` said why: the portable
`a < @a OR (a = @a AND b < @b)` reaches the planner as a **Filter** on an index scan, so a deeper
page reads and discards more rows, while the row-value form `(a, b) < (@a, @b)` becomes an
**Index Cond** and seeks. The store now issues row values wherever the engine has them, and the
same page went from 23ms to 0.1ms and the walk from 116s to 1.4s. SQL Server has no row-value
comparison and still gets the OR, so the constant-cost property does not hold there — unmeasured,
because this benchmark only runs PostgreSQL.

Two of the four measurements were also wrong on the first run, in the direction that flatters the
library: dropping one index left a second that could serve the same read, and the per-property
query asked for half the table, where a sequential scan beating an index is correct behaviour and
no evidence about anything. Both are fixed and the comments say what they used to measure.

## Concurrency is tested against PostgreSQL, not SQLite

Four tests in `IntegrationTests` cover what happens when several things use a trail at once: eight
writers on their own connections, eight scopes saving through the EF Core interceptor
simultaneously — every one down the deferred path, which is the most intricate code in the library
and was previously never run concurrently — a prune sweeping while recording continues, and a cursor
walk with an entry arriving between every page.

**PostgreSQL deliberately.** SQLite's shared cache admits one writer, so two connections contending
there teach you about SQLite's locking and nothing about this library: an early attempt at one of
these tests on SQLite was a lock-contention storm that ran for ten minutes and proved nothing.

**No assertion is a duration.** Every one is an invariant — a count, a set, an absence — because a
test that fails when a container is slow gets deleted, and then there is neither a test nor the
confidence. Each worker does a fixed amount of work and stops, so there are no timers and no
cancellation to get wrong.

One of them doubles as evidence for a design decision: replacing the cursor with `Skip` in the
paging test turns 250 milliseconds into more than ten minutes, because every arrival pushes a row
back into the part not yet walked and the walk never reaches an empty page. Offset paging under
concurrent writes is usually described as showing a row twice; it can also simply not finish.

## The public surface is a committed file

Five packages expose fifty-odd public types between them, and nothing in a build objects when one
more appears — so a type added in passing silently becomes a compatibility commitment nobody decided
to make. `SurfaceTests` renders each package's public surface and compares it to `PublicApi.txt`
beside that project, so every addition, removal and re-signing turns up as a diff on a file next to
the code that caused it:

```bash
# an intended change, once you have read the diff
DOTNETBOOST_SURFACE=update dotnet test tests/DotNetBoost.Auditing.SurfaceTests
```

On a mismatch the suite writes `PublicApi.txt.actual` next to the baseline and names what moved, so
the question "what changed" is answered by diffing two files rather than by reading an assertion
message.

It is **not** `Microsoft.CodeAnalysis.PublicApiAnalyzers`, which is the standard answer and fails
the build rather than the suite. Its baseline can only be *created* by its IDE code fix —
`dotnet format` does not apply it — and a file that can only be regenerated inside an IDE does not
belong in a repository that is otherwise driven entirely from the command line. Rendering the format
here also bought two things the analyzer would not have: the baselines are **shared between both
shipped frameworks**, so a public member that exists on net10.0 and not net8.0 fails on the run that
disagrees; and the text reads as C#, because the point is that a person reviews the diff.

The surface is fully qualified and carries nullability, both of which cost verbosity in a file
nobody reads top to bottom and buy a change that would otherwise pass: moving a public type to
another namespace breaks every `using` that named it, and turning a `string` into a `string?` breaks
callers who never checked for null.

## The contract suite runs at both levels

`StoreConformance` lives in `TestSupport`, not in one test project, so the same 28 contract tests are
inherited twice over:

| | in-memory | SQLite | PostgreSQL | SQL Server | MongoDB |
|---|---|---|---|---|---|
| **The double** | ✅ | | | | |
| **EF Core store** | | ✅ | ✅ | ✅ | |
| **Dapper store** | | ✅ | ✅ | ✅ | |
| **MongoDB store** | | | | | ✅ |

The left-hand column is the one that makes the rest of the suites mean anything. The Core engine's
tests run against `InMemoryAuditStore`, and a test built on a double is only worth something if the
double behaves like the real thing — so the double sits the same exam as the stores. Before this
layout the engine's tests rested on a hand-written fake that nothing held to that standard: it could
have drifted from every real store and every test would still have passed. One of them *was*
relying on the fake being unrealistic, and said so the moment the double started copying on write
the way a database does.

The one place the stores legitimately differ — when an appended entry becomes durable — is a
`FlushAsync` hook each subclass implements, so the difference is written down rather than hidden.

`EntityAddressingConformance` is in `TestSupport` for the same reason, but pointed outward: it is the
suite an application running two DotNetBoost libraries derives from to prove they address entities
identically. This repo can only run it against itself, because pointing it at `.Media` would need
the project reference the whole design exists to avoid.

## Why the integration suite exists

Everything the unit suites can reach, they test — and they ran green while three real defects sat in
the code, because each one needed an engine SQLite is not:

- **Dapper does not expand `IN @Ids` on Npgsql.** It rewrites the parameter into
  `IN (@Ids1, @Ids2, …)` on SQL Server and SQLite and does nothing at all on PostgreSQL, which is
  then handed `IN $1` and refuses it. Nothing in the C# says which engine is underneath. Every read
  that loaded property changes, and every delete, was broken for PostgreSQL users; the suite found
  it on its first run. `AuditStatements.AnyOf` now spells it per dialect, and
  `DapperDialectTests` pins the behaviour so the workaround can be removed if Dapper ever changes.
- **The MongoDB store had never spoken to a server.** Its documents, filters and index definitions
  were asserted by serializing and rendering them. They were right — but "the driver produced this
  BSON" and "a server accepted it and did what it says" are different claims, and only the second
  one matters.
- **Neither relational DDL had been executed.** The SQL Server and PostgreSQL scripts were checked
  by searching their text for the object names they ought to contain.

## What the integration suite adds

The contract suite again, against the three real engines — and on top of it:

- **`InterceptorTests`** — the atomicity guarantee against PostgreSQL rather than SQLite, whose
  transactions are a file lock. Including the one that matters: force the deferred audit write to
  fail after the row's INSERT has succeeded, and assert the row is gone again.
- **`CrossStoreTests`** — the Dapper bootstrap creates the schema, EF Core writes through it, Dapper
  reads it back, and the reverse. PostgreSQL has real `uuid` and `timestamptz` types that the two
  stores reach by different routes, which is where "one database serves either" is actually at risk.
- **`DapperDialectTests`** and **`SqlServerSchemaTests`** — the DDL run for real, with the indexes,
  the cascade and the column widths asserted against each server's own catalogue.
- **`MongoDbTests`** — indexes read back from the server, the multikey query the embedding decision
  rests on, and `WithSession` committing and aborting against a replica set.

## It fails without Docker; it does not skip

A container-gated suite that skips silently reports green, which is worse than not having one: the
stores it exists to prove would go on looking tested. `ContainerFixture` fails with a message saying
what is needed and pointing at the unit suites instead.

## SQL Server on arm64

Microsoft publishes no arm64 image, so on Apple Silicon that fixture runs under emulation. It does
work — this suite was developed that way, with the container ready in about twenty seconds — but it
is the slowest fixture and the likeliest to fail on a machine without amd64 emulation. Excluding it
costs nothing else, because its tests are in a collection of their own:

```bash
dotnet test --filter "FullyQualifiedName!~SqlServer"
```

## Still not covered

- **MySQL / MariaDB.** Neither store claims to support them; the Dapper store would need a fourth
  dialect.
- **The dashboard.** `clients/dashboard` has no tests — 2,162 lines of Vue and TypeScript, among
  them the two composables that do the fetching and the paging, the request builder, and the proxy
  that forwards to the audit endpoints. CI lints and type-checks it, so it is known to compile and
  to satisfy ESLint, and nothing beyond that is known. Nothing checks that a filter sends the query
  it displays, or that the table pages the way the cursor underneath it does.
