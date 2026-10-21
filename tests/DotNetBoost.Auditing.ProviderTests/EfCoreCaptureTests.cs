using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DotNetBoost.Auditing.EntityFrameworkCore;

namespace DotNetBoost.Auditing.ProviderTests;

/// <summary>
/// What the interceptor records, against a real SQLite database.
/// </summary>
public class EfCoreCaptureTests
{
    [Fact]
    public async Task An_update_records_the_property_that_moved()
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 24.99m });
            await db.SaveChangesAsync();
        });

        await harness.InScopeAsync(async (db, _) =>
        {
            var product = await db.Products.SingleAsync();
            product.Price = 19.99m;
            await db.SaveChangesAsync();
        });

        var entries = await harness.EntriesAsync();
        var update  = Assert.Single(entries, e => e.Action == AuditAction.Updated);

        Assert.Equal("Product", update.EntityName);
        Assert.Equal("1", update.EntityId);

        var change = Assert.Single(update.Changes);
        Assert.Equal(nameof(Product.Price), change.PropertyName);
        Assert.Equal("24.99", change.OldValue);
        Assert.Equal("19.99", change.NewValue);
    }

    [Fact]
    public async Task A_loaded_entity_records_only_the_property_that_moved()
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 24.99m });
            await db.SaveChangesAsync();
        });

        await harness.InScopeAsync(async (db, _) =>
        {
            var product = await db.Products.SingleAsync();
            product.Name = "Widget";          // set to what it already is
            product.Price = 9.99m;            // the only real change
            await db.SaveChangesAsync();
        });

        // Without the value comparison the trail would carry Name as well and bury the one
        // property that actually moved.
        var update = Assert.Single(await harness.EntriesAsync(), e => e.Action == AuditAction.Updated);
        Assert.Equal(nameof(Product.Price), Assert.Single(update.Changes).PropertyName);
    }

    [Fact]
    public async Task A_detached_update_has_no_before_image_and_records_the_new_values_alone()
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 24.99m });
            await db.SaveChangesAsync();
        });

        await harness.InScopeAsync(async (db, _) =>
        {
            // db.Update() on an entity this context never loaded: EF marks every property modified
            // and fabricates the original values from the current ones, so there is nothing
            // anywhere in the process to diff against.
            db.Update(new Product { Id = 1, Name = "Widget", Price = 9.99m });
            await db.SaveChangesAsync();
        });

        var update = Assert.Single(await harness.EntriesAsync(), e => e.Action == AuditAction.Updated);

        // Recorded rather than skipped, because silence would leave an application believing it is
        // audited when it is not. The price is visible in the row: every property, and no old
        // values.
        var price = Assert.Single(update.Changes, c => c.PropertyName == nameof(Product.Price));
        Assert.Null(price.OldValue);
        Assert.Equal("9.99", price.NewValue);
        Assert.Contains(update.Changes, c => c.PropertyName == nameof(Product.Name));
    }

    [Fact]
    public async Task A_detached_update_can_be_skipped_instead()
    {
        using var harness = new EfCoreHarness(o => o.RecordUpdatesWithoutBeforeImage = false);

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 24.99m });
            await db.SaveChangesAsync();
        });

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Update(new Product { Id = 1, Name = "Widget", Price = 9.99m });
            await db.SaveChangesAsync();
        });

        // For an application that would rather have no entry than a valueless one.
        Assert.DoesNotContain(await harness.EntriesAsync(), e => e.Action == AuditAction.Updated);

        // And it says so. This is the one suppression the interceptor decides after diffing rather
        // than before — the diff came back empty because there was no before-image to use — so it
        // is the interceptor's own report rather than the engine's.
        var report = Assert.Single(harness.Observer.Suppressed, s => s.Action == AuditAction.Updated);
        Assert.Equal(AuditSuppression.NothingChanged, report.Reason);
        Assert.Equal("Product", report.EntityName);
    }

    [Fact]
    public async Task A_creation_with_a_store_generated_key_records_the_key_the_database_assigned()
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 24.99m });
            await db.SaveChangesAsync();
        });

        var created = Assert.Single(await harness.EntriesAsync());

        // The deferred path: this id did not exist when SavingChanges ran.
        Assert.Equal(AuditAction.Created, created.Action);
        Assert.Equal("1", created.EntityId);

        var name = Assert.Single(created.Changes, c => c.PropertyName == nameof(Product.Name));
        Assert.Null(name.OldValue);
        Assert.Equal("Widget", name.NewValue);
    }

    [Fact]
    public async Task A_creation_with_a_client_assigned_key_is_recorded_too()
    {
        using var harness = new EfCoreHarness();
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Accounts.Add(new Account { Id = id, Email = "a@example.com", PasswordHash = "hash" });
            await db.SaveChangesAsync();
        });

        var created = Assert.Single(await harness.EntriesAsync());
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", created.EntityId);
    }

    [Fact]
    public async Task A_deletion_records_the_final_values_and_outlives_the_row()
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 24.99m });
            await db.SaveChangesAsync();
        });

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Remove(await db.Products.SingleAsync());
            await db.SaveChangesAsync();
        });

        var deleted = Assert.Single(await harness.EntriesAsync(), e => e.Action == AuditAction.Deleted);

        // Nothing cascades to the trail, which here is the feature: after the row is gone this is
        // the only copy of what it said.
        Assert.Equal("Widget", Assert.Single(deleted.Changes, c => c.PropertyName == nameof(Product.Name)).OldValue);
        Assert.Null(Assert.Single(deleted.Changes, c => c.PropertyName == nameof(Product.Name)).NewValue);

        await harness.InScopeAsync(async (db, _) => Assert.Empty(await db.Products.ToListAsync()));
    }

    [Fact]
    public async Task A_redacted_property_is_recorded_as_changed_with_its_values_withheld()
    {
        using var harness = new EfCoreHarness();
        var id = Guid.NewGuid();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Accounts.Add(new Account { Id = id, Email = "a@example.com", PasswordHash = "old" });
            await db.SaveChangesAsync();
        });

        await harness.InScopeAsync(async (db, _) =>
        {
            (await db.Accounts.SingleAsync()).PasswordHash = "new";
            await db.SaveChangesAsync();
        });

        var update = Assert.Single(await harness.EntriesAsync(), e => e.Action == AuditAction.Updated);
        var change = Assert.Single(update.Changes);

        Assert.True(change.IsRedacted);
        Assert.Null(change.OldValue);
        Assert.Null(change.NewValue);
    }

    [Fact]
    public async Task An_ignored_property_never_reaches_the_trail()
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 24.99m });
            await db.SaveChangesAsync();
        });

        await harness.InScopeAsync(async (db, _) =>
        {
            // The only change is the ignored property, so there is nothing to record.
            (await db.Products.SingleAsync()).LastViewedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        });

        Assert.DoesNotContain(await harness.EntriesAsync(), e => e.Action == AuditAction.Updated);
    }

    [Fact]
    public async Task An_ignored_entity_is_not_audited_at_all()
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Heartbeats.Add(new Heartbeat { At = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });

        Assert.Empty(await harness.EntriesAsync());
    }

    [Fact]
    public async Task The_trail_is_not_itself_audited()
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget" });
            await db.SaveChangesAsync();
        });

        // One entry for the product and nothing for the entry itself, or this would not terminate.
        var entries = await harness.EntriesAsync();
        Assert.Single(entries);
        Assert.Equal("Product", entries[0].EntityName);
    }

    [Fact]
    public async Task Everything_saved_together_shares_one_correlation_id()
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "First" });
            db.Products.Add(new Product { Name = "Second" });
            db.Accounts.Add(new Account { Id = Guid.NewGuid(), Email = "a@example.com" });
            await db.SaveChangesAsync();
        });

        var entries = await harness.EntriesAsync();

        Assert.Equal(3, entries.Count);
        Assert.Single(entries.Select(e => e.CorrelationId).Distinct());
    }

    [Fact]
    public async Task The_actor_is_captured_from_the_accessor()
    {
        using var harness = new EfCoreHarness();
        harness.Actors.Actor = () => "alice@example.com";

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget" });
            await db.SaveChangesAsync();
        });

        Assert.Equal("alice@example.com", Assert.Single(await harness.EntriesAsync()).Actor);
    }

    [Fact]
    public async Task Without_the_capture_the_tables_still_work_and_nothing_records_itself()
    {
        using var harness = new EfCoreHarness(capture: false);

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget" });
            await db.SaveChangesAsync();
        });

        Assert.Empty(await harness.EntriesAsync());

        // The recorder is still there; it is the capture that was not attached.
        await harness.InScopeAsync(async (db, sp) =>
        {
            await sp.GetRequiredService<IAuditManager>()
                .For("Product", "1")
                .UpdatedAsync([new AuditChange(nameof(Product.Price), 1m, 2m)]);

            await db.SaveChangesAsync();
        });

        Assert.Single(await harness.EntriesAsync());
    }

    [Fact]
    public async Task An_explicit_entry_joins_the_callers_unit_of_work_rather_than_saving_itself()
    {
        using var harness = new EfCoreHarness(capture: false);

        await harness.InScopeAsync(async (db, sp) =>
        {
            await sp.GetRequiredService<IAuditManager>()
                .For("Product", "42")
                .DeletedAsync();

            // Appending only tracks the entity — an entry must not be durable before the change it
            // describes.
            Assert.Empty(await harness.EntriesAsync());

            await db.SaveChangesAsync();
        });

        Assert.Single(await harness.EntriesAsync());
    }

    [Fact]
    public async Task An_owned_types_own_changes_are_not_recorded()
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Orders.Add(new Order { Reference = "A-1", Address = new ShippingAddress { City = "Berlin" } });
            await db.SaveChangesAsync();
        });

        await harness.InScopeAsync(async (db, _) =>
        {
            (await db.Orders.SingleAsync()).Address.City = "Hamburg";
            await db.SaveChangesAsync();
        });

        // The documented limitation: an owned entity shares its owner's key, so it has no address
        // of its own, and the capture does not merge it into the owner's entry.
        Assert.DoesNotContain(await harness.EntriesAsync(), e => e.Action == AuditAction.Updated);
    }

    [Fact]
    public async Task A_fluently_configured_key_is_read_from_the_model()
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.FluentlyKeyeds.Add(new FluentlyKeyed { Reference = 7, Label = "seven" });
            await db.SaveChangesAsync();
        });

        // Neither [Key] nor a property called Id: only the model knows.
        Assert.Equal("7", Assert.Single(await harness.EntriesAsync()).EntityId);
    }

    [Fact]
    public async Task A_composite_key_is_refused_with_the_workarounds_in_the_message()
    {
        using var harness = new EfCoreHarness();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.InScopeAsync(async (db, _) =>
            {
                db.CompositeKeyeds.Add(new CompositeKeyed { TenantId = 1, LocalId = 2, Label = "x" });
                await db.SaveChangesAsync();
            }));

        Assert.Contains("[AuditIgnore]", error.Message, StringComparison.Ordinal);
        Assert.Contains("audit.For(name, id)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recording_off_captures_nothing_and_the_save_still_works()
    {
        using var harness = new EfCoreHarness(o => o.Enabled = false);

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget" });
            await db.SaveChangesAsync();
        });

        Assert.Empty(await harness.EntriesAsync());
        await harness.InScopeAsync(async (db, _) => Assert.Single(await db.Products.ToListAsync()));
    }

    // ── what the interceptor decided NOT to record ─────────────────────────
    //
    // The capture skips an entity before the engine sees it — that is what IsAudited is for, since
    // the alternative is diffing an entity whose changes get thrown away. So the interceptor has to
    // report its own skips, and these are the tests that it does: an EF Core application has no
    // return value anywhere to check, so without the report a trail that stopped covering a table
    // would look exactly like a table nobody changed.

    [Fact]
    public async Task An_ignored_entity_is_skipped_and_says_why()
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Heartbeats.Add(new Heartbeat { At = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });

        Assert.Empty(await harness.EntriesAsync());

        var report = Assert.Single(harness.Observer.Suppressed);
        Assert.Equal(AuditSuppression.EntityIgnored, report.Reason);
        Assert.Equal("Heartbeat", report.EntityName);
        Assert.Equal(AuditAction.Created, report.Action);
    }

    [Fact]
    public async Task Opt_in_mode_says_why_an_unmarked_entity_recorded_nothing()
    {
        using var harness = new EfCoreHarness(o => o.RequireOptIn = true);

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 1m });
            await db.SaveChangesAsync();
        });

        Assert.Empty(await harness.EntriesAsync());

        // This is the report an operator needs: the entity looks audited in the source and the
        // table is empty, and nothing else in the application says why.
        Assert.Equal(AuditSuppression.NotOptedIn, Assert.Single(harness.Observer.Suppressed).Reason);
    }

    [Fact]
    public async Task The_kill_switch_says_why_a_save_recorded_nothing()
    {
        using var harness = new EfCoreHarness(o => o.Enabled = false);

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 1m });
            await db.SaveChangesAsync();
        });

        Assert.Empty(await harness.EntriesAsync());
        Assert.Equal(AuditSuppression.RecordingDisabled, Assert.Single(harness.Observer.Suppressed).Reason);
    }

    [Fact]
    public async Task A_save_that_moved_only_an_ignored_property_says_why()
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 1m });
            await db.SaveChangesAsync();
        });

        harness.Observer.Suppressed.Clear();

        await harness.InScopeAsync(async (db, _) =>
        {
            var product = await db.Products.SingleAsync();
            product.LastViewedAt = DateTime.UtcNow;   // [AuditIgnore]
            await db.SaveChangesAsync();
        });

        // One Created from the first save and nothing from the second.
        Assert.Single(await harness.EntriesAsync());

        var report = Assert.Single(harness.Observer.Suppressed);
        Assert.Equal(AuditSuppression.NothingChanged, report.Reason);
        Assert.Equal("1", report.EntityId);
    }

    [Fact]
    public async Task An_ordinary_capture_reports_nothing()
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 1m });
            await db.SaveChangesAsync();
        });

        Assert.Single(await harness.EntriesAsync());
        Assert.Empty(harness.Observer.Suppressed);

        // And never the missing-rules warning: the interceptor always has the entity, so the model
        // rules always apply. That warning belongs to the explicit path alone.
        Assert.Empty(harness.Observer.WithoutModelRules);
    }

    [Fact]
    public async Task A_synchronous_SaveChanges_is_captured_too()
    {
        using var harness = new EfCoreHarness();

        await harness.InScopeAsync((db, _) =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 1m });
            db.SaveChanges();
            return Task.CompletedTask;
        });

        Assert.Equal(AuditAction.Created, Assert.Single(await harness.EntriesAsync()).Action);
    }
}
