using Dapper;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.Dapper;
using DotNetBoost.Auditing.EntityFrameworkCore;
using DotNetBoost.Auditing.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.Auditing.ProviderTests;

/// <summary>A context with the audit tables mapped, for the contract tests.</summary>
public class ContractDbContext(DbContextOptions<ContractDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyAuditing();
        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>
/// The contract, against the in-memory double the unit suites are built on.
/// </summary>
/// <remarks>
/// <b>This is the one that makes the other suites mean something.</b> The Core engine's tests run
/// against <see cref="InMemoryAuditStore"/>, and a test built on a double is only worth anything if
/// the double behaves like the real thing — which is this, rather than an assumption.
/// </remarks>
public sealed class InMemoryStoreContractTests : StoreConformance
{
    private readonly InMemoryAuditStore _store = new();

    protected override Task PrepareAsync()
    {
        _store.Clear();
        return Task.CompletedTask;
    }

    protected override Task<IAuditStore> OpenAsync() => Task.FromResult<IAuditStore>(_store);

    /// <summary>Nothing: the double writes when it is called.</summary>
    protected override Task FlushAsync() => Task.CompletedTask;

    protected override Task<long> CountChangesAsync() => Task.FromResult<long>(_store.ChangeCount);
}

/// <summary>The contract, against the EF Core store over a SQLite database.</summary>
public sealed class EfCoreStoreContractTests : StoreConformance
{
    private SqliteConnection _connection = null!;
    private ContractDbContext _db = null!;

    protected override async Task PrepareAsync()
    {
        // An in-memory SQLite database lives as long as a connection to it is open, so the test
        // holds one for the duration.
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        _db = new ContractDbContext(new DbContextOptionsBuilder<ContractDbContext>()
            .UseSqlite(_connection)
            .Options);

        await _db.Database.EnsureCreatedAsync();
    }

    protected override Task<IAuditStore> OpenAsync() => Task.FromResult<IAuditStore>(new EfCoreAuditStore(_db));

    /// <summary>The EF Core store only tracks an appended entry; this is what commits it.</summary>
    protected override Task FlushAsync() => _db.SaveChangesAsync();

    protected override async Task<long> CountChangesAsync() =>
        await _db.Set<AuditPropertyChange>().AsNoTracking().CountAsync();

    public override async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

/// <summary>The contract, against the Dapper store over a SQLite database.</summary>
public sealed class DapperStoreContractTests : StoreConformance
{
    private SqliteConnection _connection = null!;

    protected override async Task PrepareAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        await DapperSchema.EnsureCreatedAsync(_connection);
    }

    protected override Task<IAuditStore> OpenAsync() =>
        Task.FromResult<IAuditStore>(new DapperAuditStore(_connection, new DapperOptions()));

    /// <summary>Nothing: the Dapper store writes when it is called.</summary>
    protected override Task FlushAsync() => Task.CompletedTask;

    protected override Task<long> CountChangesAsync() =>
        _connection.ExecuteScalarAsync<long>("""SELECT COUNT(*) FROM "AuditPropertyChanges" """);

    public override async Task DisposeAsync() => await _connection.DisposeAsync();
}
