# Aspire

`aspire/DotNetBoost.Auditing.AppHost` starts the whole stack with one command — two database
engines, all three sample APIs and the dashboard — and injects every address between them:

```bash
dotnet run --project aspire/DotNetBoost.Auditing.AppHost
```

No Aspire CLI is required: `AspireUseCliBundle` resolves the orchestrator and dashboard from NuGet.
`aspire run` does the same thing if you have the CLI.

| Resource | |
|---|---|
| `postgres` | PostgreSQL with pgweb, on a **named volume** — a trail that vanished when the container stopped would demonstrate the opposite of the point |
| `auditing` | The database holding the EF Core sample's tables **and** its trail |
| `auditingdapper` | A second database on the same server, for the Dapper sample — so pgweb reaches both and the two schemas can be compared |
| `mongo` | MongoDB with Mongo Express, on a named volume. A **standalone `mongod`**: transactions need a replica set |
| `auditingmongo` | The database holding the MongoDB sample's documents **and** its trail |
| `api` | `samples/SampleApp` — EF Core store, capture through the interceptor |
| `dapper-api` | `samples/SampleApp.Dapper` — Dapper store, explicit recording, one transaction per request |
| `mongo-api` | `samples/SampleApp.MongoDb` — MongoDB store, explicit recording, no transaction |
| `dashboard` | `clients/dashboard` (Nuxt), with `npm install` run for you |

Each API has a health check, and the dashboard waits for the one it is pointed at.

The shared defaults subscribe to the library's own meter, so the Aspire dashboard's **Metrics** tab
shows `dotnetboost.auditing.entries.recorded`, `.writes.suppressed` and `.entries.deleted` for
whichever API you select, with no wiring in the samples. Create or change a product and the recorded
counter moves; `GET /api/products/{id}` bumps an `[AuditIgnore]` view count, so reading one moves
the **suppressed** counter instead, tagged `audit.reason=NothingChanged`. Those two side by side are
the quickest way to see what [observability.md](observability.md) is for.

## One database, deliberately

Each sample's trail lives in the same database as that sample's own data, and that is the design
rather than a convenience: it is what lets a change and its audit entry commit in one transaction. A
separate audit database would be a second connection and a second commit — exactly the guarantee the
EF Core store exists to provide, and the one the Dapper sample buys back with a transaction. See
[stores.md](stores.md).

## All three stores at once

The three APIs serve the same routes and the same bodies — the DTOs are literally the same files —
over three stores, so the difference between them is something to click through rather than read
about:

| | `api` | `dapper-api` | `mongo-api` |
|---|---|---|---|
| Capture | The interceptor, from the change tracker | Every entry by hand | Every entry by hand |
| Atomic with the change | By default | **With the transaction registered in `Program.cs`** | Not here — needs a session on a replica set |
| `POST /api/products/{id}/fail` | Change and entry are already committed | **Neither** is committed | The change is; the entry was never asked for |

That last row is the whole of [stores.md](stores.md) in three lines, and each one is a `curl` away.

**Switching the EF Core sample's engine.** Exactly one block is active in `AppHost.cs`. To move to
SQL Server: comment the active block, uncomment the alternative, uncomment its `PackageReference` in
the AppHost's project file, and flip the matching blocks in `samples/SampleApp/Program.cs` and
`SampleApp.csproj`. The Dapper sample switches the same way, through its connection factory — the
store reads the dialect from the connection's runtime type.

## What the dashboard is given

```
NUXT_AUDIT_API_URL              → the api resource's http endpoint
NUXT_PUBLIC_API_REFERENCE_URL   → {api}/scalar
NUXT_PUBLIC_MAX_PAGE_SIZE       → 200, matching AuditingOptions.MaxPageSize
```

Swap `api` for `dapperApi` or `mongoApi` in that first line and the dashboard drives that API
instead, with no change to the dashboard itself. It is worth doing once: it is the clearest evidence
that the store is invisible from outside.

No identity is injected: the samples have no authentication and map the audit endpoints with
`RequireAuthorization = false`, so the dashboard's proxy sends the request as it stands. Against a
real API that is where a token would be attached — see [dashboard.md](dashboard.md).
