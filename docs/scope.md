# Scope — DotNetBoost.Auditing

> **Status:** all five packages are built and tested against this document — 493 unit tests on
> net8.0 and net10.0, plus 202 integration tests against PostgreSQL, SQL Server and MongoDB in
> containers. Every store is proven against its real engine; [testing.md](testing.md) says what
> each suite covers and what is still not covered. Written 2026-10-16 before any code existed, so the boundaries were chosen
> rather than discovered — where building changed one, the section says so. §13 lists those; what
> the library cannot do is in [limitations.md](limitations.md) and the HTTP surface is in
> [rest-api.md](rest-api.md).

**Who changed what, when, and from what to what — for any entity in the application, in two
tables, with no column on your entities and no migration when you audit something new.**

```csharp
db.Products.Attach(product);
product.Price = 19.99m;
await db.SaveChangesAsync();   // the audit rows are written in this same transaction
```

```
GET /api/audit?entityName=Product&entityId=42        -> that product's history, newest first, paged
GET /api/audit/{id}                                   -> one entry with its property changes
GET /api/audit?actor=alice@example.com&from=2026-10-01
GET /api/audit/entities                               -> which entities are audited, and what is not
```

---

**It stands alone, like the other three.** No package reference to `DotNetBoost.Settings`, `.Media`
or `.MultiLingual`, no shared kernel, no contract any of them has to implement, and no mention of
their names in the source. It installs into an application that has none of them and works in full.
Integration is an application concern and gets worked out later, in `dotnetboost-sample`, where
composition already lives — not designed into this library ahead of a user who needs it. Everything
below is written to that constraint; where it costs something, the cost is stated.

---

## 1. The decision that shapes everything else

"Audit trails" is two libraries wearing one coat, and this one is only the first:

| | |
|---|---|
| **Entity change history** — who changed which property of which entity, old value → new value | **In scope. This is the library.** |
| **Action / request logging** — who called which endpoint, when, from where, how long it took, what it threw | **Out of scope. Deliberately.** |

Request logging is a solved problem that this suite should not re-solve. OpenTelemetry,
`Microsoft.AspNetCore.HttpLogging` and Serilog all do it better than a data-access library can,
they sample and export where audit rows cannot, and the output belongs in a log sink rather than a
relational table an application queries. ABP ships both under one package, which is a large part of
why `Volo.Abp.AuditLogging` carries four tables, browser-and-client-info columns and an HTTP
dependency in the thing that records a price change.

Entity change history is the half **no observability tool gives you**, because it needs the
before-image of the row, the mapping of properties to columns, and the same transaction as the
write. That is the whole of the library's claim to exist.

**What this costs:** an application that wants "who called `POST /api/orders` at 09:14" gets
nothing from this library and must reach for the tools above. Say so in the README — a user who
installs an auditing package and finds no request log should learn that in thirty seconds, from the
first screen, not from source.

## 2. What it does not do

Each of these is a line someone will ask to have crossed. The answer is no, and the reason is
written down so the answer stays no.

- **No request, action or endpoint logging.** §1.
- **No temporal tables / point-in-time entity reconstruction.** A history of changes is not a
  history of states. Rebuilding "the product as it was on 1 March" from a change list is only
  correct if the trail is complete from creation, which it never is for a table that existed before
  the library was installed. SQL Server temporal tables and `AS OF` queries do this properly; this
  library does not pretend to.
- **No event sourcing, no event bus, no outbox.** Audit rows are a derived record for humans and
  compliance, never the source of truth and never a transport.
- **No approval workflow, no "revert this change" verb.** Reading a trail and acting on it are
  different jobs. A revert needs domain rules the library cannot see.
- **No tamper-proofing in v1** — no hash chain, no signing, no append-only enforcement. A user with
  `DELETE` on the table can erase the trail. Anyone who needs more needs database-level controls, and
  claiming otherwise in a `1.0` would be a security lie. Possible post-1.0 opt-in; see §12.
