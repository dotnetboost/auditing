using Dapper;
using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.Dapper;
using DotNetBoost.Auditing.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DotNetBoost.Auditing.IntegrationTests;

/// <summary>
/// The claim that one database serves either relational store, on a real engine.
/// </summary>
/// <remarks>
/// The unit suite tests this on SQLite, where both stores go through the same ADO provider and agree
/// about GUIDs and dates almost by accident. PostgreSQL has real <c>uuid</c> and
/// <c>timestamptz</c> types, and the two stores reach them by different routes — EF Core through its
/// model, Dapper through its own conversions — so this is where the claim is actually at risk.
/// </remarks>
[Collection("postgres")]
public sealed class CrossStoreTests(PostgresFixture fixture) : IAsyncLifetime
{
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await fixture.DatabaseAsync("audit_cross");

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        // Created by the DAPPER bootstrap, deliberately: if EF Core created it, the test would be
        // checking EF against itself.
        await DapperSchema.EnsureCreatedAsync(connection);
        await connection.ExecuteAsync("""TRUNCATE TABLE "AuditPropertyChanges", "AuditEntries" """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private AuditDbContext Context() =>
        new(new DbContextOptionsBuilder<AuditDbContext>().UseNpgsql(_connectionString).Options);

    private async Task<DapperAuditStore> DapperStoreAsync()
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        return new DapperAuditStore(connection, new DapperOptions());
    }

    private static AuditEntry Entry(string entityId, DateTime when) => new()
    {
        Id            = Guid.NewGuid(),
        EntityName    = "Product",
        EntityId      = entityId,
        Action        = AuditAction.Updated,
        Actor         = "alice@example.com",
        CorrelationId = "cross-store",
        OccurredAt    = when,
        Metadata      = """{"ticket":"OPS-1"}""",
        Changes       =
        {
            new AuditPropertyChange
            {
                PropertyName = "Price",
                OldValue     = "24.99",
                NewValue     = "19.99",
                IsRedacted   = false,
                IsTruncated  = true
            }
        }
    };

    [Fact]
    public async Task The_Dapper_bootstrap_builds_a_schema_EF_Core_can_write_to_and_read()
    {
        var when    = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var written = Entry("1", when);

        await using var db = Context();

        var store = new EfCoreAuditStore(db);
        await store.AppendAsync(written);
        await db.SaveChangesAsync();

        var read = await new EfCoreAuditStore(Context()).GetAsync(written.Id);

        Assert.NotNull(read);
        Assert.Equal(when, read.OccurredAt);
    }

    [Fact]
    public async Task What_EF_Core_writes_the_Dapper_store_reads()
    {
        var when    = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var written = Entry("42", when);

        await using (var db = Context())
        {
            await new EfCoreAuditStore(db).AppendAsync(written);
            await db.SaveChangesAsync();
        }

        var dapper = await DapperStoreAsync();
        var read   = await dapper.GetAsync(written.Id);

        Assert.NotNull(read);
        Assert.Equal("Product", read.EntityName);
        Assert.Equal("42", read.EntityId);
        Assert.Equal(AuditAction.Updated, read.Action);
        Assert.Equal("alice@example.com", read.Actor);
        Assert.Equal("""{"ticket":"OPS-1"}""", read.Metadata);

        // The two things most likely to differ between a model-driven provider and hand-written SQL.
        Assert.Equal(when, read.OccurredAt);
        Assert.Equal(DateTimeKind.Utc, read.OccurredAt.Kind);

        var change = Assert.Single(read.Changes);
        Assert.Equal("19.99", change.NewValue);
        Assert.True(change.IsTruncated);
    }

    [Fact]
    public async Task What_the_Dapper_store_writes_EF_Core_reads()
    {
        var when    = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var written = Entry("7", when);

        var dapper = await DapperStoreAsync();
        await dapper.AppendAsync(written);

        await using var db = Context();

        var read = await db.Set<AuditEntry>()
            .AsNoTracking()
            .Include(e => e.Changes)
            .FirstOrDefaultAsync(e => e.Id == written.Id);

        Assert.NotNull(read);
        Assert.Equal("7", read.EntityId);
        Assert.Equal(when, read.OccurredAt);
        Assert.Equal(AuditAction.Updated, read.Action);
        Assert.True(Assert.Single(read.Changes).IsTruncated);
    }

    [Fact]
    public async Task Both_stores_see_the_same_page_in_the_same_order()
    {
        var when = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        var dapper = await DapperStoreAsync();

        for (var i = 0; i < 5; i++)
            await dapper.AppendAsync(Entry(i.ToString(System.Globalization.CultureInfo.InvariantCulture), when.AddMinutes(i)));

        var fromDapper = await dapper.QueryAsync(new AuditQuery { Take = 5 });
        var fromEf     = await new EfCoreAuditStore(Context()).QueryAsync(new AuditQuery { Take = 5 });

        // Same rows, same order, same ids. An application moving between the stores sees one trail
        // rather than two readings of it.
        Assert.Equal(
            fromDapper.Entries.Select(e => e.Id),
            fromEf.Entries.Select(e => e.Id));
    }
}
