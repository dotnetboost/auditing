using Dapper;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.Dapper;
using DotNetBoost.Auditing.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Globalization;

namespace DotNetBoost.Auditing.IntegrationTests;

/// <summary>
/// What happens when several things use a trail at once, against PostgreSQL.
/// </summary>
/// <remarks>
/// <b>PostgreSQL and not SQLite, deliberately.</b> SQLite's shared cache admits one writer, so two
/// connections contending there teach you about SQLite's locking and nothing about this library —
/// an earlier attempt at one of these tests on SQLite was a lock-contention storm that ran for ten
/// minutes and proved nothing. Postgres has the concurrency model these questions are actually
/// about.
/// <para>
/// <b>Nothing here asserts a duration.</b> Every assertion is an invariant — a count, a set, an
/// absence — because a test that fails when a container is slow gets deleted, and then there is
/// neither a test nor the confidence. Each worker does a fixed amount of work and stops, so there
/// are no timers and no cancellation to get wrong.
/// </para>
/// </remarks>
[Collection("postgres")]
public sealed class ConcurrencyTests(PostgresFixture fixture)
{
    private const int Writers = 8;
    private const int Each    = 50;

    [Fact]
    public async Task Concurrent_writers_on_their_own_connections_all_land()
    {
        var connectionString = await PrepareAsync("audit_concurrent_writers");

        // One connection per writer, which is what one scope per request gives you. Sharing one
        // between them would be testing the gate in UnitOfWork instead, which ProviderTests does.
        var work = Enumerable.Range(0, Writers).Select(writer => Task.Run(async () =>
        {
            await using var connection = new NpgsqlConnection(connectionString);
            var store = new DapperAuditStore(connection, new DapperOptions());

            for (var i = 0; i < Each; i++)
                await store.AppendAsync(Entry(writer, i));
        }));

        await Task.WhenAll(work);

        await using var verify = new NpgsqlConnection(connectionString);

        // Every entry, once, with its property row. A lost write and a duplicated one are both
        // visible here, and so is a row whose child never arrived.
        Assert.Equal(Writers * Each, await verify.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM "AuditEntries" """));
        Assert.Equal(Writers * Each, await verify.ExecuteScalarAsync<int>("""SELECT COUNT(DISTINCT "Id") FROM "AuditEntries" """));
        Assert.Equal(Writers * Each, await verify.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM "AuditPropertyChanges" """));

