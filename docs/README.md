# DotNetBoost.Auditing documentation

[The README](../README.md) has the five-minute quick start, the verbs an application calls, and the
shortest version of what this library is and is not. These pages go further.

## Understanding it

| | |
|---|---|
| [Scope](scope.md) | What the library is, the two halves of "audit trail" and which one this is, the two tables, and everything deliberately left out |
| [Architecture](architecture.md) | The engine, where each rule is applied, the store contract, the packages, the repository layout and how the suites are run |
| [Limitations](limitations.md) | What it cannot do — the two cases that are not atomic, detached updates, owned types, composite keys — and what to do instead |
| [Testing](testing.md) | The two suites, what each proves, and the three defects the container suite found that SQLite could not |

## Using it

| | |
|---|---|
| [Stores](stores.md) | EF Core, Dapper and MongoDB — the same two tables for the first two, and the very different moment each becomes durable. One sample per store |
| [REST API](rest-api.md) | Every endpoint, the two security defaults that differ from the sibling packages, and every status code |
| [Configuration reference](configuration-reference.md) | Every builder method, every option on `AuditingOptions`, the attributes, and each store's own settings |
| [Observability](observability.md) | How to find out the trail stopped being written — the five suppressions, the two log levels, and the three counters |

## Running it

| | |
|---|---|
| [Aspire](aspire.md) | One command for both database engines, all three sample APIs and the dashboard — and why each trail shares its application's database |
| [Dashboard](dashboard.md) | The Nuxt client, and the four ways a trail viewer can quietly mislead |

The three sample APIs document themselves as code you can `curl`:
[`SampleApp`](../samples/SampleApp/README.md) (EF Core),
[`SampleApp.Dapper`](../samples/SampleApp.Dapper/README.md) and
[`SampleApp.MongoDb`](../samples/SampleApp.MongoDb/README.md) — the same routes over the same two
entities, so the difference between them is only ever the store.

## Still to write

These four follow the packages they describe, so they are listed here rather than left to be
discovered missing. None of them is a gap in what is documented, only in where: the material exists
today in the sections on the right, which is also where the README's guide table points.

| Planned page | | Until then |
|---|---|---|
| `recording.md` | `IAuditManager.For(…)` in full, and the three capture sources | [scope.md §7](scope.md#7-how-changes-are-captured), [stores.md](stores.md) |
| `querying.md` | The read verbs, the clamp, and the filters | [scope.md §8](scope.md#8-reading), [rest-api.md](rest-api.md#reading-a-page) |
| `retention.md` | Pruning, per-entity retention, and the erasure path | [scope.md §9](scope.md#9-retention) |
| `redaction.md` | `[AuditIgnore]` vs `[AuditRedact]`, and why the engine owns both | [scope.md §6](scope.md#6-redaction-and-sensitive-data) |

Released changes are in [CHANGELOG.md](../CHANGELOG.md).
