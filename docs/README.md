# DotNetBoost.Auditing documentation

Start with the [README](../README.md). These pages go further.

## Understanding it

| | |
|---|---|
| [Scope](scope.md) | What the library is, the two halves of "audit trail" and which one this is, the two tables, and everything deliberately left out |
| [Limitations](limitations.md) | What it cannot do — the two cases that are not atomic, detached updates, owned types, composite keys — and what to do instead |
| [Testing](testing.md) | The two suites, what each proves, and the three defects the container suite found that SQLite could not |

## Using it

| | |
|---|---|
| [Stores](stores.md) | EF Core and Dapper — the same two tables, and the very different moment each becomes durable |
| [REST API](rest-api.md) | Every endpoint, the two security defaults that differ from the sibling packages, and every status code |

## Running it

| | |
|---|---|
| [Aspire](aspire.md) | One command for the database, the API and the dashboard — and why the trail shares the application's database |
| [Dashboard](dashboard.md) | The Nuxt client, and the four ways a trail viewer can quietly mislead |

## Still to write

These follow the packages they describe, so they are listed here rather than left to be discovered
missing:

| | |
|---|---|
| `architecture.md` | The engine, the store contract, and where the rules are applied |
| `recording.md` | `IAuditRecorder` in full, and the three capture sources |
| `querying.md` | `IAuditReader`, the clamp, and the filters |
| `retention.md` | `IAuditPruner`, per-entity retention, and the erasure path |
| `redaction.md` | `[AuditIgnore]` vs `[AuditRedact]`, and why the engine owns both |