        // And each writer's own fifty, so a writer that silently stopped early would show up rather
        // than being hidden by another's rows.
        for (var writer = 0; writer < Writers; writer++)
        {
            Assert.Equal(Each, await verify.ExecuteScalarAsync<int>(
                """SELECT COUNT(*) FROM "AuditEntries" WHERE "Actor" = @actor""",
                new { actor = $"writer-{writer}" }));
        }
    }

    [Fact]
    public async Task Concurrent_saves_through_the_interceptor_each_record_their_creation()
    {
        var connectionString = await PrepareAsync("audit_concurrent_capture", forEfCore: true);

        var services = new ServiceCollection();

        services.AddDbContext<AuditDbContext>((sp, o) => o.UseNpgsql(connectionString).UseAuditing(sp));
        services.AddAuditing().UseEntityFrameworkCore<AuditDbContext>().Build();

        await using var provider = services.BuildServiceProvider();

        // A scope each, as concurrent requests would have. Every Product has a store-generated key,
        // so every one of these goes down the interceptor's deferred path — captured in
        // SavingChanges, written in a second save, and held together by a transaction the
        // interceptor opens itself. That machinery is the most intricate code in the library and
        // this is the first thing to run it concurrently.
        var work = Enumerable.Range(0, Writers).Select(writer => Task.Run(async () =>
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

            for (var i = 0; i < 5; i++)
            {
                db.Products.Add(new Product
                {
                    Name  = $"writer-{writer.ToString(CultureInfo.InvariantCulture)}-{i.ToString(CultureInfo.InvariantCulture)}",
                    Price = i
                });

                await db.SaveChangesAsync();
            }
        }));

        await Task.WhenAll(work);

        await using var verify = new NpgsqlConnection(connectionString);

        var products = await verify.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM "Products" """);
        var created  = await verify.ExecuteScalarAsync<int>(
            """SELECT COUNT(*) FROM "AuditEntries" WHERE "Action" = 0""");

        // One entry per product and not one more: a deferred entry lost under contention would show
        // as fewer, and one recorded twice — the re-entrancy guard failing — as more.
        Assert.Equal(Writers * 5, products);
        Assert.Equal(products, created);

        // Every entry addressed to a key that exists, which is what the deferred pass is for: a
        // placeholder that escaped would be a "0" here.
        Assert.Equal(0, await verify.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM "AuditEntries" a
            WHERE NOT EXISTS (SELECT 1 FROM "Products" p WHERE CAST(p."Id" AS TEXT) = a."EntityId")
            """));
    }

    [Fact]
    public async Task A_prune_and_a_recording_can_run_at_the_same_time()
    {
        var connectionString = await PrepareAsync("audit_prune_under_load");

        // Old enough to be pruned: five batches' worth, so the sweep is several round trips and has
        // somewhere to overlap.
        await using (var seed = new NpgsqlConnection(connectionString))
        {
            await seed.ExecuteAsync(
                """
                INSERT INTO "AuditEntries"
                    ("Id","EntityName","EntityId","Action","Actor","CorrelationId","OccurredAt","Metadata")
                SELECT gen_random_uuid(), 'Product', i::text, 0, 'historic', 'seed',
                       now() - interval '400 days', NULL
                FROM generate_series(1, 5000) AS i
                """);
        }

        var pruning = Task.Run(async () =>
        {
            await using var connection = new NpgsqlConnection(connectionString);
            var store = new DapperAuditStore(connection, new DapperOptions());

            var total = 0;
            int deleted;

            // The pruner's own loop, which is where the batching lives.
            do
            {
                deleted = await store.DeleteOlderThanAsync(
                    DateTime.UtcNow.AddDays(-30), AuditPruneScope.All, 1000);

                total += deleted;
            }
            while (deleted == 1000);

            return total;
        });

        var writing = Enumerable.Range(0, Writers).Select(writer => Task.Run(async () =>
        {
            await using var connection = new NpgsqlConnection(connectionString);
            var store = new DapperAuditStore(connection, new DapperOptions());

            for (var i = 0; i < Each; i++)
                await store.AppendAsync(Entry(writer, i));
        }));

        // No exception from either side is half the assertion: the batching exists so that a sweep
        // does not hold locks that recording needs, and a deadlock or a timeout here would say it
        // does not work.
        await Task.WhenAll([pruning, .. writing]);

        Assert.Equal(5000, await pruning);

        await using var verify = new NpgsqlConnection(connectionString);

        // Everything old went, everything new stayed. The prune's cutoff is fixed when it starts,
        // so entries arriving during the sweep are newer than it by construction — which is why it
        // terminates and why it cannot take one of these with it.
        Assert.Equal(0, await verify.ExecuteScalarAsync<int>(
            """SELECT COUNT(*) FROM "AuditEntries" WHERE "Actor" = 'historic'"""));

        Assert.Equal(Writers * Each, await verify.ExecuteScalarAsync<int>(
            """SELECT COUNT(*) FROM "AuditEntries" """));

        // No property row outlived its entry.
        Assert.Equal(0, await verify.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM "AuditPropertyChanges" c
            WHERE NOT EXISTS (SELECT 1 FROM "AuditEntries" a WHERE a."Id" = c."AuditEntryId")
            """));
    }

    [Fact]
    public async Task A_cursor_walk_is_not_disturbed_by_entries_arriving_during_it()
    {
        var connectionString = await PrepareAsync("audit_cursor_under_load");

        const int Existing = 200;
        const int Page     = 20;

        await using var connection = new NpgsqlConnection(connectionString);
        var store = new DapperAuditStore(connection, new DapperOptions());

        var original = new List<Guid>();

        for (var i = 0; i < Existing; i++)
        {
            var entry = Entry(0, i, DateTime.UtcNow.AddMinutes(-Existing + i));
            await store.AppendAsync(entry);
            original.Add(entry.Id);
        }

        var seen = new List<Guid>();
        AuditCursor? cursor = null;

        await using var inserter = new NpgsqlConnection(connectionString);
        var intruder = new DapperAuditStore(inserter, new DapperOptions());

        while (true)
        {
            var page = await store.QueryAsync(
                new AuditQuery { Take = Page, After = cursor, IncludeChanges = false });

            if (page.Entries.Count == 0)
                break;

            seen.AddRange(page.Entries.Select(e => e.Id));
            cursor = AuditCursor.Of(page.Entries[^1]);

            // A new entry between every page. With Skip this is worse than the duplicate row it is
            // usually described as: each arrival pushes a row back into the part not yet walked, so
            // the walk never reaches an empty page. Swapping `After = cursor` for
            // `Skip = seen.Count` here turns a 250ms test into one that was still going after ten
            // minutes — measured, not reasoned. A cursor is defined by the rows themselves, so an
            // arrival cannot move a boundary.
            await intruder.AppendAsync(Entry(99, seen.Count));
        }

        // Newest first, so entries arriving mid-walk sort ahead of the cursor and are never
        // visited. What matters is that the walk saw the trail it started on, exactly once each.
        Assert.Equal(Existing, seen.Count);
        Assert.Equal(Existing, seen.Distinct().Count());
        Assert.Equal(original.OrderBy(id => id), seen.OrderBy(id => id));

        // And the intruders really did arrive — without this the test would pass just as happily
        // if every insert during the walk had silently failed, which is the way a concurrency test
        // most often turns out to prove nothing.
        await using var verify = new NpgsqlConnection(connectionString);

        Assert.Equal(
            Existing + (Existing / Page),
            await verify.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM "AuditEntries" """));
    }

    /// <summary>An empty database with the audit schema, for one test to have to itself.</summary>
    private async Task<string> PrepareAsync(string database, bool forEfCore = false)
    {
        var connectionString = await fixture.DatabaseAsync(database);

        if (forEfCore)
        {
            // The EF model owns the schema here, and the Products table with it.
            await using var db = new AuditDbContext(
                new DbContextOptionsBuilder<AuditDbContext>().UseNpgsql(connectionString).Options);

            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync(
                """TRUNCATE TABLE "AuditPropertyChanges", "AuditEntries", "Products" """);

            return connectionString;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await DapperSchema.EnsureCreatedAsync(connection);
        await connection.ExecuteAsync("""TRUNCATE TABLE "AuditPropertyChanges", "AuditEntries" """);

        return connectionString;
    }

    private static AuditEntry Entry(int writer, int index, DateTime? occurredAt = null)
    {
        var entry = new AuditEntry
        {
            Id            = Guid.NewGuid(),
            EntityName    = "Product",
            EntityId      = index.ToString(CultureInfo.InvariantCulture),
            Action        = AuditAction.Updated,
            Actor         = $"writer-{writer.ToString(CultureInfo.InvariantCulture)}",
            CorrelationId = "load",
            OccurredAt    = occurredAt ?? DateTime.UtcNow
        };

        entry.Changes.Add(new AuditPropertyChange
        {
            AuditEntryId = entry.Id,
            PropertyName = "Price",
            OldValue     = "1",
            NewValue     = "2"
        });

        return entry;
    }
}
