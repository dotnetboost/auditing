using Dapper;
using DotNetBoost.Auditing.API;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.Core.Services;
using Npgsql;
using SampleApp;
using SampleApp.Dapper.Data;
using SampleApp.Dapper.Domain;
using SampleApp.Dtos;
using Scalar.AspNetCore;
using System.Data;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry, health checks, service discovery and resilient HttpClients.
builder.AddServiceDefaults();

// "auditingdapper" is the resource name from aspire/DotNetBoost.Auditing.AppHost/AppHost.cs — a
// second database on the same PostgreSQL container the EF Core sample uses, so pgweb reaches both
// and the two schemas can be compared side by side. They are identical, which is exactly the claim
// the Dapper store makes. Run this sample on its own and you supply it yourself:
//   ConnectionStrings__auditingdapper="Host=localhost;Database=auditingdapper;Username=postgres;Password=…"
var connectionString = builder.Configuration.GetConnectionString("auditingdapper")
    ?? throw new InvalidOperationException(
        "No 'auditingdapper' connection string. Run this through aspire/DotNetBoost.Auditing.AppHost, "
        + "which starts PostgreSQL and injects it — or point it at a PostgreSQL database yourself; "
        + "see samples/SampleApp.Dapper/README.md.");

// ─────────────────────────────────────────────────────────────────────────────
// Auditing. The whole configuration of it — the EF Core sample's chain with ONE line changed:
// UseDapper(...) instead of UseEntityFrameworkCore<AppDbContext>().
//
// There is no AddDbContext above it and no UseAuditing(sp) anywhere, because there is nothing to
// intercept: a Dapper application has no change tracker, so nothing can read a before-image or
// notice that a row moved. Every entry in this sample is written by hand, below.
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddAuditing(options =>
    {
        // Entries are kept for a year, except where an entity says otherwise — StockCheck keeps
        // its own for thirty days. Nothing is deleted until a prune runs; see /api/maintenance/prune.
        options.Retention = TimeSpan.FromDays(365);

        // A page of a trail is at most this many entries, whatever a caller asks for.
        options.MaxPageSize     = 200;
        options.DefaultPageSize = 50;
    })
    // The connection factory's runtime type is what selects the dialect: NpgsqlConnection here, and
    // SqlConnection or SqliteConnection would need no other change. UseDapper registers it as a
    // scoped IDbConnection, so the container disposes it and everything below — the endpoints, the
    // initializer — shares the very connection the library's store uses. That shared connection is
    // what makes one transaction possible.
    //
    // migrateSchema: true creates AuditEntries and AuditPropertyChanges at startup if they are
    // absent, in the shape the EF Core store maps. It is the right setting for a sample and for an
    // application with no migrations of its own; leave it off when migrations or a deployment step
    // own your schema, and get the DDL to check in from DapperSchema.Script(dialect) instead. It
    // only ever CREATEs — it will not migrate a table whose shape has changed. The sample's OWN
    // tables are not its business: Data/DbInitializer.cs creates those.
    .UseDapper(_ => new NpgsqlConnection(connectionString), migrateSchema: true)
    // This sample has no authentication, so there is no signed-in user to record: every entry is
    // attributed to "system". SystemActorAccessor.cs is the EF Core sample's file, compiled into
    // this project — an application with users registers .UseHttpContextActor() here instead and
    // gets the real actor from one claim.
    .UseActorAccessor<SystemAuditActorAccessor>()
    // ── NOT OPTIONAL HERE ───────────────────────────────────────────────────
    // The EF Core sample needs this line for one endpoint. This sample needs it for the reason
    // every Dapper application does: a write addressed by name — audit.For("Product", "42"), which
    // is what a composite key or a row changed by a stored procedure forces — has no type for the
    // engine to read [AuditRedact] and [AuditIgnore] from, and a prune has no per-entity retention
    // to find. For such a write, undeclared means a redacted property is written to the table in
    // full, which is the one failure in this library that is both silent and permanent.
    //
    // Every write below carries its type, so it is the prune and the discovery endpoint that would
    // notice this line going missing. The redaction would notice one refactor later, and nobody
    // would notice that.
    //
    // It is also what GET /api/audit/entities answers with, so the dashboard's model page is empty
    // without it.
    .DeclareEntitiesFrom(typeof(Product).Assembly)
    // Logs what the engine decided NOT to record, which is otherwise invisible: a null return is
    // the only signal, and the interceptor has no caller to hand it to. Warnings for the two cases
    // that are usually a misconfiguration, Debug for the rest. The three counters need no line
    // here — they are on the meter named by AuditingTelemetry.MeterName, which the Aspire defaults
    // subscribe to, so they appear in the dashboard while this app runs.
    .UseLoggingObserver()
    .Build();

