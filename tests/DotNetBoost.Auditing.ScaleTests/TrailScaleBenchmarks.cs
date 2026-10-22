using Dapper;
using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.Dapper;
using Npgsql;
using System.Diagnostics;
using System.Globalization;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace DotNetBoost.Auditing.ScaleTests;

/// <summary>
/// Four measurements on a million-entry trail, each one attached to a claim the documentation makes
/// and could not previously back.
/// </summary>
/// <remarks>
/// <b>What this is not.</b> Not a gate, not run in CI, and not a pass/fail. Every number depends on
/// the machine, the container runtime and what else is running, so an assertion on any of them would
/// fail builds for reasons nobody can act on. What these produce is a paragraph in
/// <c>docs/testing.md</c>, dated and with the hardware named, so a reader can tell measurement from
/// reasoning. They are deliberately skipped — see <see cref="ScaleFactAttribute"/>.
/// <para>
/// The only assertions are correctness ones: that a query returned what it should have. An index
/// being slow is a result to write down; an index being <i>wrong</i> is a bug, and worth failing on
/// even here.
/// </para>
/// <para>
/// PostgreSQL, because the numbers have to come from somewhere and the integration suite already
/// starts this container. They are therefore Postgres numbers and the write-up says so; SQL Server
/// would be a second run with a second set, and nobody has asked for it yet.
/// </para>
/// </remarks>
public sealed class TrailScaleBenchmarks(ITestOutputHelper output) : IAsyncLifetime
{
    private const int Entries = 1_000_000;

    // The same image the integration suite uses, so the numbers describe the engine that suite
    // tests against rather than a different one.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DOTNETBOOST_SCALE"), "run", StringComparison.Ordinal))
            return;

        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await DapperSchema.EnsureCreatedAsync(connection);

        var seeding = Stopwatch.StartNew();

        // One statement rather than a million store calls: what is being measured is reading and
        // pruning a large trail, not how long it takes to write one through Dapper.
        //
        // Shaped like a real trail rather than uniformly: a few hot entities with long histories and
        // a long tail of quiet ones, because an index that looks good against evenly spread keys can
        // behave quite differently when one entity owns a tenth of the table.
        await connection.ExecuteAsync(
            $"""
            INSERT INTO "AuditEntries"
                ("Id","EntityName","EntityId","Action","Actor","CorrelationId","OccurredAt","Metadata")
            SELECT gen_random_uuid(),
                   'Product',
                   CASE WHEN i % 10 = 0 THEN (i % 5)::text ELSE (i % 50000)::text END,
                   i % 3,
                   'actor-' || (i % 200)::text,
                   'c-' || i::text,
                   now() - (interval '1 second' * i),
                   NULL
            FROM generate_series(1, {Entries.ToString(CultureInfo.InvariantCulture)}) AS i
            """, commandTimeout: 600);

        await connection.ExecuteAsync(
            """
            INSERT INTO "AuditPropertyChanges"
                ("AuditEntryId","PropertyName","OldValue","NewValue","IsRedacted","IsTruncated")
            SELECT "Id",
                   CASE WHEN random() < 0.5 THEN 'Price' ELSE 'Name' END,
                   '1', '2', false, false
            FROM "AuditEntries"
            """, commandTimeout: 600);

        // The tables by name, not a bare ANALYZE: Dapper sends the one-word form in a way
        // PostgreSQL rejects with "syntax error at or near ANALYZE". Naming them is also the
        // narrower thing to do — the stats that matter are these two tables', and without fresh
        // ones the planner is choosing from estimates of an empty table and the numbers below
        // would describe the wrong plan.
        await connection.ExecuteAsync(Analyze);

        seeding.Stop();
        output.WriteLine($"Seeded {Entries:N0} entries and their property rows in {seeding.Elapsed.TotalSeconds:N1}s.");
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    [ScaleFact]
    public async Task One_entitys_history_is_served_by_its_index()
    {
        // The claim: "leading with the address and ending on the sort lets one index serve the
        // filter and the ordering together" — IX_AuditEntries_Entity_OccurredAt.
        var store = Store();

        var query = new AuditQuery { EntityName = "Product", EntityId = "0", Take = 50, IncludeChanges = false };

        var withIndex = await MeasureAsync("one entity's newest 50, indexed", () => store.QueryAsync(query));
        Assert.NotEmpty(withIndex.Entries);

        // Both, because dropping only the entity index leaves the age index able to serve the same
        // read: scan OccurredAt backwards and stop once 50 rows match. The first run of this
        // benchmark dropped one index, measured 0.4ms, and looked like evidence that the index does
        // nothing. It was evidence that the comparison was wrong.
        await WithoutIndexesAsync(
            ["IX_AuditEntries_Entity_OccurredAt", "IX_AuditEntries_OccurredAt"],
            async () =>
            {
                var without = await MeasureAsync(
                    "the same read, both candidate indexes dropped", () => store.QueryAsync(query));

                // The point of the comparison is the time, but a wrong answer either way is a bug.
                Assert.Equal(withIndex.Entries.Count, without.Entries.Count);
            });
    }

