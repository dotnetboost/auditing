using DotNetBoost.Auditing.Core;
using DotNetBoost.Auditing.Core.Attributes;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.EntityFrameworkCore;
using DotNetBoost.Auditing.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotNetBoost.Auditing.ProviderTests;

public class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Heartbeat> Heartbeats => Set<Heartbeat>();
    public DbSet<FluentlyKeyed> FluentlyKeyeds => Set<FluentlyKeyed>();
    public DbSet<CompositeKeyed> CompositeKeyeds => Set<CompositeKeyed>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Translation> Translations => Set<Translation>();
    public DbSet<IgnoredCompositeKeyed> IgnoredCompositeKeyeds => Set<IgnoredCompositeKeyed>();
    public DbSet<LongKeyed> LongKeyeds => Set<LongKeyed>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Account>().Property(a => a.Id).ValueGeneratedNever();
        modelBuilder.Entity<FluentlyKeyed>().HasKey(f => f.Reference);
        modelBuilder.Entity<FluentlyKeyed>().Property(f => f.Reference).ValueGeneratedNever();
        modelBuilder.Entity<CompositeKeyed>().HasKey(c => new { c.TenantId, c.LocalId });
        modelBuilder.Entity<Order>().OwnsOne(o => o.Address);
        modelBuilder.Entity<Translation>().HasKey(t => new { t.EntityName, t.EntityId, t.PropertyName, t.LanguageCode });
        modelBuilder.Entity<IgnoredCompositeKeyed>().HasKey(c => new { c.TenantId, c.LocalId });
        modelBuilder.Entity<LongKeyed>().HasKey(l => l.Code);

        modelBuilder.ApplyAuditing();

        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>
/// Counts the saves EF actually performs, which is how the tests tell "written in the caller's own
/// save" from "written by a second one".
/// </summary>
internal sealed class SaveCounter : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
{
    public int Saves { get; private set; }

    public void Reset() => Saves = 0;

    public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> SavingChanges(
        Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result)
    {
        Saves++;
        return result;
    }

    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
        Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Saves++;
        return ValueTask.FromResult(result);
    }
}

/// <summary>
/// A real SQLite database per test, over a connection the test owns, so the schema and the
/// statements are genuinely exercised rather than approximated by an in-memory provider.
/// </summary>
internal sealed class EfCoreHarness : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    /// <param name="configure">Applied to the options.</param>
    /// <param name="capture">Whether the context records itself through the interceptor.</param>
    /// <param name="build">Applied to the builder after the declarations, just before <c>Build()</c>.</param>
    public EfCoreHarness(
        Action<AuditingOptions>? configure = null,
        bool capture = true,
        Action<AuditingBuilder>? build = null)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();

        services.AddDbContext<TestDbContext>((sp, o) =>
        {
            o.UseSqlite(_connection);
            o.AddInterceptors(Saves);

            // Without this line the tables exist and IAuditManager works, but nothing records
            // itself — which is what the "capture off" tests check.
            if (capture)
                o.UseAuditing(sp);
        });

        var builder = services.AddAuditing(o => configure?.Invoke(o))
                              .UseEntityFrameworkCore<TestDbContext>()
                              .DeclareEntitiesFrom(typeof(Product).Assembly);

        build?.Invoke(builder);
        builder.Build();

        services.Replace(ServiceDescriptor.Scoped<IAuditActorAccessor>(_ => Actors));

        // The only way a suppression is visible from outside: the interceptor records changes
        // nobody asked it about, so there is no return value for it to hand a test.
        services.Replace(ServiceDescriptor.Singleton<IAuditObserver>(Observer));

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<TestDbContext>().Database.EnsureCreated();
    }

    public MutableActorAccessor Actors { get; } = new();

    /// <summary>What the engine and the interceptor decided not to record.</summary>
    public RecordingAuditObserver Observer { get; } = new();

    /// <summary>How many saves EF has performed through this harness.</summary>
    public SaveCounter Saves { get; } = new();

    /// <summary>Runs <paramref name="work"/> in its own scope, with its own context.</summary>
    public async Task<T> InScopeAsync<T>(Func<TestDbContext, IServiceProvider, Task<T>> work)
    {
        using var scope = _provider.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<TestDbContext>(), scope.ServiceProvider);
    }

    /// <summary>Runs <paramref name="work"/> in its own scope, with its own context.</summary>
    public async Task InScopeAsync(Func<TestDbContext, IServiceProvider, Task> work)
    {
        using var scope = _provider.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<TestDbContext>(), scope.ServiceProvider);
    }

    /// <summary>Every entry in the database, newest first, read through a context of its own.</summary>
    public Task<List<AuditEntry>> EntriesAsync() =>
        InScopeAsync(async (db, _) => await db.Set<AuditEntry>()
            .AsNoTracking()
            .Include(e => e.Changes)
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .ToListAsync());

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }
}
