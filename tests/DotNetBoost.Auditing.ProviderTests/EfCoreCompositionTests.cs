using System.Globalization;
using DotNetBoost.Auditing.Core;
using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.Auditing.ProviderTests;

/// <summary>
/// The capture sharing a context with another library's <c>SaveChanges</c> interceptor, registered
/// before it and after it. The stand-ins in <c>TestSupport</c> mimic what DotNetBoost.MultiLingual
/// does, without referencing it: a row written from <c>SavedChanges</c> through a nested save, a
/// transaction committed in <c>SavedChanges</c> or from EF's event, a save replayed under a
/// retrying strategy. Each case runs through <c>SaveChanges</c> and <c>SaveChangesAsync</c>: the
/// synchronous path has its own hooks, and the commit from EF's event is synchronous on both.
/// </summary>
public class EfCoreCompositionTests
{
    private const string OtherRow = "written by the other library";

    private static EfCoreHarness Harness(
        SaveChangesInterceptor other,
        bool auditingFirst,
        bool retrying = false,
        Action<AuditingBuilder>? build = null,
        Action<AuditingOptions>? configure = null) =>
        new(
            configure: configure,
            build: build,
            beforeAuditing: auditingFirst ? null : o => o.AddInterceptors(other),
            afterAuditing: auditingFirst ? o => o.AddInterceptors(other) : null,
            sqlite: retrying ? b => b.ExecutionStrategy(d => new TestRetryingStrategy(d)) : null);

    private static PostSaveWriterInterceptor Writer(WriterCommit commit) =>
        new(() => new Product { Name = OtherRow }) { Commit = commit };

    private static async Task<List<Product>> ProductsAsync(EfCoreHarness harness) =>
        await harness.InScopeAsync(async (db, _) => await db.Products.AsNoTracking().OrderBy(p => p.Id).ToListAsync());

    private static string Id(Product product) => product.Id.ToString(CultureInfo.InvariantCulture);

    // ---- A row written after the save ------------------------------------------------------

