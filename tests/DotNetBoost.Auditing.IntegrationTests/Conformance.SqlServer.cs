using Dapper;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.Dapper;
using DotNetBoost.Auditing.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using DotNetBoost.Auditing.TestSupport;

namespace DotNetBoost.Auditing.IntegrationTests;

/// <summary>The EF Core store against SQL Server.</summary>
[Collection("sqlserver")]
public sealed class SqlServerEfCoreConformance(SqlServerFixture fixture) : StoreConformance
{
    private AuditDbContext _db = null!;

    protected override async Task PrepareAsync()
    {
        var connectionString = await fixture.DatabaseAsync("audit_ef");

        _db = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlServer(connectionString)
            .Options);

        await _db.Database.EnsureCreatedAsync();

        // DELETE rather than TRUNCATE: SQL Server refuses to truncate a table another table's
        // foreign key references, and the property-changes table references this one.
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM [AuditPropertyChanges]; DELETE FROM [AuditEntries];");
    }

    protected override Task<IAuditStore> OpenAsync() =>
        Task.FromResult<IAuditStore>(new EfCoreAuditStore(_db));

    protected override Task FlushAsync() => _db.SaveChangesAsync();

    protected override async Task<long> CountChangesAsync() =>
        await _db.Set<AuditPropertyChange>().AsNoTracking().CountAsync();

    public override async Task DisposeAsync() => await _db.DisposeAsync();
}

/// <summary>
/// The Dapper store against SQL Server — and the first time its SQL Server DDL has been run.
/// </summary>
[Collection("sqlserver")]
public sealed class SqlServerDapperConformance(SqlServerFixture fixture) : StoreConformance
{
    private SqlConnection _connection = null!;

    protected override async Task PrepareAsync()
    {
        var connectionString = await fixture.DatabaseAsync("audit_dapper");

        _connection = new SqlConnection(connectionString);
        await _connection.OpenAsync();

        await DapperSchema.EnsureCreatedAsync(_connection);
        await _connection.ExecuteAsync("DELETE FROM [AuditPropertyChanges]; DELETE FROM [AuditEntries];");
    }

    protected override Task<IAuditStore> OpenAsync() =>
        Task.FromResult<IAuditStore>(new DapperAuditStore(_connection, new DapperOptions()));

    protected override Task FlushAsync() => Task.CompletedTask;

    protected override Task<long> CountChangesAsync() =>
        _connection.ExecuteScalarAsync<long>("SELECT COUNT_BIG(*) FROM [AuditPropertyChanges]");

    public override async Task DisposeAsync() => await _connection.DisposeAsync();
}

/// <summary>The SQL Server DDL, asserted against the server's own catalogue.</summary>
[Collection("sqlserver")]
public sealed class SqlServerSchemaTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task The_bootstrap_creates_every_index_and_the_cascade_it_promises()
    {
        var connectionString = await fixture.DatabaseAsync("audit_schema");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await DapperSchema.EnsureCreatedAsync(connection);

        var indexes = (await connection.QueryAsync<string>(
            """
            SELECT i.name FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            WHERE t.name IN ('AuditEntries', 'AuditPropertyChanges') AND i.name IS NOT NULL
            """)).ToList();

        Assert.Contains("IX_AuditEntries_Entity_OccurredAt", indexes);
        Assert.Contains("IX_AuditEntries_Actor_OccurredAt", indexes);
        Assert.Contains("IX_AuditEntries_OccurredAt", indexes);
        Assert.Contains("IX_AuditEntries_CorrelationId", indexes);
        Assert.Contains("IX_AuditPropertyChanges_PropertyName", indexes);
        Assert.Contains("PK_AuditEntries", indexes);
        Assert.Contains("PK_AuditPropertyChanges", indexes);

        var cascade = await connection.ExecuteScalarAsync<string>(
            """
            SELECT delete_referential_action_desc FROM sys.foreign_keys
            WHERE name = 'FK_AuditPropertyChanges_AuditEntries'
            """);

        Assert.Equal("CASCADE", cascade);
    }

    [Fact]
    public async Task The_bootstrap_declares_the_widths_Core_fixes()
    {
        var connectionString = await fixture.DatabaseAsync("audit_schema");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await DapperSchema.EnsureCreatedAsync(connection);

        var widths = (await connection.QueryAsync<(string Column, int Length)>(
            """
            SELECT c.name AS [Column], c.max_length AS [Length]
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            WHERE t.name = 'AuditEntries'
            """)).ToDictionary(r => r.Column, r => r.Length, StringComparer.Ordinal);

        // NVARCHAR, so max_length is bytes: twice the character count.
        Assert.Equal(256, widths["EntityName"]);
        Assert.Equal(128, widths["EntityId"]);
        Assert.Equal(512, widths["Actor"]);
        Assert.Equal(256, widths["CorrelationId"]);

        // NVARCHAR(MAX) reports -1, which is how the trail can hold a value of any length.
        Assert.Equal(-1, widths["Metadata"]);
    }

    [Fact]
    public async Task The_bootstrap_is_idempotent_against_a_real_engine()
    {
        var connectionString = await fixture.DatabaseAsync("audit_idempotent");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await DapperSchema.EnsureCreatedAsync(connection);
        await DapperSchema.EnsureCreatedAsync(connection);

        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM [AuditEntries]"));
    }
}
