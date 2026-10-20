using DotNetBoost.Auditing.API;
using DotNetBoost.Auditing.Core;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using Microsoft.EntityFrameworkCore;
using SampleApp;
using SampleApp.Data;
using SampleApp.Domain;
using SampleApp.Dtos;
using Scalar.AspNetCore;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry, health checks, service discovery and resilient HttpClients.
builder.AddServiceDefaults();

// ─────────────────────────────────────────────────────────────────────────────
// Store for the trail. Exactly one block is active; it must match the active ItemGroup in
// SampleApp.csproj and the container started by the AppHost.
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>((sp, options) => options
    .UseNpgsql(builder.Configuration.GetConnectionString("auditing")
               ?? throw new InvalidOperationException(
                   "No 'auditing' connection string. Run this through aspire/DotNetBoost.Auditing.AppHost, "
                   + "which starts PostgreSQL and injects it."))
    // The capture. Without this line the tables are still there and IAuditRecorder still works,
    // but nothing records itself.
    .UseAuditing(sp));

// ALTERNATIVE: SQL Server
// builder.Services.AddDbContext<AppDbContext>((sp, o) =>
//     o.UseSqlServer(builder.Configuration.GetConnectionString("auditing")!).UseAuditing(sp));

// ALTERNATIVE: SQLite, for running this without any container at all
// builder.Services.AddDbContext<AppDbContext>((sp, o) =>
//     o.UseSqlite("Data Source=sample.db").UseAuditing(sp));

// ─────────────────────────────────────────────────────────────────────────────
// Auditing. The whole configuration of it.
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
    .UseEntityFrameworkCore<AppDbContext>()
    // This sample has no authentication, so there is no signed-in user to record: every entry is
    // attributed to "system". See SystemActorAccessor.cs — an application with users registers
    // .UseHttpContextActor() here instead and gets the real actor from one claim.
    .UseActorAccessor<SystemAuditActorAccessor>()
    // Needed because this sample records some changes BY NAME (see /api/products/discount): a write
    // addressed by hand has no type for the engine to read [AuditRedact] from unless the assembly
    // is declared. In an application that only ever uses the interceptor it is optional.
    .DeclareEntitiesFrom(typeof(Product).Assembly)
    .Build();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

// Create the database on first run. A real application uses EF Core migrations.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
}

app.UseExceptionHandler();
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// ─────────────────────────────────────────────────────────────────────────────
// The library's own endpoints: read the trail, and (opt-in) erase or prune it.
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
// This sample's own endpoints. Nothing in them mentions auditing, which is the point: the
// attributes on Product and the two lines above are the whole of it.
//
// Under /api, like the library's own, so clients/dashboard can reach both through one proxy.
// ─────────────────────────────────────────────────────────────────────────────
var products = app.MapGroup("/api/products").WithTags("Products");

products.MapGet("/", async (AppDbContext db, string? category, CancellationToken ct) =>
{
    var query = db.Products.AsNoTracking();

    if (!string.IsNullOrWhiteSpace(category))
        query = query.Where(p => p.Category == category);

    var found = await query.OrderBy(p => p.Id).ToListAsync(ct);

    return Results.Ok(found.Select(ToResponse));
})
.WithSummary("Lists products.");

products.MapGet("/{id:int}", async (AppDbContext db, int id, CancellationToken ct) =>
{
    var product = await db.Products.FindAsync([id], ct);

    if (product is null)
        return Results.NotFound();

    // An ignored property, changed on a read. It never reaches the trail — otherwise this endpoint
    // would bury every price change under a stream of view counts.
    product.ViewCount++;
    await db.SaveChangesAsync(ct);

    return Results.Ok(ToResponse(product));
})
.WithSummary("Gets one product, and bumps its [AuditIgnore] view count.")
.ProducesProblem(StatusCodes.Status404NotFound);

