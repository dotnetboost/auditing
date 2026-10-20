using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DotNetBoost.Auditing.EntityFrameworkCore;

namespace DotNetBoost.Auditing.ProviderTests;

/// <summary>Reading and pruning, against the real schema and the real statements.</summary>
public class EfCoreStoreTests
{
    private static async Task SeedAsync(EfCoreHarness harness, int products)
    {
        await harness.InScopeAsync(async (db, _) =>
        {
            for (var i = 1; i <= products; i++)
                db.Products.Add(new Product { Name = $"Widget {i}", Price = i });

            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task A_page_comes_back_newest_first_and_clamped()
    {
        using var harness = new EfCoreHarness(o =>
        {
            o.MaxPageSize     = 3;
            o.DefaultPageSize = 2;
        });

        await SeedAsync(harness, 5);

        var page = await harness.InScopeAsync((_, sp) =>
            sp.GetRequiredService<IAuditManager>().QueryAsync(new AuditQuery { Take = 100 }));

        Assert.Equal(3, page.Entries.Count);
        Assert.Null(page.Total);

        // Newest first, with the id as the tiebreaker so paging a busy trail is stable.
        Assert.True(page.Entries[0].OccurredAt >= page.Entries[^1].OccurredAt);
    }

    [Fact]
    public async Task An_unsized_read_uses_the_default_page_size()
    {
        using var harness = new EfCoreHarness(o =>
        {
            o.MaxPageSize     = 10;
            o.DefaultPageSize = 2;
        });

        await SeedAsync(harness, 5);

        var page = await harness.InScopeAsync((_, sp) =>
            sp.GetRequiredService<IAuditManager>().QueryAsync(new AuditQuery()));

        Assert.Equal(2, page.Entries.Count);
    }

    [Fact]
    public async Task A_total_is_counted_only_when_it_is_asked_for()
    {
        using var harness = new EfCoreHarness();
        await SeedAsync(harness, 5);

        var page = await harness.InScopeAsync((_, sp) =>
            sp.GetRequiredService<IAuditManager>().QueryAsync(new AuditQuery { Take = 2, IncludeTotal = true }));

        Assert.Equal(2, page.Entries.Count);
        Assert.Equal(5, page.Total);
    }

    [Fact]
    public async Task One_entitys_history_is_filtered_to_that_entity()
    {
        using var harness = new EfCoreHarness();
        await SeedAsync(harness, 3);

        await harness.InScopeAsync(async (db, _) =>
        {
            (await db.Products.FirstAsync()).Price = 99m;
            await db.SaveChangesAsync();
        });

        var page = await harness.InScopeAsync(async (db, sp) =>
        {
            var product = await db.Products.FirstAsync();
            return await sp.GetRequiredService<IAuditManager>().HistoryAsync(product);
        });

        Assert.Equal(2, page.Entries.Count);
        Assert.All(page.Entries, e => Assert.Equal("1", e.EntityId));
        Assert.Contains(page.Entries, e => e.Action == AuditAction.Created);
        Assert.Contains(page.Entries, e => e.Action == AuditAction.Updated);
    }

    [Fact]
    public async Task Filters_narrow_by_action_actor_and_window()
    {
        using var harness = new EfCoreHarness();
        harness.Actors.Actor = () => "alice";
        await SeedAsync(harness, 2);

        harness.Actors.Actor = () => null;
        await harness.InScopeAsync(async (db, _) =>
        {
            db.Products.Remove(await db.Products.FirstAsync());
            await db.SaveChangesAsync();
        });

        await harness.InScopeAsync(async (_, sp) =>
        {
            var reader = sp.GetRequiredService<IAuditManager>();

            Assert.Equal(2, (await reader.QueryAsync(new AuditQuery { Actor = "alice" })).Entries.Count);
            Assert.Single((await reader.QueryAsync(new AuditQuery { ActorMissing = true })).Entries);
            Assert.Single((await reader.QueryAsync(new AuditQuery { Action = AuditAction.Deleted })).Entries);

            // Exclusive upper bound, so consecutive windows do not double-count.
            var future = await reader.QueryAsync(new AuditQuery { To = DateTime.UtcNow.AddDays(-1) });
            Assert.Empty(future.Entries);
        });
    }

    [Fact]
    public async Task Changes_can_be_left_out_of_a_read()
    {
        using var harness = new EfCoreHarness();
        await SeedAsync(harness, 1);

        var page = await harness.InScopeAsync((_, sp) =>
            sp.GetRequiredService<IAuditManager>().QueryAsync(new AuditQuery { IncludeChanges = false }));

        Assert.Empty(Assert.Single(page.Entries).Changes);
    }

    [Fact]
    public async Task An_entry_can_be_read_back_by_id_with_its_changes()
    {
        using var harness = new EfCoreHarness();
        await SeedAsync(harness, 1);

        var id = Assert.Single(await harness.EntriesAsync()).Id;

        var entry = await harness.InScopeAsync((_, sp) =>
            sp.GetRequiredService<IAuditManager>().GetAsync(id));

        Assert.NotNull(entry);
        Assert.NotEmpty(entry.Changes);
        Assert.Null(await harness.InScopeAsync((_, sp) =>
            sp.GetRequiredService<IAuditManager>().GetAsync(Guid.NewGuid())));
    }

    [Fact]
    public async Task A_prune_deletes_the_entries_past_their_retention_and_their_property_rows()
    {
        using var harness = new EfCoreHarness(o => o.Retention = TimeSpan.FromDays(30));
        await SeedAsync(harness, 3);

        // Age them past the cutoff. ExecuteUpdate bypasses the change tracker, so this does not
        // audit itself.
        await harness.InScopeAsync(async (db, _) =>
            await db.Set<AuditEntry>().ExecuteUpdateAsync(s =>
                s.SetProperty(e => e.OccurredAt, DateTime.UtcNow.AddDays(-90))));

        var deleted = await harness.InScopeAsync((_, sp) =>
            sp.GetRequiredService<IAuditManager>().PruneAsync(batchSize: 2));

        Assert.Equal(3, deleted);
        Assert.Empty(await harness.EntriesAsync());

        // The property rows go too. They are deleted explicitly rather than left to a cascade the
        // database may not be enforcing — an orphaned change row is invisible forever.
        await harness.InScopeAsync(async (db, _) =>
            Assert.Empty(await db.Set<AuditPropertyChange>().ToListAsync()));
    }

    [Fact]
    public async Task A_prune_keeps_what_is_still_inside_its_retention()
    {
        using var harness = new EfCoreHarness(o => o.Retention = TimeSpan.FromDays(30));
        await SeedAsync(harness, 2);

        Assert.Equal(0, await harness.InScopeAsync((_, sp) =>
            sp.GetRequiredService<IAuditManager>().PruneAsync()));

        Assert.Equal(2, (await harness.EntriesAsync()).Count);
    }

    [Fact]
    public async Task Erasing_an_entity_removes_its_trail_and_leaves_the_others()
    {
        using var harness = new EfCoreHarness();
        await SeedAsync(harness, 3);

        var erased = await harness.InScopeAsync(async (db, sp) =>
        {
            var product = await db.Products.FirstAsync();
            return await sp.GetRequiredService<IAuditManager>().EraseForAsync(product);
        });

        Assert.Equal(1, erased);

        var remaining = await harness.EntriesAsync();
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, e => e.EntityId == "1");

        // No orphaned property rows behind the erased entry.
        await harness.InScopeAsync(async (db, _) =>
            Assert.Equal(2, (await db.Set<AuditPropertyChange>().Select(c => c.AuditEntryId).Distinct().ToListAsync()).Count));
    }

    [Fact]
    public async Task Erasing_an_actor_removes_what_they_recorded()
    {
        using var harness = new EfCoreHarness();

        harness.Actors.Actor = () => "alice";
        await SeedAsync(harness, 2);

        harness.Actors.Actor = () => "bob";
        await SeedAsync(harness, 1);

        var erased = await harness.InScopeAsync((_, sp) =>
            sp.GetRequiredService<IAuditManager>().EraseForActorAsync("alice"));

        Assert.Equal(2, erased);
        Assert.Equal("bob", Assert.Single(await harness.EntriesAsync()).Actor);
    }

    [Fact]
    public async Task Timestamps_come_back_as_UTC()
    {
        using var harness = new EfCoreHarness();
        await SeedAsync(harness, 1);

        // SQLite and SQL Server both return Kind.Unspecified without the converter, which then
        // serializes without a "Z" and reads as local time — an hour or eight wrong about when a
        // change happened.
        Assert.Equal(DateTimeKind.Utc, Assert.Single(await harness.EntriesAsync()).OccurredAt.Kind);
    }
}
