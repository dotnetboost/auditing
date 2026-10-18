# REST API

`DotNetBoost.Auditing.API` maps minimal-API endpoints over a trail.

```csharp
services.AddAuditing()
        .UseEntityFrameworkCore<AppDbContext>()
        .UseHttpContextActor()        // the line that turns a change log into an audit trail
        .Build();

app.MapAuditEndpoints();              // authorized, reads only
```

`UseHttpContextActor()` is the important half. Without it the default accessor returns `null` and
every entry's actor is honestly unknown.

## The endpoints

| | | |
|---|---|---|
| `GET` | `/api/audit` | A filtered, paged page of the trail, newest first |
| `GET` | `/api/audit/{id}` | One entry with its property changes; `404` if there is none |
| `DELETE` | `/api/audit/entity/{entityName}/{entityId}` | Erase one entity's trail. **Opt-in** |
| `DELETE` | `/api/audit/actor/{actor}` | Erase one actor's entries. **Opt-in** |
| `POST` | `/api/audit/prune` | Apply retention. **Opt-in** |

Mount them anywhere with `app.MapAuditEndpoints("/admin/audit")`. The call returns the
`RouteGroupBuilder`, so an application can add its own metadata, filters or authorization — and
whatever it adds covers every endpoint in the group, including the destructive ones.

## Two defaults that differ from the sibling packages

**Authorization is on.** `DotNetBoost.Media` and `DotNetBoost.Settings` map their endpoints
anonymously and leave securing them to the application. This package calls `RequireAuthorization()`
for you, because of what the data is: a trail says who changed what and when, which is a map of an
organisation's activity and, through `Actor`, personal data about its staff. The failure mode of
getting that wrong is not a broken feature, it is a quiet disclosure — so the default is the safe
one and opting out has to be written down:

```csharp
app.MapAuditEndpoints(o => o.RequireAuthorization = false);   // deliberate, and visible in review
app.MapAuditEndpoints(o => o.Policy = "AuditReader");         // or under a policy
```

**The destructive endpoints do not exist** unless asked for — not merely secured, absent. Erasure is
the one irreversible thing the library does, it exists for subject-access deletion requests rather
than routine use, and a verb that deletes a trail is not something to publish because a library
happened to offer it. Most applications should schedule `IAuditPruner` instead of exposing it.

```csharp
app.MapAuditEndpoints(o =>
{
    o.Policy            = "AuditReader";
    o.MapErasure        = true;
    o.MapPrune          = true;
    o.DestructivePolicy = "AuditAdmin";   // required ON TOP of Policy — see below
});
```

`DestructivePolicy` is **additional**, not a replacement: the destructive endpoints are nested
inside the read group, so a caller satisfies both or neither. That is the safe direction — it is
impossible to configure a destructive endpoint that is easier to reach than the reads. Grant the
destructive role the read policy as well; if the two must be genuinely disjoint, map the endpoints
twice under different prefixes.

## Reading a page

```
GET /api/audit?entityName=Product&entityId=42&take=50
GET /api/audit?actor=alice@example.com&from=2026-10-01T00:00:00Z&includeTotal=true
GET /api/audit?action=Deleted&actorMissing=true
GET /api/audit?correlationId=00-7c1f…-01          # the other halves of one edit
```

| Parameter | |
|---|---|
| `entityName`, `entityId` | The address. `entityId` is only meaningful with `entityName` |
| `action` | `Created`, `Updated` or `Deleted`, by name |
| `actor` | Matched exactly. An empty `?actor=` is **not** a filter — it means the box was left blank |
| `actorMissing` | Only entries whose actor was never captured. `actor=` cannot ask this |
| `correlationId` | The other entries of the same edit |
| `from`, `to` | UTC. `from` is inclusive, `to` is **exclusive**, so consecutive windows do not double-count |
| `skip`, `take` | `take` is clamped; omitting it gives the configured default, never everything |
| `includeTotal` | Count the matches as well. Off by default: it is a second pass over the index |
| `includeChanges` | On by default. Turn it off for a wide window, where property rows outnumber entries |

The response reports the page it actually served:

```json
{
  "entries": [
    {
      "id": "7d1b…",
      "entityName": "Product",
      "entityId": "42",
      "action": "Updated",
      "actor": "alice@example.com",
      "correlationId": "00-7c1f…-01",
      "occurredAt": "2026-10-17T09:30:00Z",
      "metadata": null,
      "changes": [
        { "propertyName": "Price", "oldValue": "24.99", "newValue": "19.99", "isRedacted": false, "isTruncated": false }
      ]
    }
  ],
  "total": null,
  "skip": 0,
  "take": 1
}
```

- `action` is a **name**, so a client reads the trail without a lookup table and a renumbering
  cannot change what a stored response meant.
- `total` is `null` when no count was asked for. **`null` means not counted, never zero** — a client
  must not paginate against it.
- `take` is the size served, after clamping. A client that asked for more finds out here.
- `isRedacted` means the two nulls are "not disclosed" rather than "was empty". Say so in the UI
  rather than rendering a blank — and the values are not in the response at all, which a test
  asserts against the whole body rather than the parsed shape.
- `metadata` is passed through as the string the application stored. The library never looked
  inside it, and an endpoint that started parsing it would be the first thing to break when an
  application put something else there.
- `occurredAt` is UTC with a `Z`.

## Status codes

| | |
|---|---|
| `200` | A page, an entry, or a count of what was deleted |
| `401` / `403` | Not authenticated, or the policy was not satisfied |
| `404` | No entry with that id — or an endpoint that was not mapped (`405` where the path shape has only a `GET`) |
| `400` | `ProblemDetails`: an empty or over-long addressing value, a prune age or batch size of zero or less, an entity whose key cannot be addressed |

Only the exceptions that can only mean "the request was wrong" become `400`. An
`InvalidOperationException` from this library usually means a misconfiguration — an actor accessor
returning more than the column holds, a store that is not registered — and reporting that as the
caller's fault would hide a fault the operator needs to see as a `500`.

An erasure of a trail that is not there is `200` with `"deleted": 0`. An entity with no trail and
one that never existed are the same observation, and "nothing to erase" is a successful erasure.
