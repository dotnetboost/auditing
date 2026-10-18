// ─────────────────────────────────────────────────────────────────────────────
//  DotNetBoost.Auditing — Aspire AppHost
//
//  Starts the whole stack with one `dotnet run --project` here — no Aspire CLI required,
//  though `aspire run` does the same if you have it installed:
//
//    • the database container holding the sample's tables AND the trail  (PostgreSQL)
//    • the API       — samples/SampleApp, capturing every change through the interceptor
//    • the dashboard — clients/dashboard (Nuxt), reading the trail
//
//  The trail lives in the SAME database as the sample's own tables, and that is the point rather
//  than a convenience: it is what lets a change and its audit entry commit in one transaction.
//  A separate audit database would be a second connection and a second commit, which is the
//  guarantee the EF Core store exists to give up.
//
//  The store is swappable. Exactly one block is active below; to switch, comment the active one,
//  uncomment the alternative, uncomment its PackageReference in
//  DotNetBoost.Auditing.AppHost.csproj, and flip the matching blocks in
//  samples/SampleApp/Program.cs and samples/SampleApp/SampleApp.csproj.
//
//  MongoDB is deliberately not offered here. The sample records through the EF Core interceptor,
//  and the MongoDB and Dapper stores record EXPLICITLY — every change the sample makes by itself
//  would have to be written out by hand. See docs/stores.md.
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
    .WithPgAdmin();

var database = postgres.AddDatabase("auditing");

// ─────────────────────────────────────────────────────────────────────────────
//  Store — ALTERNATIVE: SQL Server (Entity Framework Core)
//  Package: Aspire.Hosting.SqlServer
// ─────────────────────────────────────────────────────────────────────────────
// var sqlServer = builder.AddSqlServer("sqlserver")
//     .WithDataVolume("dotnetboost-auditing-mssqldata");
//
// var database = sqlServer.AddDatabase("auditing");

// ─────────────────────────────────────────────────────────────────────────────
//  The API
// ─────────────────────────────────────────────────────────────────────────────
var api = builder.AddProject<Projects.SampleApp>("api")
    .WithReference(database)
    .WaitFor(database)
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
    .WithEnvironment("NUXT_AUDIT_API_URL", api.GetEndpoint("http"))
    // The audit endpoints require authorization, and the sample authenticates whoever the
    // X-Demo-User header names. The proxy sends this so the dashboard can read the trail at all.
    .WithEnvironment("NUXT_DEMO_USER", "dashboard@example.com")
    // The sidebar's "API reference" link. Read in the browser, so it needs the public URL.
    .WithEnvironment("NUXT_PUBLIC_API_REFERENCE_URL",
        ReferenceExpression.Create($"{api.GetEndpoint("http")}/scalar"))
    // Must match AuditingOptions.MaxPageSize in samples/SampleApp/Program.cs: the trail browser
    // will not ask for a page the engine would only clamp.
    .WithEnvironment("NUXT_PUBLIC_MAX_PAGE_SIZE", "200")
    .WithExternalHttpEndpoints();

builder.Build().Run();