    [Theory]
    [InlineData(true, WriterCommit.InSavedChanges, false)]
    [InlineData(true, WriterCommit.InSavedChanges, true)]
    [InlineData(true, WriterCommit.OnSavedChangesEvent, false)]
    [InlineData(true, WriterCommit.OnSavedChangesEvent, true)]
    [InlineData(false, WriterCommit.OnSavedChangesEvent, false)]
    [InlineData(false, WriterCommit.OnSavedChangesEvent, true)]
    public async Task Both_creations_are_recorded_once_with_their_real_ids(bool auditingFirst, WriterCommit commit, bool sync)
    {
        using var harness = Harness(Writer(commit), auditingFirst);

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget" });
            await db.SaveAsync(sync);

            Assert.Null(db.Database.CurrentTransaction);
        });

        // The other library's row is a creation with a store-generated key, written in a save
        // nested inside this one. It is recorded like the entity, once, and the nested save is not
        // mistaken for a second outermost one.
        var products = await ProductsAsync(harness);
        Assert.Equal(2, products.Count);

        var created = (await harness.EntriesAsync()).Where(e => e.Action == AuditAction.Created).Select(e => e.EntityId).Order().ToList();
        Assert.Equal(products.Select(Id).Order(), created);
    }

    [Theory]
    [InlineData(true, WriterCommit.InSavedChanges, false)]
    [InlineData(true, WriterCommit.InSavedChanges, true)]
    [InlineData(true, WriterCommit.OnSavedChangesEvent, false)]
    [InlineData(true, WriterCommit.OnSavedChangesEvent, true)]
    [InlineData(false, WriterCommit.InSavedChanges, false)]
    [InlineData(false, WriterCommit.InSavedChanges, true)]
    [InlineData(false, WriterCommit.OnSavedChangesEvent, false)]
    [InlineData(false, WriterCommit.OnSavedChangesEvent, true)]
    public async Task A_failed_write_in_the_other_library_rolls_back_everything(bool auditingFirst, WriterCommit commit, bool sync)
    {
        var writer = Writer(commit);
        writer.FailWrite = true;
        using var harness = Harness(writer, auditingFirst);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget" });
            await db.SaveAsync(sync);
        }));

        // With the capture registered first it owns the transaction, and the other library writes
        // inside it after the capture's SavedChanges has returned. Committing there would leave the
        // entity and its entry behind with the other library's row missing.
        Assert.Empty(await ProductsAsync(harness));
        Assert.Empty(await harness.EntriesAsync());
    }

    [Theory]

    [InlineData(false)]

    [InlineData(true)]
    public async Task A_throw_from_a_later_SavedChanges_rolls_back_everything(bool sync)
    {
        using var harness = Harness(new ThrowAfterSaveInterceptor(), auditingFirst: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget" });
            await db.SaveAsync(sync);

            Assert.Null(db.Database.CurrentTransaction);
        }));

        Assert.Empty(await ProductsAsync(harness));
        Assert.Empty(await harness.EntriesAsync());
    }

    // ---- A transaction committed before the capture wrote its entries ----------------------

    [Theory]

    [InlineData(false)]

    [InlineData(true)]
    public async Task An_early_commit_by_the_other_library_is_reported(bool sync)
    {
        // Registered first and committing in its own SavedChanges, the other library's transaction
        // is gone by the time the capture writes the deferred entries.
        using var harness = Harness(Writer(WriterCommit.InSavedChanges), auditingFirst: false);

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget" });
            await db.SaveAsync(sync);
        });

        // Written anyway, as before, but no longer silently.
        var (correlationId, entries) = Assert.Single(harness.Observer.LostAtomicity);
        Assert.Equal(2, entries);
        Assert.All(await harness.EntriesAsync(), e => Assert.Equal(correlationId, e.CorrelationId));
    }

    [Theory]

    [InlineData(false)]

    [InlineData(true)]
    public async Task An_early_commit_by_the_other_library_fails_the_save_under_Throw(bool sync)
    {
        using var harness = Harness(
            Writer(WriterCommit.InSavedChanges),
            auditingFirst: false,
            build: b => b.OnLostAtomicity(LostAtomicity.Throw));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget" });
            await db.SaveAsync(sync);
        }));

        Assert.Contains("committed before", error.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Observer.LostAtomicity);
    }

    [Theory]
    [InlineData(true, WriterCommit.InSavedChanges, false)]
    [InlineData(true, WriterCommit.InSavedChanges, true)]
    [InlineData(true, WriterCommit.OnSavedChangesEvent, false)]
    [InlineData(true, WriterCommit.OnSavedChangesEvent, true)]
    [InlineData(false, WriterCommit.OnSavedChangesEvent, false)]
    [InlineData(false, WriterCommit.OnSavedChangesEvent, true)]
    public async Task An_atomic_composition_reports_nothing(bool auditingFirst, WriterCommit commit, bool sync)
    {
        using var harness = Harness(Writer(commit), auditingFirst, build: b => b.OnLostAtomicity(LostAtomicity.Throw));

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget" });
            await db.SaveAsync(sync);
        });

        Assert.Empty(harness.Observer.LostAtomicity);
    }

    // ---- A save replayed under a retrying strategy -----------------------------------------

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task A_replayed_save_records_each_change_once(bool auditingFirst, bool sync)
    {
        var replay = new ReplayingInterceptor();
        using var harness = Harness(replay, auditingFirst, retrying: true);

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 1m });
            await db.SaveChangesAsync();
        });

        await harness.InScopeAsync(async (db, _) =>
        {
            (await db.Products.SingleAsync()).Price = 2m;
            db.Products.Add(new Product { Name = "Gadget" });
            await db.SaveAsync(sync);

            Assert.Null(db.Database.CurrentTransaction);
        });

        var entries = await harness.EntriesAsync();
        Assert.Single(entries, e => e.Action == AuditAction.Updated);
        Assert.Equal(
            (await ProductsAsync(harness)).Select(Id).Order(),
            entries.Where(e => e.Action == AuditAction.Created).Select(e => e.EntityId).Order());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task A_replay_retried_after_a_transient_failure_records_each_change_once(bool auditingFirst, bool sync)
    {
        var replay = new ReplayingInterceptor();
        using var harness = Harness(replay, auditingFirst, retrying: true);

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 1m });
            await db.SaveChangesAsync();
        });

        replay.FailAttempts = 1;
        await harness.InScopeAsync(async (db, _) =>
        {
            // An update is recorded into the save while SavingChanges runs, so its entry is tracked
            // before the replay starts — and the replay puts the tracker back before retrying.
            (await db.Products.SingleAsync()).Price = 2m;
            db.Products.Add(new Product { Name = "Gadget" });
            await db.SaveAsync(sync);
        });

        Assert.Equal(3, replay.Attempts);

        var entries = await harness.EntriesAsync();
        Assert.Single(entries, e => e.Action == AuditAction.Updated);
        Assert.Equal(
            (await ProductsAsync(harness)).Select(Id).Order(),
            entries.Where(e => e.Action == AuditAction.Created).Select(e => e.EntityId).Order());
    }
}
