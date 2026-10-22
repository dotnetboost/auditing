# Testing

Two kinds of suite, and the difference matters when reading a claim in these docs.

The layout is the one `DotNetBoost.Settings`, `.Media` and `.MultiLingual` use — organised by
**kind of test**, not by package:

| Project | Tests | TFMs | Needs |
|---|---|---|---|
| `TestSupport` | — | net8.0, net10.0 | The shared doubles and conformance suites. Not a test project |
| `UnitTests` | 181 | net8.0, net10.0 | Nothing. The Core engine, over the in-memory double |
| `ProviderTests` | 230 | net8.0, net10.0 | Nothing. Every store, over SQLite |
| `ApiTests` | 59 | net8.0, net10.0 | Nothing. A real ASP.NET Core host |
| `SurfaceTests` | 7 | net8.0, net10.0 | Nothing. The public surface of all five packages, against a committed baseline |
| `IntegrationTests` | 198 | net10.0 | **Docker.** PostgreSQL 16, SQL Server 2022, MongoDB 7 (replica set) |

```bash
dotnet test                                                   # everything — 556 tests
dotnet test tests/DotNetBoost.Auditing.IntegrationTests       # containers only
dotnet test --filter "FullyQualifiedName!~SqlServer"          # skip the slow fixture
```

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
- **Concurrency.** Nothing yet writes from several connections at once, so the stores' behaviour
  under contention is untested rather than known.
- **Scale.** The prune is batched and the indexes are chosen for the reads the library issues, but
  no test has put a million entries in a table to find out whether that was the right guess.
