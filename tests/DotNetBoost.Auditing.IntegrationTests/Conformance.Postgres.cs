using Dapper;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.Dapper;
using DotNetBoost.Auditing.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using DotNetBoost.Auditing.TestSupport;

namespace DotNetBoost.Auditing.IntegrationTests;

/// <summary>The EF Core store against PostgreSQL.</summary>
[Collection("postgres")]
public sealed class PostgresEfCoreConformance(PostgresFixture fixture) : StoreConformance
{
    private AuditDbContext _db = null!;

    protected override async Task PrepareAsync()
    {
        var connectionString = await fixture.DatabaseAsync("audit_ef");

        _db = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(connectionString)
            .Options);

        // Created once and emptied between tests. Dropping and recreating per test would be the
        // tidier story and several seconds slower over the suite.
        await _db.Database.EnsureCreatedAsync();
        await _db.Database.ExecuteSqlRawAsync(
            """TRUNCATE TABLE "AuditPropertyChanges", "AuditEntries" """);
    }

    protected override Task<IAuditStore> OpenAsync() =>
        Task.FromResult<IAuditStore>(new EfCoreAuditStore(_db));

    /// <summary>The EF Core store only tracks an appended entry; this is what commits it.</summary>
    protected override Task FlushAsync() => _db.SaveChangesAsync();

    protected override async Task<long> CountChangesAsync() =>
        await _db.Set<AuditPropertyChange>().AsNoTracking().CountAsync();

    public override async Task DisposeAsync() => await _db.DisposeAsync();
}

/// <summary>
/// The Dapper store against PostgreSQL — and the first time its PostgreSQL DDL has ever been run.
/// </summary>
[Collection("postgres")]
public sealed class PostgresDapperConformance(PostgresFixture fixture) : StoreConformance
{
    private NpgsqlConnection _connection = null!;

    protected override async Task PrepareAsync()
    {
        var connectionString = await fixture.DatabaseAsync("audit_dapper");

        _connection = new NpgsqlConnection(connectionString);
        await _connection.OpenAsync();

        // The package's own bootstrap. Until this suite existed, the PostgreSQL arm of it was
        // asserted as text and nothing more.
        await DapperSchema.EnsureCreatedAsync(_connection);

        await _connection.ExecuteAsync("""TRUNCATE TABLE "AuditPropertyChanges", "AuditEntries" """);
    }

    protected override Task<IAuditStore> OpenAsync() =>
        Task.FromResult<IAuditStore>(new DapperAuditStore(_connection, new DapperOptions()));

    /// <summary>Nothing: the Dapper store writes when it is called.</summary>
    protected override Task FlushAsync() => Task.CompletedTask;

    protected override Task<long> CountChangesAsync() =>
        _connection.ExecuteScalarAsync<long>("""SELECT COUNT(*) FROM "AuditPropertyChanges" """);

    public override async Task DisposeAsync() => await _connection.DisposeAsync();
}
