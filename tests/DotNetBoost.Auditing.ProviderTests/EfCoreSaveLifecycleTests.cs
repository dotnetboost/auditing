using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.EntityFrameworkCore;
using DotNetBoost.Auditing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.Auditing.ProviderTests;

/// <summary>
/// How the capture behaves when a save does not run the ordinary way: cancelled, refused, in
/// conflict, suppressed by another interceptor, or made by a context other than the one the store
/// was registered with. Each case runs through <c>SaveChanges</c> and <c>SaveChangesAsync</c>,
/// whose interceptor hooks are separate code.
/// </summary>
public class EfCoreSaveLifecycleTests
{
    // ---- A cancelled save (asynchronous only: a synchronous save takes no token) -----------

    [Fact]
    public async Task A_cancelled_save_leaves_no_transaction_open()
    {
        var cancel = new CancellingInterceptor();
        using var harness = new EfCoreHarness(afterAuditing: o => o.AddInterceptors(cancel));

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget" });

            using var request = new CancellationTokenSource();
            cancel.Cancel = request;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.SaveChangesAsync(request.Token));

            // The deferred creation made the interceptor open a transaction before the token was
            // cancelled. EF reports a cancellation through SaveChangesCanceled, not
            // SaveChangesFailed, so that is where it has to be rolled back.
            Assert.Null(db.Database.CurrentTransaction);
        });

        await harness.InScopeAsync(async (db, _) => Assert.Empty(await db.Products.ToListAsync()));
    }

    [Fact]
    public async Task A_save_retried_after_a_cancellation_records_the_creation_once()
    {
        var cancel = new CancellingInterceptor();
        using var harness = new EfCoreHarness(afterAuditing: o => o.AddInterceptors(cancel));

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget" });

            using var request = new CancellationTokenSource();
            cancel.Cancel = request;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.SaveChangesAsync(request.Token));

            // Retrying a cancelled save is ordinary: nothing was written, and the entity is still
            // Added. The cancelled attempt's state must not survive into this one.
            cancel.Cancel = null;
            await db.SaveChangesAsync();
        });

        var entry = Assert.Single(await harness.EntriesAsync());
        Assert.Equal(AuditAction.Created, entry.Action);
        await harness.InScopeAsync(async (db, _) =>
            Assert.Equal(entry.EntityId, (await db.Products.SingleAsync()).Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    // ---- A save refused before it ran -------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_save_refused_by_a_later_SavingChanges_hook_is_cleaned_up_by_the_next_save(bool sync)
    {
        // EF calls SavingChanges before the part of SaveChanges that reports failures, so nothing
        // tells this interceptor that the save it opened a transaction for is over. The transaction
        // stays open until the context saves again (or is disposed); that save must start over
        // rather than join the abandoned one.
        var refuse = new RefusingInterceptor();
        using var harness = new EfCoreHarness(afterAuditing: o => o.AddInterceptors(refuse));

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget" });

            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveAsync(sync));
            Assert.NotNull(db.Database.CurrentTransaction);

            refuse.Active = false;
            await db.SaveAsync(sync);

            Assert.Null(db.Database.CurrentTransaction);
        });

        var product = await harness.InScopeAsync(async (db, _) => await db.Products.SingleAsync());
        var entry = Assert.Single(await harness.EntriesAsync());
        Assert.Equal(product.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), entry.EntityId);
    }

    // ---- A concurrency conflict -------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_concurrency_conflict_leaves_no_transaction_open(bool sync)
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Gone", Price = 1m });
            await db.SaveChangesAsync();
        });

        await harness.InScopeAsync(async (db, _) =>
        {
            var gone = await db.Products.SingleAsync();

            // Deleted behind this context's back, so updating it affects no row.
            await harness.InScopeAsync(async (other, _) =>
                await other.Products.Where(p => p.Id == gone.Id).ExecuteDeleteAsync());

            gone.Price = 2m;
            db.Products.Add(new Product { Name = "Widget" });

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveAsync(sync));
            Assert.Null(db.Database.CurrentTransaction);
        });
    }

    // An ambient TransactionScope is tested in IntegrationTests (CompositionTests): EF's SQLite
    // provider refuses ambient transactions whether or not this library is involved.

    // ---- A save another interceptor suppressed ---------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_suppressed_creation_is_not_recorded_and_opens_no_transaction(bool sync)
    {
        var suppress = new SuppressingInterceptor();
        using var harness = new EfCoreHarness(beforeAuditing: o => o.AddInterceptors(suppress));

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget" });

            suppress.Active = true;
            Assert.Equal(0, await db.SaveAsync(sync));

            // The interceptor that suppressed the save decided what happens to it. Auditing has
            // nothing to record for a save that never reached the database.
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Empty(db.ChangeTracker.Entries<AuditEntry>());
        });

        Assert.Empty(await harness.EntriesAsync());
        await harness.InScopeAsync(async (db, _) => Assert.Empty(await db.Products.ToListAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_suppressed_update_leaves_no_entry_behind(bool sync)
    {
        var suppress = new SuppressingInterceptor();
        using var harness = new EfCoreHarness(beforeAuditing: o => o.AddInterceptors(suppress));

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 1m });
            await db.SaveChangesAsync();
        });

        await harness.InScopeAsync(async (db, _) =>
        {
            (await db.Products.SingleAsync()).Price = 2m;

            suppress.Active = true;
            await db.SaveAsync(sync);

            // Left tracked, the entry would be written by whatever this context saves next,
            // describing a change that was never made.
            Assert.Empty(db.ChangeTracker.Entries<AuditEntry>());
        });

        Assert.DoesNotContain(await harness.EntriesAsync(), e => e.Action == AuditAction.Updated);
    }

    // ---- A context other than the registered instance --------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_second_context_in_the_scope_writes_its_own_creation_entry(bool sync)
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, sp) =>
        {
            // Same type, same options, same scope — but not the instance the DI container hands
            // out, which is the one the store was bound to.
            await using var second = new TestDbContext(sp.GetRequiredService<DbContextOptions<TestDbContext>>());

            second.Products.Add(new Product { Name = "Widget" });
            await second.SaveAsync(sync);

            Assert.Empty(db.ChangeTracker.Entries<AuditEntry>());
        });

        Assert.Single(await harness.EntriesAsync(), e => e.Action == AuditAction.Created);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_second_context_in_the_scope_writes_its_own_update_entry_in_its_own_save(bool sync)
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 1m });
            await db.SaveChangesAsync();
        });

        await harness.InScopeAsync(async (db, sp) =>
        {
            await using var second = new TestDbContext(sp.GetRequiredService<DbContextOptions<TestDbContext>>());

            (await second.Products.SingleAsync()).Price = 2m;
            await second.SaveAsync(sync);

            Assert.Empty(db.ChangeTracker.Entries<AuditEntry>());
        });

        Assert.Single(await harness.EntriesAsync(), e => e.Action == AuditAction.Updated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_context_without_the_audit_tables_is_refused_with_the_fix(bool sync)
    {
        using var harness = new EfCoreHarness();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.InScopeAsync(async (_, sp) =>
            {
                var options = new DbContextOptionsBuilder<UnmappedDbContext>().UseSqlite(harness.Connection);
                options.UseAuditing(sp);

                await using var unmapped = new UnmappedDbContext(options.Options);

                unmapped.Products.Add(new Product { Name = "Widget", Price = 1m });
                await unmapped.SaveAsync(sync);
            }));

        Assert.Contains(nameof(UnmappedDbContext), error.Message, StringComparison.Ordinal);
        Assert.Contains("ApplyAuditing", error.Message, StringComparison.Ordinal);

        // Refused, not quietly recorded somewhere else.
        Assert.Empty(await harness.EntriesAsync());
    }

    /// <summary>Maps the products table and nothing else: no <c>ApplyAuditing()</c>.</summary>
    private sealed class UnmappedDbContext(DbContextOptions<UnmappedDbContext> options) : DbContext(options)
    {
        public DbSet<Product> Products => Set<Product>();
    }
}