// ─────────────────────────────────────────────────────────────────────────────
// One transaction per request — the atomicity the EF Core store gets for free.
//
// Registering UnitOfWork's transaction as the scope's IDbTransaction is all it takes: UseDapper
// resolves one when it builds its store, so a row's UPDATE and its entry's INSERTs land in the same
// transaction with nothing in the endpoints saying so. See Data/UnitOfWork.cs, and
// POST /api/products/{id}/fail for it working.
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddScoped<UnitOfWork>();
builder.Services.AddScoped<IDbTransaction>(sp => sp.GetRequiredService<UnitOfWork>().Current);

// Readiness, as AddDbContextCheck<AppDbContext>() is for the EF Core sample.
builder.Services.AddHealthChecks()
    .AddCheck("postgresql", new NpgsqlHealthCheck(() => new NpgsqlConnection(connectionString)));

// Npgsql emits its own ActivitySource, so every statement below shows up as a span on the Aspire
// dashboard with no extra package — the library's own INSERTs included.

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

// The sample's own two tables. The library's two are created by the hosted service that
// migrateSchema: true registered, which runs as app.Run() starts the host — the two sets never
// meet, so the order does not matter.
await DbInitializer.InitializeAsync(app.Services);

app.UseExceptionHandler();
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// Commits the request's transaction when the response is a success. Registered after
// UseExceptionHandler so that a failure it turns into ProblemDetails still arrives back here as a
// 4xx/5xx — which is not a commit. See Data/UnitOfWorkMiddleware.cs.
app.UseMiddleware<UnitOfWorkMiddleware>();

// ─────────────────────────────────────────────────────────────────────────────
// The library's own endpoints: read the trail, and (opt-in) erase or prune it. Identical to the EF
// Core sample's — reading a trail knows nothing about how it was written.
// ─────────────────────────────────────────────────────────────────────────────
app.MapAuditEndpoints(options =>
{
    // Unlike the sibling libraries, these REQUIRE AUTHORIZATION by default — a trail says who
    // changed what, which is not data to serve anonymously. This sample has no authentication, so
    // the default has to be turned off explicitly or every read here answers 401.
    //
    // DO NOT COPY THIS LINE. It is one line in a sample with no users and no real data; in an
    // application it serves the whole trail, and through its actors personal data about staff, to
    // anyone who can reach the port.
    options.RequireAuthorization = false;

    // Both off by default. On here because the sample exists to show them — in an application,
    // think twice: these delete history.
    options.MapErasure = true;
    options.MapPrune   = true;
});

// ─────────────────────────────────────────────────────────────────────────────
// This sample's own endpoints. The same routes and the same bodies as samples/SampleApp — the DTOs
// are literally its files — so clients/dashboard can be pointed at either API without a change.
//
// What differs is everything about the capture. In the EF Core sample no endpoint mentions
// auditing; here every one of them does, because nothing else can: a SELECT is the only
// before-image available, and an entry exists only because a line below writes it.
//
// Under /api, like the library's own, so clients/dashboard can reach both through one proxy.
// ─────────────────────────────────────────────────────────────────────────────
var products = app.MapGroup("/api/products").WithTags("Products");