products.MapPost("/", async (AppDbContext db, CreateProduct body, CancellationToken ct) =>
{
    var product = new Product
    {
        Name         = body.Name,
        Price        = body.Price,
        Category     = body.Category,
        SupplierCost = body.SupplierCost
    };

    db.Products.Add(product);

    // One save. The Created entry is written by the interceptor — and because the key is
    // store-generated, it is written by a second save inside a transaction the interceptor opened,
    // so the product and its trail commit together or neither does.
    await db.SaveChangesAsync(ct);

    return Results.Created($"/api/products/{product.Id}", ToResponse(product));
})
.WithSummary("Creates a product. Recorded as Created, with the key the database assigned.");

products.MapPut("/{id:int}", async (AppDbContext db, int id, UpdateProduct body, CancellationToken ct) =>
{
    var product = await db.Products.FindAsync([id], ct);

    if (product is null)
        return Results.NotFound();

    // Loaded first, so EF has a before-image and the trail records only what moved. A detached
    // db.Update(...) has no before-image at all — see docs/limitations.md.
    if (body.Name is { } name)                 product.Name = name;
    if (body.Price is { } price)               product.Price = price;
    if (body.Category is { } category)         product.Category = category;
    if (body.SupplierCost is { } supplierCost) product.SupplierCost = supplierCost;

    await db.SaveChangesAsync(ct);

    return Results.Ok(ToResponse(product));
})
.WithSummary("Updates a product. Only the properties that actually changed are recorded.")
.ProducesProblem(StatusCodes.Status404NotFound);

products.MapDelete("/{id:int}", async (AppDbContext db, int id, CancellationToken ct) =>
{
    var product = await db.Products.FindAsync([id], ct);

    if (product is null)
        return Results.NotFound();

    db.Products.Remove(product);
    await db.SaveChangesAsync(ct);

    // The Deleted entry is still there, with the product's final values. Nothing cascades to the
    // trail, which here is the feature: this is the only copy of what the row said.
    return Results.NoContent();
})
.WithSummary("Deletes a product. Its trail outlives it, with the final values.")
.ProducesProblem(StatusCodes.Status404NotFound);

products.MapGet("/{id:int}/history", async (
    AppDbContext db,
    IAuditReader reader,
    int id,
    // Nullable, with defaults applied below. A non-nullable int here would make both REQUIRED query
    // parameters — minimal APIs reads "not nullable" as "must be supplied" — and the endpoint would
    // answer 400 to the obvious call.
    int? skip,
    int? take,
    CancellationToken ct) =>
{
    var product = await db.Products.FindAsync([id], ct);

    // Addressed by hand when the row is gone: the trail outlives the entity, so a history endpoint
    // that needed the entity to exist could not show the deletion.
    //
    // Zero means "the configured default page", which the reader applies — this endpoint does not
    // get to invent a page size of its own.
    var page = product is null
        ? await reader.QueryAsync(
            new AuditQuery
            {
                EntityName = "Product",
                EntityId   = id.ToString(CultureInfo.InvariantCulture),
                Skip       = skip ?? 0,
                Take       = take ?? 0
            }, ct)
        : await reader.HistoryAsync(product, skip ?? 0, take ?? 0, ct);

    // The API package's wire shape, not the store model: AuditAction comes out as a name, and the
    // response carries the page size actually served after the clamp.
    return Results.Ok(AuditPageResponse.From(page, skip ?? 0));
})
.WithSummary("One product's trail, newest first — including a deleted one's.");

