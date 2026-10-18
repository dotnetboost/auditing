using DotNetBoost.Auditing.Core.Metadata;
using DotNetBoost.Auditing.Core.Models;
using Microsoft.EntityFrameworkCore;
using DotNetBoost.Auditing.EntityFrameworkCore;

namespace DotNetBoost.Auditing.ProviderTests;

/// <summary>
/// The library's headline guarantee, against a real database: a change and its trail commit
/// together or neither does.
/// </summary>
public class EfCoreAtomicityTests
{
    [Fact]
    public async Task An_update_is_recorded_in_the_callers_own_save()
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 24.99m });
            await db.SaveChangesAsync();
        });

        harness.Saves.Reset();

        await harness.InScopeAsync(async (db, _) =>
        {
            (await db.Products.SingleAsync()).Price = 19.99m;
            await db.SaveChangesAsync();
        });

        // One save, one batch, one transaction — the entry went to the database alongside the row
        // it describes, and no transaction had to be opened to make that true.
        Assert.Equal(1, harness.Saves.Saves);
        Assert.Single(await harness.EntriesAsync(), e => e.Action == AuditAction.Updated);
    }

    [Fact]
    public async Task A_creation_with_a_store_generated_key_costs_a_second_save()
    {
        using var harness = new EfCoreHarness();
        harness.Saves.Reset();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget" });
            await db.SaveChangesAsync();
        });

        // The documented cost of a key that does not exist until the INSERT has run. The second
        // save is the deferred pass, and the interceptor's own transaction is what keeps the two
        // atomic.
        Assert.Equal(2, harness.Saves.Saves);
        Assert.Single(await harness.EntriesAsync());
    }

    [Fact]
    public async Task A_client_assigned_key_needs_no_second_save()
    {
        using var harness = new EfCoreHarness();
        harness.Saves.Reset();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Accounts.Add(new Account { Id = Guid.NewGuid(), Email = "a@example.com" });
            await db.SaveChangesAsync();
        });

        // Nothing to defer, so the creation and its entry go in one save — the same guarantee an
        // update gets. This is the reason the README recommends assigning keys in code.
        Assert.Equal(1, harness.Saves.Saves);
        Assert.Single(await harness.EntriesAsync());
    }

    [Fact]
    public async Task Rolling_back_the_callers_transaction_takes_the_trail_with_it()
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, _) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();

            db.Products.Add(new Product { Name = "Widget", Price = 1m });
            await db.SaveChangesAsync();

            await transaction.RollbackAsync();
        });

        // Neither the row nor its entry. If the entry had been written in a transaction of its own,
        // the trail would now claim a product was created that never existed.
        Assert.Empty(await harness.EntriesAsync());
        await harness.InScopeAsync(async (db, _) => Assert.Empty(await db.Products.ToListAsync()));
    }

    [Fact]
    public async Task Committing_the_callers_transaction_keeps_both()
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, _) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();

            db.Products.Add(new Product { Name = "Widget", Price = 1m });
            await db.SaveChangesAsync();

            await transaction.CommitAsync();
        });

        Assert.Single(await harness.EntriesAsync());
        await harness.InScopeAsync(async (db, _) => Assert.Single(await db.Products.ToListAsync()));
    }

    [Fact]
    public async Task A_failure_writing_the_deferred_entry_rolls_the_change_back_with_it()
    {
        using var harness = new EfCoreHarness();

        // The actor is only read when an entry is recorded, and a store-generated creation is
        // recorded in the deferred pass — AFTER the INSERT has already succeeded. An over-long
        // actor therefore fails exactly where the guarantee is hardest to keep.
        harness.Actors.Actor = () => new string('a', AuditColumns.ActorMaxLength + 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.InScopeAsync(async (db, _) =>
            {
                db.Products.Add(new Product { Name = "Widget", Price = 1m });
                await db.SaveChangesAsync();
            }));

        // This is the whole point of the interceptor owning a transaction: the product's INSERT had
        // succeeded, and it is gone again because its trail could not be written.
        await harness.InScopeAsync(async (db, _) => Assert.Empty(await db.Products.ToListAsync()));
        Assert.Empty(await harness.EntriesAsync());
    }

    [Fact]
    public async Task A_failure_recording_an_update_fails_the_save_before_anything_is_written()
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 24.99m });
            await db.SaveChangesAsync();
        });

        harness.Actors.Actor = () => new string('a', AuditColumns.ActorMaxLength + 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.InScopeAsync(async (db, _) =>
            {
                (await db.Products.SingleAsync()).Price = 19.99m;
                await db.SaveChangesAsync();
            }));

        // An update is recorded during SavingChanges, so the failure happens before the UPDATE is
        // sent at all. The price in the database is still the old one.
        await harness.InScopeAsync(async (db, _) =>
            Assert.Equal(24.99m, (await db.Products.SingleAsync()).Price));
    }

    [Fact]
    public async Task Several_saves_on_one_context_each_get_their_own_state()
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "First" });
            await db.SaveChangesAsync();

            db.Products.Add(new Product { Name = "Second" });
            await db.SaveChangesAsync();

            (await db.Products.FirstAsync()).Price = 5m;
            await db.SaveChangesAsync();
        });

        var entries = await harness.EntriesAsync();

        // Two creations and one update, and no entry recorded twice — which is what per-save state
        // cleared in a finally buys.
        Assert.Equal(2, entries.Count(e => e.Action == AuditAction.Created));
        Assert.Single(entries, e => e.Action == AuditAction.Updated);
    }
}