products.MapGet("/", async (
    IDbConnection connection,
    UnitOfWork unitOfWork,
    string? category,
    CancellationToken ct) =>
{
    const string sql = """
        SELECT "Id", "Name", "Price", "Category", "SupplierCost", "ViewCount"
        FROM "Products"
        WHERE (@Category IS NULL OR "Category" = @Category)
        ORDER BY "Id"
        """;

    var found = await connection.QueryAsync<Product>(
        new CommandDefinition(sql, new { Category = category }, unitOfWork.Current, cancellationToken: ct));

    return Results.Ok(found.Select(ToResponse));
})
.WithSummary("Lists products. A read records nothing, here as in the EF Core sample.");

products.MapGet("/{id:int}", async (
    IDbConnection connection,
    UnitOfWork unitOfWork,
    IAuditManager audit,
    int id,
    CancellationToken ct) =>
{
    var product = await FindAsync(connection, unitOfWork, id, ct);

    if (product is null)
        return Results.NotFound();

    const string bump = """UPDATE "Products" SET "ViewCount" = "ViewCount" + 1 WHERE "Id" = @Id""";

    await connection.ExecuteAsync(
        new CommandDefinition(bump, new { Id = id }, unitOfWork.Current, cancellationToken: ct));

    // ── An ignored property, reported deliberately ───────────────────────────
    // The EF Core sample demonstrates [AuditIgnore] by not having to think about it: the
    // interceptor drops the property and no entry is ever written. The explicit path can only
    // demonstrate it by trying — so this reports the view count and the engine throws it away.
    //
    // The call returns null. Every reported property was ignored, and an Updated write of nothing
    // records nothing, so there is no view-count noise in the trail afterwards. That is the point:
    // the model's rules are applied in the engine rather than in the capture source, so an
    // application cannot opt out of them by addressing the write itself.
    _ = await audit.For(product).UpdatedAsync(
        [new AuditChange(nameof(Product.ViewCount), product.ViewCount, product.ViewCount + 1)], ct: ct);

    product.ViewCount++;

    return Results.Ok(ToResponse(product));
})
.WithSummary("Gets one product, and reports its [AuditIgnore] view count — which the engine drops.")
.ProducesProblem(StatusCodes.Status404NotFound);

products.MapPost("/", async (
    IDbConnection connection,
    UnitOfWork unitOfWork,
    IAuditManager audit,
    CreateProduct body,
    CancellationToken ct) =>
{
    var product = new Product
    {
        Name         = body.Name,
        Price        = body.Price,
        Category     = body.Category,
        SupplierCost = body.SupplierCost
    };

    const string sql = """
        INSERT INTO "Products" ("Name", "Price", "Category", "SupplierCost", "ViewCount")
        VALUES (@Name, @Price, @Category, @SupplierCost, 0)
        RETURNING "Id"
        """;

    // The key comes back from RETURNING, and it has to exist before the entry is recorded: an entry
    // is addressed by it. The EF Core store has the same problem with a store-generated key and
    // solves it by deferring the entry to a second save inside a transaction it opens itself; here
    // the transaction is already open, so the INSERT and the entry are two statements in it.
    product.Id = await connection.ExecuteScalarAsync<int>(
        new CommandDefinition(sql, product, unitOfWork.Current, cancellationToken: ct));

    // The initial values, as NewValue — read off the entity rather than listed, so a new property
    // is in the trail the day it is added to Product. For(product) carries the type, so SupplierCost
    // is recorded as changed with its values withheld.
    await audit.For(product).CreatedAsync(AuditDiff.InitialValues(product), ct: ct);

    return Results.Created($"/api/products/{product.Id}", ToResponse(product));
})
.WithSummary("Creates a product and records the creation explicitly.");

