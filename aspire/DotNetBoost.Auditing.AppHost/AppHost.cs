// ─────────────────────────────────────────────────────────────────────────────
//  DotNetBoost.Auditing — Aspire AppHost
//
//  Starts the whole stack with one `dotnet run --project` here — no Aspire CLI required,
//  though `aspire run` does the same if you have it installed:
//
//    • the database container holding the sample's tables AND the trail  (PostgreSQL)
//    • the API       — samples/SampleApp            (EF Core store, capture through the interceptor)
//    • the same API  — samples/SampleApp.Dapper     (Dapper store, second database on the same server)
//    • MongoDB
//    • the same API  — samples/SampleApp.MongoDb    (MongoDB store)
//    • the dashboard — clients/dashboard (Nuxt), pointed at any one of them
//
//  All three stores run at once, on purpose, because the difference between them is the thing worth
//  seeing: the EF Core sample records nothing by hand and gets atomicity from the caller's own
//  SaveChanges; the other two record EVERY entry explicitly, and the Dapper one buys the same
//  guarantee with a transaction per request while the MongoDB one shows the window that leaves open.
//  See docs/stores.md.
//
//  In every case the trail lives in the SAME database as the sample's own data, and that is the
//  design rather than a convenience: it is what makes one transaction possible at all.
//
//  The EF Core sample's ENGINE is swappable — exactly one of its blocks below is active. To switch,
//  comment the active one, uncomment the alternative, uncomment its PackageReference in
//  DotNetBoost.Auditing.AppHost.csproj, and flip the matching blocks in
//  samples/SampleApp/Program.cs and samples/SampleApp/SampleApp.csproj.
// ─────────────────────────────────────────────────────────────────────────────

var builder = DistributedApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────────────────────
//  Store — ACTIVE: PostgreSQL (Entity Framework Core)
//  Package: Aspire.Hosting.PostgreSQL
// ─────────────────────────────────────────────────────────────────────────────
var postgres = builder.AddPostgres("postgres")
    // Named volume, so a trail collected in one session is still there in the next. An audit trail
    // that vanished when the container stopped would demonstrate the opposite of the point.
    .WithDataVolume("dotnetboost-auditing-pgdata")
    .WithPgWeb();

var database = postgres.AddDatabase("auditing");

// A second database on the SAME server, for samples/SampleApp.Dapper. Its own database rather than
// its own container: the two samples' tables have the same names, and seeing the Dapper store create
// its AuditEntries and AuditPropertyChanges from scratch — which is what migrateSchema: true does —
// is the point. pgweb above reaches both, so the two schemas can be compared side by side; they are
// identical, which is exactly the claim that store makes.
var dapperDatabase = postgres.AddDatabase("auditingdapper");

// ─────────────────────────────────────────────────────────────────────────────
//  Store — ALTERNATIVE: SQL Server (Entity Framework Core)
//  Package: Aspire.Hosting.SqlServer
// ─────────────────────────────────────────────────────────────────────────────
// var sqlServer = builder.AddSqlServer("sqlserver")
//     .WithDataVolume("dotnetboost-auditing-mssqldata");
//
// var database = sqlServer.AddDatabase("auditing");

// ─────────────────────────────────────────────────────────────────────────────
//  MongoDB — for samples/SampleApp.MongoDb
//  Package: Aspire.Hosting.MongoDB
//
//  Not an alternative to the block above: a second store running beside it, so both are up at once
//  and the difference is something you can click through rather than read about.
//
//  Mongo Express is the counterpart to WithPgWeb, and worth opening once: an entry here is ONE
//  document with its property changes embedded, where next door it is a row plus a row per property.
//
//  This is a STANDALONE mongod — Aspire.Hosting.MongoDB 13.6.4 does have WithReplicaSet, but this
//  AppHost does not call it yet — so transactions are not available, and the store's
//  WithSession(session) has nothing to enlist in.
//  That is not hidden from the sample: POST /api/products/{id}/fail exists to show the window it
//  leaves open, and samples/SampleApp.MongoDb/README.md has the command for a one-node replica set
//  if you want to close it.
// ─────────────────────────────────────────────────────────────────────────────
var mongo = builder.AddMongoDB("mongo")
    .WithDataVolume("dotnetboost-auditing-mongodata")
    .WithMongoExpress();

var mongoDatabase = mongo.AddDatabase("auditingmongo");

// ─────────────────────────────────────────────────────────────────────────────
//  The APIs
// ─────────────────────────────────────────────────────────────────────────────
var api = builder.AddProject<Projects.SampleApp>("api")
    .WithReference(database)
    .WaitFor(database)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// ── API — samples/SampleApp.Dapper ───────────────────────────────────────────
// The same API over the Dapper store: the same routes, the same request and response bodies
// (literally the same DTO files) and the same two entities. What differs is inside the endpoints —
// every entry is written by hand there, and every request runs in a real transaction, so a row and
// its entry commit or roll back together.
var dapperApi = builder.AddProject<Projects.SampleApp_Dapper>("dapper-api")
    .WithReference(dapperDatabase)
    .WaitFor(dapperDatabase)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// ── API — samples/SampleApp.MongoDb ──────────────────────────────────────────
// The same API again over the MongoDB store. Its recording is the Dapper sample's, almost line for
// line — which is the point: the explicit path costs the same whatever the engine.
var mongoApi = builder.AddProject<Projects.SampleApp_MongoDb>("mongo-api")
    .WithReference(mongoDatabase)
    .WaitFor(mongoDatabase)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// ── Dashboard — clients/dashboard (Nuxt) ─────────────────────────────────────
// npm install runs automatically before the dev server starts.
builder.AddJavaScriptApp("dashboard", "../../clients/dashboard", "dev")
    .WithNpm()
    // Nuxt binds whatever PORT says; without this it picks its own and Aspire has no endpoint to
    // publish, so the dashboard never gets a clickable URL.
    .WithHttpEndpoint(env: "PORT")
    .WithReference(api)
    .WaitFor(api)
    // Consumed by server/api/[...path].ts, which proxies the browser's calls — so the dashboard
    // reaches the audit endpoints and the sample's product endpoints through one origin.
    //
    // Swap `api` for `dapperApi` or `mongoApi` here and the dashboard drives that API instead, with
    // no change to the dashboard itself: all three serve the same routes and the same bodies. That
    // is worth doing once — it is the clearest evidence that the store is invisible from outside.
    .WithEnvironment("NUXT_AUDIT_API_URL", api.GetEndpoint("http"))
    // The sidebar's "API reference" link. Read in the browser, so it needs the public URL.
    .WithEnvironment("NUXT_PUBLIC_API_REFERENCE_URL",
        ReferenceExpression.Create($"{api.GetEndpoint("http")}/scalar"))
    // Only a fallback: the API publishes AuditingOptions.MaxPageSize in GET /api/audit/entities
    // and the dashboard reads it from there, so this is what it uses before the first answer
    // arrives — or if discovery is turned off. All three samples configure 200.
    .WithEnvironment("NUXT_PUBLIC_MAX_PAGE_SIZE", "200")
    .WithExternalHttpEndpoints();

builder.Build().Run();