- **No UI beyond the dashboard.** The REST API and the Nuxt dashboard the other libraries ship, and
  nothing embedded in anyone's admin panel.

## 3. The model: two tables

Exactly the shape `DotNetBoost.Media` uses, for the same reason — two tables serve every entity in
the application, so auditing something new costs an attribute at most.

**`AuditEntries`** — one row per change to one entity, per save.

| Column | Notes |
|---|---|
| `Id` | `Guid`, library-generated |
| `EntityName` | Entity Addressing v1 (§4). Max 128 |
| `EntityId` | The key as its stored string. Max 64 |
| `Action` | `Created` · `Updated` · `Deleted` |
| `Actor` | Max 256. `null` means **not captured**, never a claim (§5) |
| `CorrelationId` | One value per `SaveChanges`, so the several entries of one save are identifiable as one edit. Ambient `Activity.Current?.Id` when there is one |
| `OccurredAt` | UTC |
| `Metadata` | Optional JSON for application-supplied context (tenant, reason, ticket) |

**`AuditPropertyChanges`** — one row per property that actually changed.

| Column | Notes |
|---|---|
| `AuditEntryId` | FK to the entry — the one real foreign key in the schema |
| `PropertyName` | As named on the class, not the column |
| `OldValue` / `NewValue` | `nvarchar(max)`-equivalent, `null` for never-set |
| `IsRedacted` | Set when the property is `[AuditRedact]`; both values `null` (§6) |
| `IsTruncated` | Set when either value was cut short at `MaxValueLength`. Stored rather than inferred: a value that happens to be exactly the limit is not truncated and must not read as though it were |

**Why a property table and not one JSON column on the entry.** A JSON column is smaller and
writes faster, and it makes "every change anyone ever made to `Price`" an unindexable scan in SQL
Server and PostgreSQL alike. Per-property questions are most of why anyone keeps a trail, so they
get rows. The envelope keeps a JSON `Metadata` column because nothing queries it.

**No foreign key from `AuditEntries` to your tables**, the same trade Media makes: two tables serve
every entity, nothing cascades, and deleting an entity leaves its trail standing — which here is a
feature rather than a price. A `Deleted` entry is the most valuable row in the table.

**Table and schema names are configurable** in every store. Settings hardcoded `ToTable("Settings")`
and it is the one place the three libraries disagree; do not add a fourth.

## 4. Entity Addressing v1

Auditing is the **third** implementation of the suite's addressing spec, so it publishes the same
constant under the same name and is held to the same rules:

```csharp
public const string AddressingVersion = "1.0";   // DotNetBoost.Auditing.Core.Metadata.EntityKey
```

Same name resolution (`[AuditEntity("…")]` or the type name, proxies unwrapped), same key
formatting (numbers invariant, GUIDs `"D"`), same `MaxLength` of 64 and 128, same `IsUnset`. That
agreement — and nothing else — is what lets an application join an audit row to a media row or a
translation row on `(EntityName, EntityId)`. Port `EntityKey.cs` and its self-test suite from Media;
do not re-derive it.

`EntityKey.cs` and its self-test suite are **copied** from Media, never referenced as a package.
Three independent implementations asserting the same constant is the design, not duplication to be
cleaned up later: the day one of them references the others, the suite has a shared kernel.

**Composite keys: throw** — and this turned out not to be a disagreement at all. Reading both
siblings while porting `EntityKey.cs` showed that Media and MultiLingual *both* refuse a composite
key, in the convention accessor and in the EF Core one, with the same reasoning: `GetProperties()`
has no guaranteed order, so taking the first key property would address the entity differently
between builds. (The suite note claiming Media takes the first silently is stale; Media's
`ConventionEntityKeyAccessor` throws `NotSupportedException` and its EF accessor throws
`InvalidOperationException`.) Auditing throws too, for a reason of its own on top: nothing ever
rewrites an audit entry, so a trail scattered across two addresses stays wrong permanently. Nothing
is asked of Media, and there is no cross-repo change pending here.

