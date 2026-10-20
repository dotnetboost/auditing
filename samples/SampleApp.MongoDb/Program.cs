using DotNetBoost.Auditing.API;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using MongoDB.Driver;
using SampleApp;
using SampleApp.Dtos;
using SampleApp.MongoDb.Data;
using SampleApp.MongoDb.Domain;
using Scalar.AspNetCore;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry, health checks, service discovery and resilient HttpClients.
builder.AddServiceDefaults();

// "auditingmongo" is the resource name from aspire/DotNetBoost.Auditing.AppHost/AppHost.cs. Run
// this sample on its own and you supply it yourself:
//   ConnectionStrings__auditingmongo="mongodb://localhost:27017/auditing"
var connectionString = builder.Configuration.GetConnectionString("auditingmongo")
    ?? throw new InvalidOperationException(
        "No 'auditingmongo' connection string. Run this through aspire/DotNetBoost.Auditing.AppHost, "
        + "which starts MongoDB and injects it — or point it at a MongoDB database yourself; see "
        + "samples/SampleApp.MongoDb/README.md.");

// ─────────────────────────────────────────────────────────────────────────────
// The driver. One client for the process, as it expects: it owns the connection pool. The
// collections are singletons too — IMongoCollection is thread-safe and stateless, so nothing here
// is per-request.
//
// The trail goes in THIS database, beside these collections. That is the same decision the EF Core
// sample makes by putting the audit tables in its own DbContext, and for a weaker version of the
// same reason: a separate audit database would be a second server to be up, and nothing would ever
// put a change and its entry in one transaction.
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));
builder.Services.AddSingleton(sp =>
{
    var url = MongoUrl.Create(connectionString);
    return sp.GetRequiredService<IMongoClient>().GetDatabase(url.DatabaseName ?? "auditing");
});
builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>().GetCollection<Product>("products"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>().GetCollection<StockCheck>("stockChecks"));
builder.Services.AddSingleton<IdSequence>();

// ─────────────────────────────────────────────────────────────────────────────
// Auditing. The whole configuration of it — the EF Core sample's chain with ONE line changed:
// UseMongoDb() instead of UseEntityFrameworkCore<AppDbContext>().
//
// There is nothing to intercept: a MongoDB application has no change tracker, so nothing can read a
// before-image or notice that a document moved. Every entry in this sample is written by hand,
// below — exactly as in samples/SampleApp.Dapper, which is the other half of the point: the
// explicit path is the same code whichever engine is underneath.
//
// UseMongoDb() creates the collection's indexes when the host starts, because there are no
// migrations to hang them off: the per-entity read, the actor read, the prune scan, the correlation
// lookup, and a multikey one on the embedded Changes.PropertyName. MongoDbOptions names the
// collection and can turn that off — but only when something else creates them, never to work
// around a permission failure. An audit collection only grows, and an unindexed one is a scan.
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
    // No arguments: it uses the IMongoDatabase registered above, so the trail lands in the same
    // database as the documents. There are overloads that take a connection string and a database
    // name, or resolve one per tenant from a scoped accessor.
    .UseMongoDb()
    // This sample has no authentication, so there is no signed-in user to record: every entry is
    // attributed to "system". SystemActorAccessor.cs is the EF Core sample's file, compiled into
    // this project — an application with users registers .UseHttpContextActor() here instead and
    // gets the real actor from one claim.
    .UseActorAccessor<SystemAuditActorAccessor>()
    // ── NOT OPTIONAL HERE ───────────────────────────────────────────────────
    // Same reason as in the Dapper sample: an entry is addressed by name and key, so a write the
    // engine has no type for — audit.For("Product", "42") — has no [AuditRedact] or [AuditIgnore]
    // to apply, and a prune has no per-entity retention to find. For such a write, undeclared means
    // a redacted property goes into the entry document in full. Every write below carries its type,
    // so it is the prune and the discovery endpoint that would notice this line going missing; the
    // redaction would notice one refactor later, and nobody would notice that.
    //
    // It is also what GET /api/audit/entities answers with, so the dashboard's model page is empty
    // without it.
    .DeclareEntitiesFrom(typeof(Product).Assembly)
    .Build();

