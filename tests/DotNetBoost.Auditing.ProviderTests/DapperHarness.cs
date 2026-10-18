using DotNetBoost.Auditing.Core;
using DotNetBoost.Auditing.Core.Attributes;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Dapper;
using DotNetBoost.Auditing.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Data;

namespace DotNetBoost.Auditing.ProviderTests;

/// <summary>Only used by the cross-store test, to create the schema the way EF Core would.</summary>
public class EfDbContext(DbContextOptions<EfDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyAuditing();
        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>
/// A real SQLite database per test, shared-cache in memory so several connections see the same one
/// — which is what the Dapper store needs, since every scope opens its own connection and the
/// container disposes it.
/// </summary>
internal sealed class DapperHarness : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly ServiceProvider _provider;

    public DapperHarness(
        Action<AuditingOptions>? configure = null,
        bool declareEntities = true,
        bool migrateSchema = true)
    {
        // The database lives as long as one connection to it is open, so the harness holds one.
        ConnectionString = $"DataSource=file:{Guid.NewGuid():N}?mode=memory&cache=shared";

        _keepAlive = new SqliteConnection(ConnectionString);
        _keepAlive.Open();

        var services = new ServiceCollection();

        var builder = services.AddAuditing(o => configure?.Invoke(o))
            .UseDapper(_ => new SqliteConnection(ConnectionString), migrateSchema: false);

        if (declareEntities)
            builder.DeclareEntitiesFrom(typeof(Product).Assembly);

        builder.Build();

        services.Replace(ServiceDescriptor.Scoped<IAuditActorAccessor>(_ => Actors));

        _provider = services.BuildServiceProvider();

        if (migrateSchema)
        {
            // Run the package's own DDL rather than a hand-written script: a bootstrap that is only
            // exercised by its own unit test is a bootstrap nobody has run.
            DapperSchema.EnsureCreatedAsync(_keepAlive).GetAwaiter().GetResult();
        }
    }

    public string ConnectionString { get; }

    public MutableActorAccessor Actors { get; } = new();

    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = _provider.CreateScope();
        return await work(scope.ServiceProvider);
    }

    public async Task InScopeAsync(Func<IServiceProvider, Task> work)
    {
        using var scope = _provider.CreateScope();
        await work(scope.ServiceProvider);
    }

    /// <summary>A connection of the test's own, for arranging and asserting outside the store.</summary>
    public SqliteConnection Connect()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>An EF Core context over the same database, for the cross-store test.</summary>
    public EfDbContext EfContext()
    {
        var options = new DbContextOptionsBuilder<EfDbContext>()
            .UseSqlite(ConnectionString)
            .Options;

        return new EfDbContext(options);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _keepAlive.Dispose();
    }
}