## 5. The actor

```csharp
public interface IAuditActorAccessor
{
    string? GetActor();
}
```

Copy Settings' discipline verbatim, including the comment: **returning a placeholder instead of
`null` turns "nobody captured this" into a claim about who acted.** The default registered by
`AddAuditing()` returns `null`; `DotNetBoost.Auditing.API` ships the `HttpContext`-backed one behind
`.UseHttpContextActor()`.

**This is the second library to need an ambient actor**, which makes the deferred
`DotNetBoost.Abstractions` question live for the first time — see §12. Until it is answered,
Auditing declares its own accessor and depends on nothing.

## 6. Redaction and sensitive data

An auditing library is the single most likely place in an application for a secret to end up in
plain text, forever, in a table nobody thinks to look at.

- `[AuditIgnore]` on a property — never recorded, not even its name.
- `[AuditRedact]` on a property — recorded as changed, both values `null`, `IsRedacted` set.
- `[AuditIgnore]` on a class — the entity is not audited at all. `.IgnoreEntity<T>()` at
  registration is the same rule for a type the application cannot annotate.
- Redaction follows **the attribute**, never whether the value happened to be encrypted at rest, so
  it holds with no encryptor configured. This is the rule Settings' observer got right.
- An opt-in **allow-list mode** (`AuditOnly`) for the entities that want it, because the sane
  default for `Updated` on an unknown entity is still "record everything that changed".
- A `ValueTruncation` limit, defaulted, so one `byte[]` column or a 2MB JSON blob cannot make an
  audit row larger than the row it describes.

GDPR erasure is the user's job, but the library must not make it impossible: `DeleteForAsync(entity)`
and `DeleteForActorAsync(actor)` exist for exactly that, and are documented as the erasure path.

## 7. How changes are captured

Three sources, in descending order of how much the library does for you.

**a. The EF Core interceptor — `DotNetBoost.Auditing.EntityFrameworkCore`. Built, and the guarantee
holds with one documented cost.** A `SaveChangesInterceptor` reads the change tracker and appends
audit rows **into the same `SaveChanges`**, hence the same transaction. Either the change and its
trail both land or neither does. This is the headline feature and the thing Settings' old audit
trail got most wrong — it wrote entries one round trip at a time, each with its own
`SaveChangesAsync`, *after* the write had committed and outside its transaction.

What building it established, beyond what this section assumed:
- **Updates and deletions get it for free.** Their keys already exist while `SavingChanges` runs, so
  their entries go into the caller's single save with no transaction opened to arrange it.
- **A creation with a store-generated key cannot.** Its id does not exist until the INSERT has run,
  so that entry is deferred to `SavedChanges` and a second save — and the interceptor opens a
  transaction across both when the caller has none, so the guarantee survives. A test forces the
  deferred write to fail and asserts the creation is gone again.
- **EF's temporary keys are a trap worth naming.** An `Added` entity is not left at its CLR default:
  EF assigns a large negative placeholder. The first implementation recorded one as an entity's
  address, filing a creation under an id that existed nowhere. `IsTemporary` and `IsKeySet` are the
  signals; `EntityKey.IsUnset` alone cannot see it.
- **Two cases stay non-atomic** — a retrying execution strategy with no caller transaction, and a
  non-relational provider. Both still record. See [limitations.md](limitations.md).

**b. The explicit recorder — `DotNetBoost.Auditing.Core`. Built, with the Dapper store behind it.**
`IAuditManager` / `audit.For(entity).RecordAsync(…)`, for Dapper users and for anything the change
tracker cannot see (a raw `ExecuteUpdate`, a stored procedure, a domain event). No transaction
guarantee unless the caller supplies the transaction, which `WithTransaction(tx)` accepts the way
Media's store does.

Two things building it settled:
- **The same database really does serve either store**, and it is now a test rather than a claim —
  written with EF Core and read with Dapper, and the reverse, including the SQLite representations
  of a GUID, a timestamp and a boolean.