products.MapPut("/{id:int}", async (
    IDbConnection connection,
    UnitOfWork unitOfWork,
    IAuditManager audit,
    int id,
    UpdateProduct body,
    CancellationToken ct) =>
{
    // ── The before-image, read by hand ───────────────────────────────────────
    // This SELECT is the biggest single difference between this sample and the EF Core one. There it
    // loads the entity so that EF has a before-image; here it loads the entity BECAUSE there is no
    // other way to know what the values were — and a trail that cannot say what a property changed
    // from is most of the value gone. A blind UPDATE is one round trip cheaper and records nothing
    // worth reading.
    var before = await FindAsync(connection, unitOfWork, id, ct);

    if (before is null)
        return Results.NotFound();

    var after = new Product
    {
        Id           = before.Id,
        Name         = body.Name         ?? before.Name,
        Price        = body.Price        ?? before.Price,
        Category     = body.Category     ?? before.Category,
        SupplierCost = body.SupplierCost ?? before.SupplierCost,
        ViewCount    = before.ViewCount
    };

    // Only the properties that actually moved, which the change tracker works out for the EF Core
    // sample and AuditDiff works out from the type here. Written out as four `if`s it worked just as
    // well until someone added a fifth property to Product and the trail quietly stopped mentioning
    // it — which is the failure this call is here to remove.
    //
    // It compares the properties the engine reasons about: not Id, which is already the entry's
    // address, and not the [AuditIgnore] view count, so a request that moved nothing else comes back
    // empty rather than as an entry that says a change happened and never which. SupplierCost IS
    // compared — [AuditRedact] withholds the values from the table, not from the comparison.
    var changes = AuditDiff.Between(before, after);

    // The engine would refuse an Updated write of nothing anyway; returning here also saves the
    // UPDATE and the round trip.
    if (changes.Count == 0)
        return Results.Ok(ToResponse(before));

    const string sql = """
        UPDATE "Products"
        SET "Name" = @Name, "Price" = @Price, "Category" = @Category, "SupplierCost" = @SupplierCost
        WHERE "Id" = @Id
        """;

    await connection.ExecuteAsync(
        new CommandDefinition(sql, after, unitOfWork.Current, cancellationToken: ct));

    await audit.For(after).UpdatedAsync(changes, ct: ct);

    return Results.Ok(ToResponse(after));
})
.WithSummary("Updates a product. The before-image comes from a SELECT, because nothing else has one.")
.ProducesProblem(StatusCodes.Status404NotFound);

products.MapDelete("/{id:int}", async (
    IDbConnection connection,
    UnitOfWork unitOfWork,
    IAuditManager audit,
    int id,
    CancellationToken ct) =>
{
    var product = await FindAsync(connection, unitOfWork, id, ct);

    if (product is null)
        return Results.NotFound();

    const string sql = """DELETE FROM "Products" WHERE "Id" = @Id""";

    await connection.ExecuteAsync(
        new CommandDefinition(sql, new { Id = id }, unitOfWork.Current, cancellationToken: ct));

    // The final values, as OldValue — and worth the trouble, because after the DELETE this entry is
    // the only copy of what the row said, which is the one entry where listing the properties by
    // hand and forgetting one cannot be repaired afterwards. Nothing cascades to the trail: the
    // audit tables carry no foreign key to Products, so the history survives the row by design
    // rather than by luck.
    await audit.For(product).DeletedAsync(AuditDiff.FinalValues(product), ct: ct);

    return Results.NoContent();
})
.WithSummary("Deletes a product. Its trail outlives it, with the final values.")
.ProducesProblem(StatusCodes.Status404NotFound);

products.MapGet("/{id:int}/history", async (
    IDbConnection connection,
    UnitOfWork unitOfWork,
    IAuditManager audit,
    int id,
    // Nullable, with defaults applied below. A non-nullable int here would make both REQUIRED query
    // parameters — minimal APIs reads "not nullable" as "must be supplied" — and the endpoint would
    // answer 400 to the obvious call.
    int? skip,
    int? take,
    CancellationToken ct) =>
{
    var product = await FindAsync(connection, unitOfWork, id, ct);

    // Addressed by hand when the row is gone: the trail outlives the entity, so a history endpoint
    // that needed the entity to exist could not show the deletion.
    //
    // Zero means "the configured default page", which the manager applies — this endpoint does not
    // get to invent a page size of its own.
    var page = product is null
        ? await audit.QueryAsync(
            new AuditQuery
            {
                EntityName = "Product",
                EntityId   = id.ToString(CultureInfo.InvariantCulture),
                Skip       = skip ?? 0,
                Take       = take ?? 0
            }, ct)
        : await audit.HistoryAsync(product, skip ?? 0, take ?? 0, ct);

    // The API package's wire shape, not the store model: AuditAction comes out as a name, and the
    // response carries the page size actually served after the clamp.
    return Results.Ok(AuditPageResponse.From(page, skip ?? 0));
})
.WithSummary("One product's trail, newest first — including a deleted one's.");