// Readiness, as AddDbContextCheck<AppDbContext>() is for the EF Core sample.
builder.Services.AddHealthChecks()
    .AddCheck<MongoHealthCheck>("mongodb");

// The driver emits no ActivitySource of its own, unlike Npgsql, so the statements below do not show
// up as spans on the Aspire dashboard. For the equivalent, add
// MongoDB.Driver.Core.Extensions.DiagnosticSources and hook its subscriber in the client settings'
// ClusterConfigurator.

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

// No EnsureCreated and no DDL: collections appear on first write, and the library creates its own
// indexes from a hosted service as the host starts. The sample declares no index of its own, as the
// EF Core sample declares none — a real catalogue would index Category, which the list endpoint
// filters on.

app.UseExceptionHandler();
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// ─────────────────────────────────────────────────────────────────────────────
// The library's own endpoints: read the trail, and (opt-in) erase or prune it. Identical to the EF
// Core sample's — reading a trail knows nothing about how it was written, or about the shape it is
// stored in.
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
// are literally its files — so clients/dashboard can be pointed at any of the three APIs without a
// change.
//
// The recording below is the same explicit recording as samples/SampleApp.Dapper's, line for line
// in places: that is what "no change tracker" costs, and it costs the same whatever the engine. What
// is specific to this store is where the atomicity goes — see POST /api/products/{id}/fail.
// ─────────────────────────────────────────────────────────────────────────────
var products = app.MapGroup("/api/products").WithTags("Products");

products.MapGet("/", async (IMongoCollection<Product> collection, string? category, CancellationToken ct) =>
{
    var filter = string.IsNullOrWhiteSpace(category)
        ? Builders<Product>.Filter.Empty
        : Builders<Product>.Filter.Eq(p => p.Category, category);

    var found = await collection.Find(filter).SortBy(p => p.Id).ToListAsync(ct);

    return Results.Ok(found.Select(ToResponse));
})
.WithSummary("Lists products. A read records nothing, here as in the EF Core sample.");

products.MapGet("/{id:int}", async (
    IMongoCollection<Product> collection,
    IAuditManager audit,
    int id,
    CancellationToken ct) =>
{
    // One round trip: the increment and the read-back of the updated document are the same
    // operation, which is the kind of thing a document store is good at.
    var product = await collection.FindOneAndUpdateAsync(
        Builders<Product>.Filter.Eq(p => p.Id, id),
        Builders<Product>.Update.Inc(p => p.ViewCount, 1),
        new FindOneAndUpdateOptions<Product> { ReturnDocument = ReturnDocument.After },
        ct);

    if (product is null)
        return Results.NotFound();

    // ── An ignored property, reported deliberately ───────────────────────────
    // The EF Core sample demonstrates [AuditIgnore] by not having to think about it: the
    // interceptor drops the property and no entry is ever written. The explicit path can only
    // demonstrate it by trying — so this reports the view count and the engine throws it away.
    //
    // The call returns null. Every reported property was ignored, and an Updated write of nothing
    // records nothing, so there is no view-count noise in the collection afterwards. The model's
    // rules are applied in the engine rather than in the capture source, so an application cannot
    // opt out of them by addressing the write itself.
    _ = await audit.For(product).UpdatedAsync(
        [new AuditChange(nameof(Product.ViewCount), product.ViewCount - 1, product.ViewCount)], ct: ct);

    return Results.Ok(ToResponse(product));
})
.WithSummary("Gets one product, and reports its [AuditIgnore] view count — which the engine drops.")
.ProducesProblem(StatusCodes.Status404NotFound);