- **Dapper will not convert SQLite's `TEXT` back to a `Guid`**, because `Guid` is not
  `IConvertible`. The store converts its own identity columns rather than registering a global
  `SqlMapper` handler that would change how every `Guid` in the host application is read.

**c. Sibling libraries — later, and not here.** `DotNetBoost.Settings` raises
`ISettingWriteObserver` on every completed write, carrying everything an audit entry needs: group,
per-property diff, actor, correlation id, timestamp, and `IsRedacted` for `[Sensitive]`. An observer
that turns one into audit rows is about forty lines — and those forty lines are **application
code**, the same way the sample's `Integration/MediaText.cs` is. This library ships no bridge
package and names no sibling; Settings ships none naming this library either. It gets written once,
in `dotnetboost-sample`, and only becomes a package if users are seen writing the same glue
repeatedly — ergonomics, not capability.

Worth knowing whenever that is written: `ISettingWriteObserver` logs and swallows exceptions by
contract, since it runs after the write has committed. Entries fed from an observer are therefore
best-effort, while entity entries from (a) are transactional. Not this library's problem to solve,
but the sample should say so out loud rather than let the two look alike.

## 8. Reading

Every read is **bounded**. Settings' `GetHistoryAsync` was unbounded over a table that only grows,
with no paging on the endpoint, and that is on the list of why it was deleted.

```csharp
IAuditManager.QueryAsync(new AuditQuery
{
    EntityName = "Product", EntityId = "42",
    Actor = null, Action = null, From = null, To = null,
    Skip = 0, Take = 50          // Take is clamped to AuditOptions.MaxPageSize; After walks instead
});
```

`GET /api/audit` takes the same filters and the same clamp, returns newest-first, and ships
authorization closed by default — the trail says who did what, which is exactly the data you do not
expose on an open endpoint.

**Built, and the closed default went further than this section promised.** Two things settled while
writing it:
- **The destructive endpoints are not mapped at all** unless asked for, rather than mapped and
  secured. Erasure exists for subject-access deletion requests, not routine use, and a verb that
  deletes a trail should not be published because a library happened to offer it.
- **`DestructivePolicy` is additional, not alternative.** Nesting the destructive endpoints inside
  the read group makes it *impossible* to configure a destructive endpoint that is easier to reach
  than the reads, and means any authorization an application adds to the returned group covers the
  dangerous endpoints too. The price is that a destructive role needs the read policy as well, which
  a test asserts so nobody "fixes" it later.

## 9. Retention

**Audit tables only grow, and nothing else in this suite has this problem.** A library that records
every change and offers no way to stop is a library that fills a disk, so pruning is v1 scope, not a
later nicety:

- `AuditOptions.Retention` — a `TimeSpan?`, `null` meaning keep everything, which stays the default.
- `IAuditManager.PruneAsync(olderThan, ct)` — batched deletes, resumable, safe to run concurrently
  with writes. Per-entity overrides (`[AuditRetention(Days = 90)]`), because an order's trail and a
  user-preference's trail are not kept for the same length of time.
- **`AuditPruneScope`**, which the build added because the design above does not work without it: an
  entity with a longer per-entity retention has to be *excluded* from the global sweep, or the
  global cutoff deletes it first and `[AuditRetention]` silently means the opposite of what it says.
  So `IAuditStore.DeleteOlderThanAsync` takes a scope — one entity, or everything except the named
  ones — rather than a nullable entity name.
- No hosted background service in `.Core`. An opt-in one in `.API` or the Aspire sample, so the
  library never starts a timer in a process that did not ask for it.

## 10. One manager, and what it cost

Recording, reading and retention are one interface, `IAuditManager`, as `ISettingManager`,
`IMediaManager` and `IMultiLingualManager` are in the sibling libraries. One service to inject, one
place to look, `audit.For(x)` the same idiom as `media.For(x)`:

```csharp
await audit.For(product).UpdatedAsync([new AuditChange(nameof(Product.Price), 24.99m, 19.99m)], ct);
var page  = await audit.HistoryAsync(product, ct: ct);
var gone  = await audit.PruneAsync(ct: ct);
```

The engine is still three classes — `AuditRecorder` holds the rules, `AuditReader` the clamp,
`AuditPruner` the cutoffs — because they take different dependencies and have nothing to say to
each other. They are internal, and `AuditManager` forwards to them without logic of its own, so
there is exactly one implementation of redaction and one of the page clamp.

**What this cost, and it is not nothing.** The alternative was three interfaces, one per verb, and
auditing is the one library in this suite where that would have earned its keep. For settings,
media and translations the threat model is a developer making a mistake; nobody tampers with a
translation to cover their tracks. Audit data is the only data in the suite that someone has a
motive to forge or destroy, and that motive belongs to the people the data is about.

With one interface, **"this class displays history and cannot alter it" is no longer something a
constructor can say.** A page that renders a trail is handed the same service that erases one. That
guarantee was never enforcement — a developer with commit access could always have injected the
pruner — but it was legibility: "which code can write to the audit trail?" was answerable by
reading constructors, and widening that privilege showed up in a diff as a signature change rather
than as one line inside a method. Now it is a convention, held up by review.

What keeps standing, and carries more weight for it:
- `IAuditStore` has no update verb and never will (§3). An entry that can be edited is not an
  entry, whatever the service surface looks like.
- The entry's id and timestamp are the engine's. `AuditWrite` has no field for either, so no
  caller backdates a row.
- Nothing prunes on a timer (§9). Deletion is always something an application scheduled.

**What does not stand, and is worth naming here rather than leaving to be found:** the actor is
*not* the engine's last word. `AuditWrite.Actor` overrides the ambient accessor — deliberately, for
a background job acting for a user or an import naming its source — so any code that can record can
also name someone else as the one who acted. The fluent path cannot: `For(x).UpdatedAsync(…)` never
sets it. But `RecordAsync` can, and on this branch every class holding `IAuditManager` has
`RecordAsync`.

**What was bought.** One name to learn instead of three, symmetry with the rest of the suite, and
the end of the question that prompted this section — a developer arriving from `DotNetBoost.Media`
now finds what they went looking for. For a suite whose consumers meet all four libraries, that is
worth more than it would be for a package shipped alone.

## 11. Packages

```
DotNetBoost.Auditing.Core                  attributes · IAuditManager · AuditSubject
                                           IAuditActorAccessor · IAuditStore · options · DI
DotNetBoost.Auditing.EntityFrameworkCore   the SaveChanges interceptor + the EF store
DotNetBoost.Auditing.Dapper                store only; recording is explicit
DotNetBoost.Auditing.MongoDb               store only; recording is explicit
DotNetBoost.Auditing.API                   minimal-API endpoints + UseHttpContextActor()
```

All five exist. One assumption in this list did not survive contact with MongoDB: "two tables"
is a relational decision, and §3's justification for it — that a JSON column is unindexable — is
false in a document database. `.MongoDb` therefore keeps **one** collection with the changes
embedded, indexed on `Changes.PropertyName`, which answers the same question the second table exists
for. The models a caller sees are identical either way.

Still missing, and listed here rather than left to be noticed: `samples/`, `clients/dashboard`,
`aspire/` — all of which the sibling libraries have. The integration suite that used to head this
list now exists, and [testing.md](testing.md) records both what it proved and the three defects it
found that SQLite could not.

`services.AddAuditing()` returning an `AuditingBuilder`, with `.Use*` / `.Disable*` / `.With*`
extension methods — the shape `AddMedia()` and `AddSettings()` already have. Plus `docs/`,
`samples/`, `tests/`, `clients/dashboard`, `aspire/`, `net8.0;net10.0`, MIT,
`1.0.0-preview.1` on NuGet, and **zero dependency on the other three** — no package reference, no
shared contract, none of their names in the source or the `.csproj` files.

