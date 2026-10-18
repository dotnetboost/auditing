using Dapper;
using DotNetBoost.Auditing.Dapper;
using Npgsql;

namespace DotNetBoost.Auditing.IntegrationTests;

/// <summary>
/// The schema the Dapper bootstrap builds, and the one piece of driver behaviour the store has to
/// work around.
/// </summary>
[Collection("postgres")]
public sealed class DapperDialectTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Dapper_does_not_expand_an_IN_list_on_Npgsql()
    {
        var connectionString = await fixture.DatabaseAsync("dialect_probe");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("""CREATE TABLE IF NOT EXISTS "Probe" ("Id" UUID NOT NULL)""");

        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

        // This pins the reason AuditStatements.AnyOf exists. Dapper rewrites `IN @Ids` into
        // `IN (@Ids1, @Ids2)` on SQL Server and SQLite and does nothing at all here, so PostgreSQL
        // is handed `IN $1` and refuses it. Nothing in the C# hints at which engine is underneath,
        // which is why the SQLite unit suite passed this happily.
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            connection.QueryAsync<Guid>("""SELECT "Id" FROM "Probe" WHERE "Id" IN @Ids""", new { Ids = ids }));

        Assert.Equal("42601", error.SqlState);

        // The form the store uses instead: one parameter rather than one per id, so there is no
        // 2100-parameter ceiling and the plan is reused across batch sizes.
        var fine = await connection.QueryAsync<Guid>(
            """SELECT "Id" FROM "Probe" WHERE "Id" = ANY(@Ids)""", new { Ids = ids });

        Assert.Empty(fine);
    }

    [Fact]
    public async Task If_Dapper_ever_fixes_that_this_test_is_the_one_that_says_so()
    {
        // Deliberately the same assertion from the other side: AnyOf can be simplified to one
        // spelling the day `IN @p` works on every provider. Until then, the per-dialect form stays.
        Assert.Equal(
            "\"Id\" = ANY(@Ids)",
            AuditStatements.AnyOf(SqlDialect.PostgreSql, "\"Id\"", "@Ids"));

        Assert.Equal(
            "[Id] IN @Ids",
            AuditStatements.AnyOf(SqlDialect.SqlServer, "[Id]", "@Ids"));

        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_bootstrap_creates_every_index_it_promises_on_postgres()
    {
        var connectionString = await fixture.DatabaseAsync("dialect_schema");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await DapperSchema.EnsureCreatedAsync(connection);

        var indexes = (await connection.QueryAsync<string>(
            """
            SELECT indexname FROM pg_indexes
            WHERE tablename IN ('AuditEntries', 'AuditPropertyChanges')
            """)).ToList();

        // Asserted against the catalogue, not against the script that was asked for. Until this
        // suite existed, the PostgreSQL arm of the DDL was checked by searching its text for the
        // names it ought to contain.
        Assert.Contains("IX_AuditEntries_Entity_OccurredAt", indexes);
        Assert.Contains("IX_AuditEntries_Actor_OccurredAt", indexes);
        Assert.Contains("IX_AuditEntries_OccurredAt", indexes);
        Assert.Contains("IX_AuditEntries_CorrelationId", indexes);
        Assert.Contains("IX_AuditPropertyChanges_PropertyName", indexes);
        Assert.Contains("PK_AuditEntries", indexes);
        Assert.Contains("PK_AuditPropertyChanges", indexes);
    }

    [Fact]
    public async Task The_bootstrap_declares_the_cascade_and_the_widths_Core_fixes()
    {
        var connectionString = await fixture.DatabaseAsync("dialect_schema");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await DapperSchema.EnsureCreatedAsync(connection);

        var cascade = await connection.ExecuteScalarAsync<string>(
            """
            SELECT confdeltype FROM pg_constraint
            WHERE conname = 'FK_AuditPropertyChanges_AuditEntries'
            """);

        // 'c' is ON DELETE CASCADE. The one cascade in the schema, so a deleted entry cannot leave
        // unreadable property rows behind even where the store's own delete is bypassed.
        Assert.Equal("c", cascade);

        var widths = (await connection.QueryAsync<(string Column, int? Length)>(
            """
            SELECT column_name AS "Column", character_maximum_length AS "Length"
            FROM information_schema.columns
            WHERE table_name = 'AuditEntries'
            """)).ToDictionary(r => r.Column, r => r.Length, StringComparer.Ordinal);

        Assert.Equal(128, widths["EntityName"]);
        Assert.Equal(64, widths["EntityId"]);
        Assert.Equal(256, widths["Actor"]);
        Assert.Equal(128, widths["CorrelationId"]);
    }

    [Fact]
    public async Task The_bootstrap_is_idempotent_against_a_real_engine()
    {
        var connectionString = await fixture.DatabaseAsync("dialect_idempotent");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await DapperSchema.EnsureCreatedAsync(connection);
        await DapperSchema.EnsureCreatedAsync(connection);
        await DapperSchema.EnsureCreatedAsync(connection);

        Assert.Equal(0, await connection.ExecuteScalarAsync<long>("""SELECT COUNT(*) FROM "AuditEntries" """));
    }
}
