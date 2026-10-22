# Observability

**How to find out that the trail stopped being written.** Everything else in these docs is about
what gets recorded; this page is about the opposite, which is the failure that actually costs you
something.

The engine declines to record a change in five cases, all of them correct:

| Reason | Why |
|---|---|
| `RecordingDisabled` | `Enabled` is off — the kill switch |
| `EntityIgnored` | the entity is `[AuditIgnore]` |
| `NotOptedIn` | opt-in is on and the entity is not `[Audited]` |
| `EntityNotDeclared` | opt-in is on and nothing is declared under the entity's name |
| `NothingChanged` | an update with no recordable property left — nothing was reported, or every reported property is ignored |

`RecordAsync` returns `null` to say so. On the explicit path a caller may check it. **On the EF Core
path nobody can:** the interceptor records changes nobody asked it about, so there is no return
value anywhere for it to hand back.

That is the whole problem. An application that stops being audited — opt-in turned on, `[AuditIgnore]`
reaching a base class, `DeclareEntitiesFrom` forgotten — carries on looking healthy. Logs clean,
health checks green, and the trail simply stops mentioning a table. It is found months later by
someone looking for a change that was never recorded, and an audit trail is the one thing that
cannot be backfilled: the window between the mistake and the discovery is gone for good.

Two mechanisms close it, and they answer different questions.

## The observer: why did *this* write vanish

```csharp
services.AddAuditing()
        .UseEntityFrameworkCore<AppDbContext>()
        .UseLoggingObserver()        // DotNetBoost.Auditing.API
        .Build();
```

Every suppression is reported with its reason, by the interceptor as well as by the explicit path.
The levels are chosen by whether the case is usually a decision or usually a mistake, because that
is the only thing that makes this readable in a real log:

| Level | Reasons | Why |
|---|---|---|
| `Debug` | `RecordingDisabled`, `EntityIgnored`, `NotOptedIn`, `NothingChanged` | Configured intent. A high-churn ignored entity reports one on **every save** — at any higher level this is the noisiest thing in the application and the first thing an operator filters out, which would take the two that matter with it |
| `Warning` | `EntityNotDeclared`, and a write recorded with no model rules | Nearly always a misconfiguration: the source says the entity is audited and the table disagrees |

`EntityNotDeclared` is deliberately its own reason rather than part of `NotOptedIn` — it means a
missing declaration rather than a decision, and the two warrant different levels.

The sixth report is not a suppression at all. **A write recorded with no model rules** means the
write was addressed by name, nothing is declared under that name, and the entry went in *without*
`[AuditIgnore]` and `[AuditRedact]` — so if that entity has a redacted property, its value is now in
the table in full. The engine cannot tell whether it does, because without a declared type there
are no attributes to read, so it reports what it knows. This is the only failure in the library
that cannot be undone, and one line at `Build()` prevents it:
`DeclareEntitiesFrom(typeof(Product).Assembly)`.

For anything other than logging — a counter of your own, a health check, an alert — implement
`IAuditObserver` and register it with `.UseObserver<T>()`. Two methods, and the interface says what
an implementation may not do: **throw, or block.** Both are called on the recording path,
synchronously, including from inside an EF Core `SaveChanges`, so an exception there fails the
business write that was being audited.

## The counters: is the trail being written at all

```csharp
builder.Services.AddOpenTelemetry()
       .WithMetrics(metrics => metrics.AddMeter(AuditingTelemetry.MeterName));
```

| Instrument | Unit | Tags |
|---|---|---|
| `dotnetboost.auditing.entries.recorded` | `{entry}` | `audit.entity`, `audit.action` |
| `dotnetboost.auditing.writes.suppressed` | `{write}` | `audit.entity`, `audit.reason`, `audit.action` |
| `dotnetboost.auditing.entries.deleted` | `{entry}` | `audit.operation` — `prune` or `erase` |

**The first one is what you alert on, and it is the absence that matters.** "This entity recorded
nothing for an hour" is the shape of every way a trail stops covering a table, and it is a question
only this counter can answer: a suppression report is about the writes that *were* declined and says
nothing about the ones that should have arrived and did not. Read the second against the first — a
ratio that moves is a configuration that changed.

The counters are also the only form in which the Debug-level suppressions are visible in
production, where Debug is off.

Nothing is emitted until something subscribes. Each instrument is asked whether it is enabled
before any tag is built, so an unsubscribed counter is a field read and a branch.

**The Aspire defaults already subscribe**, so `dotnet run --project aspire/DotNetBoost.Auditing.AppHost`
shows these for all three sample APIs with no further wiring — see [aspire.md](aspire.md).

### Two things deliberately not tagged

`audit.entity` is bounded by the application's model, which is what makes it safe. **The entity's
key is not a tag anywhere, and neither is the actor.** Both are unbounded, and one unbounded tag
turns a counter into a time series per row or per person and takes the metrics backend with it — and
the actor is personal data that the rest of the library is careful about. Both are sitting right
there on every write, which is why it is the mistake worth guarding, and there is a test that
asserts no measurement carries either.

### What is metered and what is logged

A counter is for what you watch as a rate. A log line is for what you fix once. That is why the two
misconfigurations are warnings rather than instruments of their own: they are a property of a
deployment, not a signal that varies, and an instrument per log line is a dashboard nobody reads.

`entries.recorded` counts what the engine handed to the store, which is **not** the same as what is
committed: the EF Core store only tracks an entry, and the caller's `SaveChanges` makes it durable,
so a rolled-back save counts an entry that no longer exists. Metering the commit instead would mean
the engine knowing when each store becomes durable, which is exactly what `IAuditStore` leaves to
the store — see [stores.md](stores.md).

## Both are off unless you ask

The observer registered by `AddAuditing()` does nothing, and no counter is emitted until a
subscriber appears. That is the deliberate default — a library should not acquire a logging
dependency or start emitting telemetry on an application's behalf — and it is also the thing worth
writing down: the two lines on this page are the difference between finding out on the first write
and finding out from the table.

Core keeps its single NuGet dependency either way. `Meter` and `IMeterFactory` are in the shared
framework on both target frameworks, so the counters cost nothing; the `ILogger` observer is in
`DotNetBoost.Auditing.API`, where the logging dependency already is.