Repository: `~/dev/dotnetboost-auditing`, `github.com/dotnetboost/auditing`.

## 12. Post-1.0, explicitly not now

- **Hash-chained entries** — each row carrying the previous row's hash, so deletion is detectable.
  Opt-in, because it serialises writes.
- **Snapshot entries** — the full JSON of the entity alongside the diff, for the
  point-in-time reconstruction §2 refuses to fake.
- **A second actor dimension** — impersonation (`ActorOnBehalfOf`), once someone asks twice.

## 13. Settled by building Core

- **`AuditModelRegistry` and `DeclareEntitiesFrom`.** §7b assumed a write addressed by hand simply
  has no model rules. That is a hole, and the one place a hole is unacceptable: a Dapper application
  addresses everything by name, so a `[AuditRedact]` property reported that way would be written
  down in full. Declared entities give the engine a way from a stored name back to a type, which
  closes it — and the same declaration is what a prune needs to find per-entity retentions. An
  entity that is *not* declared still gets no rules, which the API documents rather than hides.
- **The type travels with the subject.** `For(entity)` resolves the address and passes the CLR type
  along with it. The first version dropped the type after resolving the name, which cost every write
  through `For(entity)` its redaction — invisibly. The test suite caught it; it is now a documented
  property of `AuditSubject` rather than an accident of the call path.
- **One dependency.** Core needs only
  `Microsoft.Extensions.DependencyInjection.Abstractions`: options are validated at `Build()`
  instead of through a hosted service, and the engine has nothing to say at runtime that is not
  either a thrown exception or a recorded entry.
- **`AuditDiff`, and it was the samples that argued for it.** §7b said the explicit path reports its
  own change list, which is true and is not the same thing as writing one out. Both non-EF samples
  grew the identical four-property `if` chain, in two files, and a chain like that is correct the day
  it is written and silently incomplete the day a property is added to the entity: nothing fails, the
  trail simply stops mentioning that property. `AuditDiff.Between` / `InitialValues` / `FinalValues`
  take the property list off the type instead. It is a convenience over the model rather than a
  second set of rules — it skips the key (already the address), skips `[AuditIgnore]` so that an
  update of only ignored properties comes back empty, compares `[AuditRedact]` and leaves the
  withholding to the engine, and skips anything that is not a simple value. That last one is the
  honest cost: a navigation property, a collection or a nested object is skipped **silently**,
  because comparing them by reference would report a change on every write, so an entity whose
  interesting state lives in a nested object still reports it by hand. Key discovery moved into one
  internal place both this and `ConventionEntityKeyAccessor` read — the diff has to leave out exactly
  the property the addressing uses, and two copies of that rule would eventually be two rules.
- **Truncate the correlation id, refuse the actor.** Both are bounded columns, and the two deserve
  opposite answers. An over-long actor is a misconfigured accessor and half an identity is a
  different identity, so it throws. A correlation id is a diagnostic aid, and failing a business
  write because a legacy `Activity` id grew past the column would be the library breaking the
  application over its own bookkeeping.

## 14. Still to settle

1. **`DotNetBoost.Abstractions`.** The sample's decision was: extract `ICurrentUser` /
   `ICurrentTenant` only once a *second* library needs them, from two working consumers, never
   invented for zero. Auditing is that second consumer (§5). The call: **still do not extract it.**
   Ship `IAuditActorAccessor`, let it prove itself against `ISettingActorAccessor` in a real
   application, and extract from two shipped implementations rather than one shipped and one
   planned. Revisit at Auditing 1.1.
2. **Multi-tenancy.** Is `TenantId` a first-class column on `AuditEntries` or just a `Metadata` key?
   Nothing else in the suite has a tenant column, so the consistent answer is `Metadata` — but a
   tenant-filtered audit query is a likely enough read to deserve an index, and adding the column
   later is a migration for every user.