    [ScaleFact]
    public async Task The_per_property_question_is_served_by_its_index()
    {
        // The claim behind AuditingModelOptions.IndexPropertyName: "every change anyone ever made to
        // Price is most of why a trail is kept", and in JSON it would be an unindexable scan.
        await using var connection = Connect();

        // Selective, which is what an index is for. The first run of this benchmark counted every
        // Price change — half the table — and found the index slower than a sequential scan. That
        // is correct behaviour for that query and no evidence at all about this one. The question
        // the index exists for is "when did anyone last touch this property", not "how many times
        // in history".
        const string Sql =
            """
            SELECT COUNT(*) FROM "AuditPropertyChanges" WHERE "PropertyName" = 'Rare'
            """;

        await connection.ExecuteAsync(
            """
            UPDATE "AuditPropertyChanges" SET "PropertyName" = 'Rare'
            WHERE "AuditEntryId" IN (SELECT "Id" FROM "AuditEntries" LIMIT 500)
            """, commandTimeout: 600);

        await connection.ExecuteAsync(Analyze);

        var counted = await MeasureAsync(
            "count a rare property's changes (500 of 1,000,000), indexed",
            () => connection.ExecuteScalarAsync<long>(Sql, commandTimeout: 600));

        Assert.True(counted > 0);

        await WithoutIndexAsync("IX_AuditPropertyChanges_PropertyName", async () =>
        {
            var scanned = await MeasureAsync(
                "the same count, index dropped",
                () => connection.ExecuteScalarAsync<long>(Sql, commandTimeout: 600));

            Assert.Equal(counted, scanned);
        });
    }

    [ScaleFact]
    public async Task A_cursor_costs_the_same_at_any_depth_and_an_offset_does_not()
    {
        // The claim from the keyset paging work: "an offset makes the database read and discard
        // everything it skips, so page 4,000 costs 200,000 rows to serve; a cursor costs the same
        // as the first page however deep it goes."
        var store = Store();

        const int Page = 50;

        var first = await store.QueryAsync(new AuditQuery { Take = Page, IncludeChanges = false });

        await MeasureAsync("page 1 by offset", () => store.QueryAsync(
            new AuditQuery { Take = Page, Skip = 0, IncludeChanges = false }));

        await MeasureAsync("page 10,000 by offset (skip 500,000)", () => store.QueryAsync(
            new AuditQuery { Take = Page, Skip = 500_000, IncludeChanges = false }));

        await MeasureAsync("page 1 by cursor", () => store.QueryAsync(
            new AuditQuery { Take = Page, After = AuditCursor.Of(first.Entries[^1]), IncludeChanges = false }));

        // Walk to the same depth, then time the page there. The walk itself is the honest cost of
        // using a cursor to reach the middle of a trail — a client that pages forward has already
        // paid it one page at a time, which is the case the cursor is for.
        var cursor = AuditCursor.Of(first.Entries[^1]);
        var walk   = Stopwatch.StartNew();

        for (var page = 0; page < 500_000 / Page; page++)
        {
            var next = await store.QueryAsync(
                new AuditQuery { Take = Page, After = cursor, IncludeChanges = false });

            if (next.Entries.Count == 0)
                break;

            cursor = AuditCursor.Of(next.Entries[^1]);
        }

        walk.Stop();
        output.WriteLine($"  walked to depth 500,000 by cursor in {walk.Elapsed.TotalSeconds:N1}s " +
                         $"({500_000 / Page:N0} pages)");

        await MeasureAsync("page 10,000 by cursor (at that depth)", () => store.QueryAsync(
            new AuditQuery { Take = Page, After = cursor, IncludeChanges = false }));

        // The two spellings of the keyset predicate, side by side with their plans. This comparison
        // is why the store issues the row-value form wherever the engine has it: the first run of
        // this benchmark measured 23ms for a page at depth 500,000 and 116 seconds to walk there,
        // because the OR form reaches the planner as a Filter rather than an Index Cond. It is kept
        // because SQL Server has no row values and still gets the OR, so the cost of that is a
        // number somebody can look up rather than a thing to rediscover.
        await using var plans = Connect();
        await plans.OpenAsync();

        var parameters = new { at = cursor.OccurredAt, id = cursor.Id, take = Page };

        const string Ored =
            """
            SELECT "Id" FROM "AuditEntries"
            WHERE ("OccurredAt" < @at OR ("OccurredAt" = @at AND "Id" < @id))
            ORDER BY "OccurredAt" DESC, "Id" DESC LIMIT @take
            """;

        const string RowValue =
            """
            SELECT "Id" FROM "AuditEntries"
            WHERE ("OccurredAt", "Id") < (@at, @id)
            ORDER BY "OccurredAt" DESC, "Id" DESC LIMIT @take
            """;

        await MeasureAsync("  the same page, OR form (what SQL Server still gets)",
            () => plans.QueryAsync<Guid>(Ored, parameters));

        await MeasureAsync("  the same page, row-value form (what this engine gets)",
            () => plans.QueryAsync<Guid>(RowValue, parameters));

        foreach (var (name, sql) in new[] { ("OR", Ored), ("row value", RowValue) })
        {
            var plan = await plans.QueryAsync<string>($"EXPLAIN {sql}", parameters);
            output.WriteLine($"  plan, {name}: {string.Join(" / ", plan.Select(line => line.Trim()))}");
        }
    }