products.MapPost("/discount", async (
    AppDbContext db,
    IAuditRecorder audit,
    ApplyDiscount body,
    CancellationToken ct) =>
{
    if (body.Percent is <= 0 or >= 100)
        return Results.Problem("Percent must be between 0 and 100.", statusCode: StatusCodes.Status400BadRequest);

    var factor = 1m - (body.Percent / 100m);

    var affected = await db.Products
        .Where(p => p.Category == body.Category)
        .ToListAsync(ct);

    if (affected.Count == 0)
        return Results.Ok(new AffectedResponse(0));

    // ── The case the interceptor cannot see ──────────────────────────────────
    // A set-based UPDATE never goes through the change tracker, so nothing is captured for it. The
    // prices really do change and the trail would say nothing — so this records explicitly, which
    // is also exactly how a Dapper or MongoDB application records everything.
    var ids = affected.Select(p => p.Id).ToList();

    // The Math.Round is not decoration. Price is decimal(18,2), so the database would round the
    // product for itself and store 17.99 where the entry below said 17.991 — a trail that is wrong
    // about the value it claims the row now holds, which is the one thing a trail must not be.
    // Rounding in the statement and in the entry, by the same rule, is what keeps the two in
    // agreement: ROUND() on all three engines is half away from zero, as MidpointRounding.AwayFromZero
    // is.
    await db.Products
        .Where(p => ids.Contains(p.Id))
        .ExecuteUpdateAsync(set => set.SetProperty(p => p.Price, p => Math.Round(p.Price * factor, 2)), ct);

    foreach (var product in affected)
    {
        var discounted = decimal.Round(product.Price * factor, 2, MidpointRounding.AwayFromZero);

        await audit.For(product).UpdatedAsync(
            [new AuditChange(nameof(Product.Price), product.Price, discounted)],
            metadata: $$"""{"reason":"bulk discount","percent":{{body.Percent}}}""",
            ct);
    }

    // The entries were TRACKED by the append above, not written: the EF Core store joins this
    // context's unit of work. This is what makes them durable.
    await db.SaveChangesAsync(ct);

    return Results.Ok(new AffectedResponse(affected.Count));
})
.WithSummary("Discounts a category with one UPDATE, and records it explicitly — the change tracker never sees it.")
.ProducesProblem(StatusCodes.Status400BadRequest);

products.MapPost("/{id:int}/fail", async (AppDbContext db, int id, CancellationToken ct) =>
{
    var product = await db.Products.FindAsync([id], ct);

    if (product is null)
        return Results.NotFound();

    product.Price += 1m;
    await db.SaveChangesAsync(ct);

    // ── The guarantee, demonstrated ─────────────────────────────────────────
    // The price change and its audit entry were committed by the save above, in one transaction.
    // This then fails, and both survive — which is correct: they happened.
    //
    // The interesting half is the other way round. If writing the ENTRY had failed, the price
    // change would have been rolled back with it, and the database would hold neither. There is a
    // test for exactly that in tests/DotNetBoost.Auditing.IntegrationTests/InterceptorTests.cs,
    // against PostgreSQL.
    throw new InvalidOperationException(
        "Deliberate failure, after the save. Check the trail: the price change IS recorded, because "
        + "it did happen. See the comment in Program.cs for the case that matters.");
})
.WithSummary("Changes a price, saves, then throws — showing what the trail says afterwards.")
.ProducesProblem(StatusCodes.Status404NotFound);

// ─────────────────────────────────────────────────────────────────────────────
// Stock checks: the noisy entity, kept for thirty days rather than the configured year.
// ─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/stock-checks", async (AppDbContext db, RecordStockCheck body, CancellationToken ct) =>
{
    db.StockChecks.Add(new StockCheck
    {
        ProductId = body.ProductId,
        OnHand    = body.OnHand,
        CheckedAt = DateTime.UtcNow
    });

    await db.SaveChangesAsync(ct);

    return Results.NoContent();
})
.WithTags("Products")
.WithSummary("Records a stock check — audited, but kept for 30 days rather than a year.");

// ─────────────────────────────────────────────────────────────────────────────
// Retention. The library never prunes on its own: there is no hosted service and no timer, because
// the one irreversible thing it does should be something an application schedules.
// ─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/maintenance/prune", async (IAuditPruner pruner, int? batchSize, CancellationToken ct) =>
{
    var deleted = await pruner.PruneAsync(batchSize is > 0 ? batchSize.Value : 1000, ct);

    // Honours both retentions at once: StockCheck's thirty days and the configured year for
    // everything else. A global sweep does not take the longer trail with it.
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