products.MapPost("/", async (
    IMongoCollection<Product> collection,
    IdSequence ids,
    IAuditManager audit,
    CreateProduct body,
    CancellationToken ct) =>
{
    var product = new Product
    {
        // The key before the write, not after it. MongoDB generates no integers, so this comes from
        // the counters collection — see Data/IdSequence.cs. It means recording never has to wait
        // for the insert, which is the problem the EF Core store solves with a deferred second save.
        Id           = await ids.NextAsync("products", ct),
        Name         = body.Name,
        Price        = body.Price,
        Category     = body.Category,
        SupplierCost = body.SupplierCost
    };

    await collection.InsertOneAsync(product, options: null, ct);

    // The initial values, as NewValue. For(product) carries the type, so SupplierCost is recorded
    // as changed with its values withheld.
    //
    // One insert writes the whole entry, changes and all: the property changes are embedded in the
    // entry document rather than kept in a second collection. So there is no window in which the
    // trail holds an entry whose property rows have not arrived — which is a guarantee the
    // relational stores need a transaction for.
    await audit.For(product).CreatedAsync(
    [
        new AuditChange(nameof(Product.Name), null, product.Name),
        new AuditChange(nameof(Product.Price), null, product.Price),
        new AuditChange(nameof(Product.Category), null, product.Category),
        new AuditChange(nameof(Product.SupplierCost), null, product.SupplierCost)
    ], ct: ct);

    return Results.Created($"/api/products/{product.Id}", ToResponse(product));
})
.WithSummary("Creates a product and records the creation explicitly, in one insert each.");

products.MapPut("/{id:int}", async (
    IMongoCollection<Product> collection,
    IAuditManager audit,
    int id,
    UpdateProduct body,
    CancellationToken ct) =>
{
    // ── The before-image, read by hand ───────────────────────────────────────
    // The same read the Dapper sample does with a SELECT, and for the same reason: there is no other
    // way to know what the values were, and a trail that cannot say what a price changed *from* is
    // most of the value gone.
    var before = await collection.Find(Builders<Product>.Filter.Eq(p => p.Id, id)).FirstOrDefaultAsync(ct);

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
    // sample. The engine would record nothing for an Updated write whose list came out empty, but
    // the comparison itself belongs where the two values are.
    var changes = new List<AuditChange>(capacity: 4);

    if (after.Name != before.Name)
        changes.Add(new AuditChange(nameof(Product.Name), before.Name, after.Name));

    if (after.Price != before.Price)
        changes.Add(new AuditChange(nameof(Product.Price), before.Price, after.Price));

    if (after.Category != before.Category)
        changes.Add(new AuditChange(nameof(Product.Category), before.Category, after.Category));

    if (after.SupplierCost != before.SupplierCost)
        changes.Add(new AuditChange(nameof(Product.SupplierCost), before.SupplierCost, after.SupplierCost));

    if (changes.Count == 0)
        return Results.Ok(ToResponse(before));

    await collection.ReplaceOneAsync(
        Builders<Product>.Filter.Eq(p => p.Id, id), after, new ReplaceOptions(), ct);

    await audit.For(after).UpdatedAsync(changes, ct: ct);

    return Results.Ok(ToResponse(after));
})
.WithSummary("Updates a product. The before-image comes from a read, because nothing else has one.")
.ProducesProblem(StatusCodes.Status404NotFound);

products.MapDelete("/{id:int}", async (
    IMongoCollection<Product> collection,
    IAuditManager audit,
    int id,
    CancellationToken ct) =>
{
    // The document itself is the only source of its final values, so it is read back as it is
    // deleted rather than before — one round trip instead of two.
    var product = await collection.FindOneAndDeleteAsync(
        Builders<Product>.Filter.Eq(p => p.Id, id), options: null, ct);

    if (product is null)
        return Results.NotFound();

    // The final values, as OldValue — and worth the trouble, because after the delete this entry is
    // the only copy of what the document said. Nothing cascades to the trail: the entries are their
    // own collection with no reference to this one, so the history survives the document by design
    // rather than by luck.
    await audit.For(product).DeletedAsync(
    [
        new AuditChange(nameof(Product.Name), product.Name, null),
        new AuditChange(nameof(Product.Price), product.Price, null),
        new AuditChange(nameof(Product.Category), product.Category, null),
        new AuditChange(nameof(Product.SupplierCost), product.SupplierCost, null)
    ], ct: ct);

    return Results.NoContent();
})
.WithSummary("Deletes a product. Its trail outlives it, with the final values.")
.ProducesProblem(StatusCodes.Status404NotFound);

