# DotNetBoost.Auditing — Dashboard

A Nuxt 4 dashboard over the `DotNetBoost.Auditing` REST API: browse a trail, filter it, and see one
entity's history next to the entity itself.

## Running it

The easy way is the Aspire AppHost, which starts the database, the API and this dashboard together
and injects every address:

```bash
dotnet run --project ../../aspire/DotNetBoost.Auditing.AppHost
```

Separately, with the API already running on `http://localhost:5280`:

```bash
cp .env.example .env
npm install
npm run dev
```

## The one thing worth knowing

**The audit endpoints require authorization**, unlike the sibling libraries'. A trail says who
changed what and when, which is a map of an organisation's activity and, through the actor, personal
data about its staff — so `MapAuditEndpoints()` is closed by default.

That means this dashboard cannot read anything without an identity. `server/api/[...path].ts`
attaches one **server-side**, from `NUXT_DEMO_USER`, and the browser never sees it: an identity
shipped to the client would let anyone with the page read the whole trail. A real dashboard forwards
the signed-in user's own token from that same handler.

## What the UI is careful about

- **A missing actor is said, not blanked.** `null` means nobody was captured — a background job, a
  migration, a webhook — and it is rendered as "not captured" rather than an empty cell, because an
  empty cell reads as a missing value rather than a recorded fact.
- **A redacted value is not an empty one.** `•••••` with a badge, and a line saying the values were
  never recorded rather than hidden by the UI.
- **`total: null` is not zero.** The count is opt-in, because it is a second pass over an
  ever-growing index. When it was not requested, paging falls back to "there might be more" instead
  of claiming the end of the trail.
- **The page size stops at the API's `MaxPageSize`**, so the selector never offers a page the engine
  would only clamp.
