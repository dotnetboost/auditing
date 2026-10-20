# Dashboard

`clients/dashboard` is a Nuxt 4 app over the REST API: browse a trail, filter it, and read one
entity's history next to the entity itself. It is a client of the library, not part of it — nothing
in the packages knows it exists.

```bash
dotnet run --project aspire/DotNetBoost.Auditing.AppHost   # starts it with everything else
```

Standalone, against an API already on `http://localhost:5280`:

```bash
cd clients/dashboard && cp .env.example .env && npm install && npm run dev
```

## Pages

| | |
|---|---|
| **Overview** | Counts per action, the newest entries, and a short note on what wrote them |
| **Trail** | The browser: filter by entity, key, action, actor and window; page through; open an entry |
| **Trail → the model** | What the API says about the filtered entity: every property with its rule, and how long entries are kept |
| **Products** | The sample's own entity — create, change a price, delete |
| **Products → one product** | Change the price, with that product's history beside it. It still answers after the product is deleted |

## The model comes from the API, not from here

The entity filter is a list, not a text box, and it is filled from `GET /api/audit/entities`. The
properties shown beside a filtered trail — `SupplierCost` redacted, `ViewCount` ignored — come from
`GET /api/audit/entities/{entityName}`. None of it is written out in the dashboard: these are facts
about the application's classes, and a hand-written copy of them here would be wrong the first time
somebody changed an attribute.

It is also the answer to the two questions a trail provokes and cannot answer itself — *why is this
property never here?* and *why does this one arrive with no values?* — which otherwise take a trip
through the source.

The same response carries `maxPageSize`, so the page-size selector offers what the engine will
actually serve instead of a number kept in step by hand. `NUXT_PUBLIC_MAX_PAGE_SIZE` survives as
the fallback for the moment before the API answers, and for an API with discovery turned off.

Both endpoints can be absent: an application that declares no entities has no inventory to publish,
and `MapDiscovery = false` turns them off outright. The filter falls back to a text box, which is
what it was before. An empty list is never rendered as "nothing is audited", because it does not
mean that.

## The identity problem, and where it is solved

The audit endpoints **require authorization**, unlike the sibling libraries'. So a dashboard cannot
read anything without one, and where that identity lives matters: an identity shipped to the browser
would let anyone with the page read the whole trail.

`server/api/[...path].ts` proxies every `/api/**` call, and that handler is the place for it. The
browser only ever talks to the dashboard's own origin — which also keeps the API free of CORS
configuration — so a token attached there never reaches the client. A real dashboard forwards the
signed-in user's own from exactly that line.

Against the sample it sends nothing, because the sample has no authentication and opens the audit
endpoints with `RequireAuthorization = false`. That is a property of the sample, not of this
dashboard, and not a shape to copy into one pointed at a real API.

## Five things the UI is careful about

Each is a way a trail viewer can quietly mislead:

- **A missing actor is said, not blanked.** `null` means nobody was captured — a background job, a
  migration, a webhook — so it renders as "not captured". An empty cell reads as a missing *value*
  rather than a recorded fact.
- **A redacted value is not an empty one.** `•••••`, a badge, and a line saying the values were
  never recorded rather than hidden by the UI.
- **`total: null` is not zero.** The count is opt-in, because it is a second pass over an
  ever-growing index. When it was not asked for, paging falls back to "there might be more" rather
  than claiming the end of the trail.
- **The page-size selector stops at the API's `MaxPageSize`**, because a larger page would only be
  clamped by the engine.
- **A window excludes its end, and the picker says so.** `to` is exclusive so consecutive ranges
  never double-count, which means a day picked as the end of a range is sent as the midnight
  *after* it — otherwise choosing the 9th would hide everything that happened on the 9th. The
  period button shows the window the way it was picked, the inclusive last day, not that instant.