products.MapGet("/{id:int}/history", async (
    IMongoCollection<Product> collection,
    IAuditManager audit,
    int id,
    // Nullable, with defaults applied below. A non-nullable int here would make both REQUIRED query
    // parameters — minimal APIs reads "not nullable" as "must be supplied" — and the endpoint would
    // answer 400 to the obvious call.
    int? skip,
    int? take,
    CancellationToken ct) =>
{
    var product = await collection.Find(Builders<Product>.Filter.Eq(p => p.Id, id)).FirstOrDefaultAsync(ct);

    // Addressed by hand when the document is gone: the trail outlives the entity, so a history
    // endpoint that needed the document to exist could not show the deletion.
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

    // The API package's wire shape, not the store model: AuditAction comes out as a name — and in
    // this store it was *stored* as a name too, because a document is read by people as often as by
    // code. The relational stores keep an integer, which is what their index wants.
    return Results.Ok(AuditPageResponse.From(page, skip ?? 0));
})
.WithSummary("One product's trail, newest first — including a deleted one's.");

products.MapPost("/discount", async (
    IMongoCollection<Product> collection,
    IAuditManager audit,
    ApplyDiscount body,
    CancellationToken ct) =>
{
    if (body.Percent is <= 0 or >= 100)
        return Results.Problem("Percent must be between 0 and 100.", statusCode: StatusCodes.Status400BadRequest);

    var factor = 1m - (body.Percent / 100m);
    var filter = Builders<Product>.Filter.Eq(p => p.Category, body.Category);

    var affected = await collection.Find(filter).SortBy(p => p.Id).ToListAsync(ct);

    if (affected.Count == 0)
        return Results.Ok(new AffectedResponse(0));

    // In the EF Core sample this endpoint is the exception — the one write its change tracker cannot
    // see, recorded explicitly to make the point. Here it is not an exception at all: a bulk write is
    // recorded exactly like every other endpoint in this file, because every endpoint in this file
    // records the same way.
    //
    // One $mul over the whole category would be a line shorter, and it is the wrong shape here. A
    // document has no column scale, so whatever $mul computes is what gets stored — 17.991 and not
    // 17.99 — and the application would be recording a value it did not compute and cannot round.
    // So the price is rounded HERE, and the same value is both written and recorded; a trail that is
    // wrong about the value it claims the document now holds is the one thing a trail must not be.
    //
    // It is still one round trip: a bulk write carries every $set in a single command.
    var writes = new List<WriteModel<Product>>(affected.Count);
    var discounts = new Dictionary<int, decimal>(affected.Count);

    foreach (var product in affected)
    {
        var discounted = decimal.Round(product.Price * factor, 2, MidpointRounding.AwayFromZero);

        discounts[product.Id] = discounted;
        writes.Add(new UpdateOneModel<Product>(
            Builders<Product>.Filter.Eq(p => p.Id, product.Id),
            Builders<Product>.Update.Set(p => p.Price, discounted)));
    }

    await collection.BulkWriteAsync(writes, options: null, ct);

    foreach (var product in affected)
    {
        // For(product) carries the type, so [AuditRedact] applies here whatever the chain above
        // says. An endpoint that composed audit.For("Product", id) instead — which is what a key
        // assembled from several fields, or a document nothing loaded, forces — would depend
        // entirely on DeclareEntitiesFrom for that.
        await audit.For(product).UpdatedAsync(
            [new AuditChange(nameof(Product.Price), product.Price, discounts[product.Id])],
            metadata: $$"""{"reason":"bulk discount","percent":{{body.Percent}}}""",
            ct);
    }

    return Results.Ok(new AffectedResponse(affected.Count));
})
.WithSummary("Discounts a category with one bulk update, and records an entry per product.")
.ProducesProblem(StatusCodes.Status400BadRequest);

