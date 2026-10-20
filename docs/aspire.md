# Aspire

`aspire/DotNetBoost.Auditing.AppHost` starts the whole stack with one command — the database, the
sample API and the dashboard — and injects every address between them:

```bash
dotnet run --project aspire/DotNetBoost.Auditing.AppHost
```

No Aspire CLI is required: `AspireUseCliBundle` resolves the orchestrator and dashboard from NuGet.
`aspire run` does the same thing if you have the CLI.

| Resource | |
|---|---|
| `postgres` | PostgreSQL 16 with pgweb, on a **named volume** — a trail that vanished when the container stopped would demonstrate the opposite of the point |
| `auditing` | The database holding the sample's tables **and** the trail |
| `api` | `samples/SampleApp`, with a health check |
| `dashboard` | `clients/dashboard` (Nuxt), with `npm install` run for you |

## One database, deliberately

The trail lives in the same database as the sample's own tables, and that is the design rather than
a convenience: it is what lets a change and its audit entry commit in one transaction. A separate
audit database would be a second connection and a second commit — exactly the guarantee the EF Core
store exists to provide. See [stores.md](stores.md).

## Switching the store

Exactly one block is active in `AppHost.cs`. To move to SQL Server: comment the active block,
uncomment the alternative, uncomment its `PackageReference` in the AppHost's project file, and flip
the matching blocks in `samples/SampleApp/Program.cs` and `SampleApp.csproj`.

**MongoDB is deliberately not offered.** The sample records through the EF Core interceptor, and the
MongoDB and Dapper stores record *explicitly* — every change the sample makes by itself would have
to be written out by hand, which would make it a different sample rather than a configuration of
this one.

## What the dashboard is given

```
NUXT_AUDIT_API_URL              → the api resource's http endpoint
NUXT_PUBLIC_API_REFERENCE_URL   → {api}/scalar
NUXT_PUBLIC_MAX_PAGE_SIZE       → 200, matching AuditingOptions.MaxPageSize
```

No identity is injected: the sample has no authentication and maps the audit endpoints with
`RequireAuthorization = false`, so the dashboard's proxy sends the request as it stands. Against a
real API that is where a token would be attached — see [dashboard.md](dashboard.md).
