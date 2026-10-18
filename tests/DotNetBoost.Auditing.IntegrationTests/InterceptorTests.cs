using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Metadata;
using DotNetBoost.Auditing.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotNetBoost.Auditing.IntegrationTests;

/// <summary>
/// The library's headline guarantee against a real transactional engine: a change and its trail
/// commit together or neither does.
/// </summary>
/// <remarks>
/// The unit suite proves this on SQLite, whose transactions are a file lock. PostgreSQL has MVCC,
/// real isolation and a server that can refuse a statement mid-transaction — which is where a
/// guarantee built on "the interceptor opens a transaction when the caller has none" is actually
/// worth testing.
/// </remarks>
[Collection("postgres")]
public sealed class InterceptorTests(PostgresFixture fixture) : IAsyncLifetime
{
    private sealed class Actor : IAuditActorAccessor
    {
        public Func<string?> Get { get; set; } = () => null;

        public string? GetActor() => Get();
    }

    private readonly Actor _actor = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await fixture.DatabaseAsync("audit_interceptor");

        var services = new ServiceCollection();

        services.AddDbContext<AuditDbContext>((sp, o) => o.UseNpgsql(connectionString).UseAuditing(sp));

        services.AddAuditing()
            .UseEntityFrameworkCore<AuditDbContext>()
            .DeclareEntity<Product>()
            .Build();

        services.Replace(ServiceDescriptor.Scoped<IAuditActorAccessor>(_ => _actor));

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync(
            """TRUNCATE TABLE "AuditPropertyChanges", "AuditEntries", "Products" """);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private async Task<T> InScopeAsync<T>(Func<AuditDbContext, Task<T>> work)
    {
        using var scope = _provider.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AuditDbContext>());
    }

    private async Task InScopeAsync(Func<AuditDbContext, Task> work)
    {
        using var scope = _provider.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<AuditDbContext>());
    }

    private Task<List<AuditEntry>> EntriesAsync() =>
        InScopeAsync(db => db.Set<AuditEntry>().AsNoTracking().Include(e => e.Changes).ToListAsync());

    [Fact]
    public async Task A_creation_is_captured_with_the_key_postgres_assigned()
    {
        await InScopeAsync(async db =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 24.99m });
            await db.SaveChangesAsync();
        });

        var entry = Assert.Single(await EntriesAsync());

        // A serial column: the id does not exist until the INSERT returns, which is the whole
        // reason for the deferred pass.
        Assert.Equal(AuditAction.Created, entry.Action);
        Assert.NotEqual("0", entry.EntityId);
        Assert.Equal("Widget", Assert.Single(entry.Changes, c => c.PropertyName == "Name").NewValue);
    }

    [Fact]
    public async Task An_update_records_only_the_property_that_moved()
    {
        await InScopeAsync(async db =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 24.99m });
            await db.SaveChangesAsync();
        });

        await InScopeAsync(async db =>
        {
            (await db.Products.SingleAsync()).Price = 19.99m;
            await db.SaveChangesAsync();
        });

        var update = Assert.Single(await EntriesAsync(), e => e.Action == AuditAction.Updated);
        var change = Assert.Single(update.Changes);

        Assert.Equal("Price", change.PropertyName);
        Assert.Equal("24.99", change.OldValue);
        Assert.Equal("19.99", change.NewValue);
    }

    [Fact]
    public async Task A_failure_writing_the_deferred_entry_rolls_the_creation_back_with_it()
    {
        // The actor is only read when an entry is recorded, and a creation with a store-generated
        // key is recorded in the deferred pass — after the INSERT has already succeeded.
        _actor.Get = () => new string('a', AuditColumns.ActorMaxLength + 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InScopeAsync(async db =>
            {
                db.Products.Add(new Product { Name = "Widget", Price = 1m });
                await db.SaveChangesAsync();
            }));

        _actor.Get = () => null;

        // On a real engine with real isolation: the row was inserted, the trail could not be
        // written, and the transaction the interceptor opened took the insert back out.
        Assert.Empty(await InScopeAsync(db => db.Products.AsNoTracking().ToListAsync()));
        Assert.Empty(await EntriesAsync());
    }

    [Fact]
    public async Task Rolling_back_the_callers_own_transaction_takes_the_trail_with_it()
    {
        await InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();

            db.Products.Add(new Product { Name = "Widget", Price = 1m });
            await db.SaveChangesAsync();

            await transaction.RollbackAsync();
        });

        Assert.Empty(await InScopeAsync(db => db.Products.AsNoTracking().ToListAsync()));
        Assert.Empty(await EntriesAsync());
    }

    [Fact]
    public async Task Committing_the_callers_own_transaction_keeps_both()
    {
        await InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();

            db.Products.Add(new Product { Name = "Widget", Price = 1m });
            await db.SaveChangesAsync();

            await transaction.CommitAsync();
        });

        Assert.Single(await InScopeAsync(db => db.Products.AsNoTracking().ToListAsync()));
        Assert.Single(await EntriesAsync());
    }

    [Fact]
    public async Task Everything_saved_together_shares_one_correlation_id()
    {
        await InScopeAsync(async db =>
        {
            db.Products.Add(new Product { Name = "First" });
            db.Products.Add(new Product { Name = "Second" });
            await db.SaveChangesAsync();
        });

        var entries = await EntriesAsync();

        Assert.Equal(2, entries.Count);
        Assert.Single(entries.Select(e => e.CorrelationId).Distinct());
    }

    [Fact]
    public async Task A_deletion_outlives_the_row_it_describes()
    {
        await InScopeAsync(async db =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 9.99m });
            await db.SaveChangesAsync();
        });

        await InScopeAsync(async db =>
        {
            db.Products.Remove(await db.Products.SingleAsync());
            await db.SaveChangesAsync();
        });

        // Nothing cascades to the trail, which here is the feature: after the row is gone this is
        // the only copy of what it said.
        var deleted = Assert.Single(await EntriesAsync(), e => e.Action == AuditAction.Deleted);
        Assert.Equal("9.99", Assert.Single(deleted.Changes, c => c.PropertyName == "Price").OldValue);
        Assert.Empty(await InScopeAsync(db => db.Products.AsNoTracking().ToListAsync()));
    }

    [Fact]
    public async Task Timestamps_round_trip_through_postgres_as_UTC()
    {
        await InScopeAsync(async db =>
        {
            db.Products.Add(new Product { Name = "Widget" });
            await db.SaveChangesAsync();
        });

        // `timestamp with time zone` refuses a DateTime whose Kind is not Utc on the way in, and
        // the value converter is what keeps it so. Without it this test would not have got as far
        // as the assertion.
        Assert.Equal(DateTimeKind.Utc, Assert.Single(await EntriesAsync()).OccurredAt.Kind);
    }
}