products.MapPost("/discount", async (
    IDbConnection connection,
    UnitOfWork unitOfWork,
    IAuditManager audit,
    ApplyDiscount body,
    CancellationToken ct) =>
{
    if (body.Percent is <= 0 or >= 100)
        return Results.Problem("Percent must be between 0 and 100.", statusCode: StatusCodes.Status400BadRequest);

    var factor = 1m - (body.Percent / 100m);

    const string select = """
        SELECT "Id", "Name", "Price", "Category", "SupplierCost", "ViewCount"
        FROM "Products"
        WHERE "Category" = @Category
        ORDER BY "Id"
        """;

    var affected = (await connection.QueryAsync<Product>(new CommandDefinition(
        select, new { body.Category }, unitOfWork.Current, cancellationToken: ct))).ToList();

    if (affected.Count == 0)
        return Results.Ok(new AffectedResponse(0));

    // In the EF Core sample this endpoint is the exception — the one write its change tracker cannot
    // see, recorded explicitly to make the point. Here it is not an exception at all: a set-based
    // UPDATE is recorded exactly like every other endpoint in this file, because every endpoint in
    // this file records the same way. That symmetry is the honest summary of the Dapper store.
    //
    // The predicate is the category rather than a list of ids, on purpose: Dapper does not expand
    // `IN @Ids` on Npgsql — PostgreSQL wants `= ANY(@Ids)` — which was a real defect in the
    // library's own store until the container suite found it. See docs/stores.md.
    //
    // The round() is not decoration. Price is numeric(18,2), so the database would round the product
    // for itself and store 17.99 where the entry below said 17.991 — a trail that is wrong about the
    // value it claims the row now holds, which is the one thing a trail must not be. Rounding in the
    // statement and in the entry, by the same rule, is what keeps the two in agreement:
    // PostgreSQL's round() on numeric is half away from zero, which is MidpointRounding.AwayFromZero.
    const string update = """
        UPDATE "Products" SET "Price" = round("Price" * @Factor, 2) WHERE "Category" = @Category
        """;

    await connection.ExecuteAsync(new CommandDefinition(
        update, new { body.Category, Factor = factor }, unitOfWork.Current, cancellationToken: ct));

    foreach (var product in affected)
    {
        var discounted = decimal.Round(product.Price * factor, 2, MidpointRounding.AwayFromZero);

        // For(product) carries the type, so [AuditRedact] applies here whatever the chain above
        // says. An endpoint that composed audit.For("Product", id) instead — which is what a key
        // assembled from several columns, or a row no instance was loaded for, forces — would
        // depend entirely on DeclareEntitiesFrom for that.
        await audit.For(product).UpdatedAsync(
            [new AuditChange(nameof(Product.Price), product.Price, discounted)],
            metadata: $$"""{"reason":"bulk discount","percent":{{body.Percent}}}""",
            ct);
    }

    return Results.Ok(new AffectedResponse(affected.Count));
})
.WithSummary("Discounts a category with one UPDATE, and records an entry per product.")
.ProducesProblem(StatusCodes.Status400BadRequest);