    [ScaleFact]
    public async Task The_first_prune_of_a_large_backlog_is_batched()
    {
        // The claim: "the first prune of a trail that has been collecting for a year is a delete of
        // millions of rows, and one statement would hold locks across the table that recording
        // needs." This measures what the batching costs in return.
        var store = Store();

        // Everything older than a day — the seeded trail spans a million seconds, about eleven days,
        // so this is the great majority of it.
        var cutoff = DateTime.UtcNow.AddDays(-1);

        var total   = 0;
        var batches = 0;
        var slowest = TimeSpan.Zero;
        var sweep   = Stopwatch.StartNew();

        while (true)
        {
            var batch = Stopwatch.StartNew();
            var deleted = await store.DeleteOlderThanAsync(cutoff, AuditPruneScope.All, 1000);
            batch.Stop();

            if (batch.Elapsed > slowest)
                slowest = batch.Elapsed;

            total += deleted;
            batches++;

            if (deleted < 1000)
                break;
        }

        sweep.Stop();

        output.WriteLine(
            $"  pruned {total:N0} entries in {batches:N0} batches of 1,000: " +
            $"{sweep.Elapsed.TotalSeconds:N1}s total, " +
            $"{sweep.Elapsed.TotalMilliseconds / batches:N0}ms mean per batch, " +
            $"{slowest.TotalMilliseconds:N0}ms slowest");

        // The number that matters for the design: the slowest single batch is how long recording
        // could be waiting behind the sweep, which is what batching exists to bound.
        Assert.True(total > 0);
    }

    private DapperAuditStore Store() => new(Connect(), new DapperOptions());

    private NpgsqlConnection Connect() => new(_connectionString);

    private async Task<T> MeasureAsync<T>(string what, Func<Task<T>> work)
    {
        // Once to warm the plan and the cache, then the one that is reported: a first run includes
        // parsing and a cold buffer, which is a different question from how the index performs.
        await work();

        var clock  = Stopwatch.StartNew();
        var result = await work();
        clock.Stop();

        output.WriteLine($"  {what}: {clock.Elapsed.TotalMilliseconds:N1}ms");
        return result;
    }

    /// <summary>Drops one index, runs <paramref name="work"/>, and puts it back.</summary>
    private Task WithoutIndexAsync(string index, Func<Task> work) => WithoutIndexesAsync([index], work);

    /// <summary>
    /// Drops every index named, runs <paramref name="work"/>, and puts them all back.
    /// </summary>
    /// <remarks>
    /// Plural because one index is rarely the only one that can serve a query, and leaving a second
    /// candidate in place measures that one rather than the absence of the first.
    /// </remarks>
    private async Task WithoutIndexesAsync(string[] indexes, Func<Task> work)
    {
        await using var connection = Connect();
        await connection.OpenAsync();

        var definitions = new List<string>();

        foreach (var index in indexes)
        {
            var definition = await connection.ExecuteScalarAsync<string>(
                "SELECT indexdef FROM pg_indexes WHERE indexname = @index", new { index });

            Assert.NotNull(definition);
            definitions.Add(definition!);

            await connection.ExecuteAsync($"DROP INDEX {Quote(index)}", commandTimeout: 600);
        }

        try
        {
            await work();
        }
        finally
        {
            // Put them back even if the measurement threw, so one failure does not change what
            // every later measurement is measuring.
            foreach (var definition in definitions)
                await connection.ExecuteAsync(definition, commandTimeout: 600);

            await connection.ExecuteAsync(Analyze);
        }
    }

    /// <summary>Fresh planner statistics for the two tables. See the note at the seeding call.</summary>
    private const string Analyze = """ANALYZE "AuditEntries", "AuditPropertyChanges" """;

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