products.MapPost("/{id:int}/fail", async (
    IMongoCollection<Product> collection,
    int id,
    CancellationToken ct) =>
{
    var updated = await collection.FindOneAndUpdateAsync(
        Builders<Product>.Filter.Eq(p => p.Id, id),
        Builders<Product>.Update.Inc(p => p.Price, 1m),
        new FindOneAndUpdateOptions<Product> { ReturnDocument = ReturnDocument.After },
        ct);

    if (updated is null)
        return Results.NotFound();

    // ── The window, shown rather than hidden ────────────────────────────────
    // The price has changed and committed — a single-document write in MongoDB is atomic by itself,
    // and there is nothing wrapped around this request. Then this throws, so NOTHING IS RECORDED:
    // the price moved and the trail says it never did.
    //
    // That is the explicit path's real failure mode, and it is the one thing the EF Core sample
    // cannot demonstrate, because there the capture is inside the save and cannot be forgotten or
    // skipped. The Dapper sample closes the same gap with a transaction per request; this sample
    // cannot, because the AppHost runs a STANDALONE mongod and a transaction there needs a replica
    // set. The fix, where you have one:
    //
    //     using var session = await client.StartSessionAsync(ct);
    //     session.StartTransaction();
    //     var store = new MongoAuditStore(database, options).WithSession(session);
    //     // … write the document, record the change …
    //     await session.CommitTransactionAsync(ct);
    //
    // Registering an IClientSessionHandle in the request scope does the same for the
    // container-resolved store, exactly as the Dapper sample registers an IDbTransaction. There is
    // an integration test that commits and aborts one against a real replica set; see
    // docs/stores.md.
    throw new InvalidOperationException(
        "Deliberate failure, after the change and before the entry. Check the price and the trail: "
        + "the price IS higher and the trail says nothing happened. That gap is what a session on a "
        + "replica set closes — see the comment in Program.cs.");
})
.WithSummary("Changes a price, then throws before recording — the gap a session would close.")
.ProducesProblem(StatusCodes.Status404NotFound);

// ─────────────────────────────────────────────────────────────────────────────
// Stock checks: the noisy entity, kept for thirty days rather than the configured year.
// ─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/stock-checks", async (
    IMongoCollection<StockCheck> collection,
    IdSequence ids,
    IAuditManager audit,
    RecordStockCheck body,
    CancellationToken ct) =>
{
    var check = new StockCheck
    {
        Id        = await ids.NextAsync("stockChecks", ct),
        ProductId = body.ProductId,
        OnHand    = body.OnHand,
        CheckedAt = DateTime.UtcNow
    };

    await collection.InsertOneAsync(check, options: null, ct);

    await audit.For(check).CreatedAsync(
    [
        new AuditChange(nameof(StockCheck.ProductId), null, check.ProductId),
        new AuditChange(nameof(StockCheck.OnHand), null, check.OnHand)
    ], ct: ct);

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
    // everything else — and it can only do that because StockCheck is DECLARED. Deleting an entry
    // here takes its property changes with it, because they are fields of the same document: no
    // second delete, and no cascade to depend on.
    return Results.Ok(new AffectedResponse(deleted));
})
.WithTags("Maintenance")
.WithSummary("Applies the configured and declared retentions. Irreversible.");

app.Run();

static ProductResponse ToResponse(Product product) => new(
    product.Id,
    product.Name,
    product.Price,
    product.Category,
    product.SupplierCost,
    product.ViewCount);