products.MapPost("/{id:int}/fail", async (
    IDbConnection connection,
    UnitOfWork unitOfWork,
    IAuditManager audit,
    int id,
    CancellationToken ct) =>
{
    var product = await FindAsync(connection, unitOfWork, id, ct);

    if (product is null)
        return Results.NotFound();

    const string sql = """UPDATE "Products" SET "Price" = "Price" + 1 WHERE "Id" = @Id""";

    await connection.ExecuteAsync(
        new CommandDefinition(sql, new { Id = id }, unitOfWork.Current, cancellationToken: ct));

    await audit.For(product).UpdatedAsync(
        [new AuditChange(nameof(Product.Price), product.Price, product.Price + 1m)], ct: ct);

    // ── The guarantee, demonstrated ─────────────────────────────────────────
    // The price change and its entry have both been WRITTEN and neither has been committed: they
    // are two statements in the request's transaction. This throw means UnitOfWorkMiddleware never
    // commits it and the scope rolls it back, so afterwards the database holds neither — the price
    // is unchanged and the trail says nothing happened, which is the truth.
    //
    // This is the mirror image of the same endpoint in the EF Core sample, where the save had
    // already committed before the throw. Both are correct, and the difference is the whole of
    // docs/stores.md: there the guarantee comes from joining the caller's SaveChanges, here from the
    // transaction registered in Program.cs. Take that registration out and this endpoint leaves an
    // entry for a change that never happened — which is worse than no entry, because a reader
    // cannot tell.
    throw new InvalidOperationException(
        "Deliberate failure, before the commit. Check the price and the trail: NEITHER moved, "
        + "because the change and its entry are in one transaction and it was rolled back.");
})
.WithSummary("Changes a price, records it, then throws — and the transaction takes both back.")
.ProducesProblem(StatusCodes.Status404NotFound);

// ─────────────────────────────────────────────────────────────────────────────
// Stock checks: the noisy entity, kept for thirty days rather than the configured year.
// ─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/stock-checks", async (
    IDbConnection connection,
    UnitOfWork unitOfWork,
    IAuditManager audit,
    RecordStockCheck body,
    CancellationToken ct) =>
{
    var check = new StockCheck
    {
        ProductId = body.ProductId,
        OnHand    = body.OnHand,
        CheckedAt = DateTime.UtcNow
    };

    const string sql = """
        INSERT INTO "StockChecks" ("ProductId", "OnHand", "CheckedAt")
        VALUES (@ProductId, @OnHand, @CheckedAt)
        RETURNING "Id"
        """;

    check.Id = await connection.ExecuteScalarAsync<int>(
        new CommandDefinition(sql, check, unitOfWork.Current, cancellationToken: ct));

    // CheckedAt is in here too, which the hand-written list it replaced left out — the EF Core
    // sample's interceptor has always recorded it, so the three samples now say the same thing about
    // the same entity.
    await audit.For(check).CreatedAsync(AuditDiff.InitialValues(check), ct: ct);

    return Results.NoContent();
})
.WithTags("Products")
.WithSummary("Records a stock check — audited, but kept for 30 days rather than a year.");

// ─────────────────────────────────────────────────────────────────────────────
// Retention. The library never prunes on its own: there is no hosted service and no timer, because
// the one irreversible thing it does should be something an application schedules.
// ─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/maintenance/prune", async (IAuditManager audit, int? batchSize, CancellationToken ct) =>
{
    var deleted = await audit.PruneAsync(batchSize is > 0 ? batchSize.Value : 1000, ct);

    // Honours both retentions at once: StockCheck's thirty days and the configured year for
    // everything else — and it can only do that because StockCheck is DECLARED. An undeclared
    // entity has no [AuditRetention] for the prune to find, so the global sweep would take its
    // entries along with everything else's.
    return Results.Ok(new AffectedResponse(deleted));
})
.WithTags("Maintenance")
.WithSummary("Applies the configured and declared retentions. Irreversible.");

app.Run();

// One product by key, or null — the sample's stand-in for FindAsync. Every read carries
// unitOfWork.Current: once a connection has an open transaction, ADO.NET requires its commands to
// carry it.
static async Task<Product?> FindAsync(
    IDbConnection connection, UnitOfWork unitOfWork, int id, CancellationToken ct)
{
    const string sql = """
        SELECT "Id", "Name", "Price", "Category", "SupplierCost", "ViewCount"
        FROM "Products"
        WHERE "Id" = @Id
        """;

    return await connection.QuerySingleOrDefaultAsync<Product>(
        new CommandDefinition(sql, new { Id = id }, unitOfWork.Current, cancellationToken: ct));
}

static ProductResponse ToResponse(Product product) => new(
    product.Id,
    product.Name,
    product.Price,
    product.Category,
    product.SupplierCost,
    product.ViewCount);
