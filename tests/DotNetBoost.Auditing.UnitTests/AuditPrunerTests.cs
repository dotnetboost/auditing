using DotNetBoost.Auditing.Core;
using DotNetBoost.Auditing.Core.Metadata;
using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.Core.Services;
using DotNetBoost.Auditing.TestSupport;
using System.Globalization;

namespace DotNetBoost.Auditing.UnitTests;

public class AuditPrunerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 9, 30, 0, TimeSpan.Zero);

    private static (AuditPruner Pruner, RecordingAuditStore Store) Build(
        Action<AuditingOptions>? configure = null,
        AuditModelRegistry? registry = null,
        MetricsProbe? probe = null)
    {
        var options = new AuditingOptions();
        configure?.Invoke(options);

        var store = new RecordingAuditStore();

        var pruner = new AuditPruner(
            store,
            new ConventionEntityKeyAccessor(),
            registry ?? AuditModelRegistry.Empty,
            options,
            new FixedTimeProvider(Now),
            // With no probe the counters go to a meter nothing listens to.
            new AuditMetrics(probe));

        return (pruner, store);
    }

    [Fact]
    public async Task With_no_retention_configured_anywhere_a_prune_deletes_nothing()
    {
        var (pruner, store) = Build();

        Assert.Equal(0, await pruner.PruneAsync());
        // Not even a round trip: deleting on a default would be deleting history nobody asked to lose.
        Assert.Empty(store.PruneCalls);
    }

    [Fact]
    public async Task The_configured_retention_sets_the_cutoff()
    {
        var (pruner, store) = Build(o => o.Retention = TimeSpan.FromDays(30));

        await pruner.PruneAsync();

        var call = Assert.Single(store.PruneCalls);
        Assert.Equal(Now.UtcDateTime.AddDays(-30), call.Cutoff);
        Assert.Null(call.Scope.EntityName);
        Assert.Empty(call.Scope.ExcludeEntityNames);
    }

    [Fact]
    public async Task An_entity_with_its_own_retention_is_pruned_to_its_own_cutoff_and_excluded_from_the_global_sweep()
    {
        var registry = new AuditModelRegistry(new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["Query"] = typeof(SearchQuery)   // [AuditRetention(Days = 90)]
        });

        var (pruner, store) = Build(o => o.Retention = TimeSpan.FromDays(30), registry);

        await pruner.PruneAsync();

        Assert.Equal(2, store.PruneCalls.Count);

        var perEntity = store.PruneCalls[0];
        Assert.Equal("Query", perEntity.Scope.EntityName);
        Assert.Equal(Now.UtcDateTime.AddDays(-90), perEntity.Cutoff);

        // The whole reason AuditPruneScope exists: without the exclusion, the 30-day global sweep
        // would delete the 90-day trail at 30 days, making [AuditRetention] the opposite of what
        // it says.
        var global = store.PruneCalls[1];
        Assert.Null(global.Scope.EntityName);
        Assert.Equal(new[] { "Query" }, global.Scope.ExcludeEntityNames);
        Assert.Equal(Now.UtcDateTime.AddDays(-30), global.Cutoff);
    }

    [Fact]
    public async Task A_per_entity_retention_works_with_no_global_retention_at_all()
    {
        var registry = new AuditModelRegistry(new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["Query"] = typeof(SearchQuery)
        });

        var (pruner, store) = Build(registry: registry);

        await pruner.PruneAsync();

        var call = Assert.Single(store.PruneCalls);
        Assert.Equal("Query", call.Scope.EntityName);
    }

    [Fact]
    public async Task Deleting_keeps_going_until_a_batch_comes_back_short()
    {
        var (pruner, store) = Build(o => o.Retention = TimeSpan.FromDays(30));

        await store.SeedAsync([.. Enumerable.Range(0, 7).Select(i => new AuditEntry
        {
            Id         = Guid.NewGuid(),
            EntityName = "Product",
            EntityId   = i.ToString(CultureInfo.InvariantCulture),
            OccurredAt = Now.UtcDateTime.AddDays(-90)
        })]);

        var deleted = await pruner.PruneAsync(batchSize: 2);

        // Four round trips for seven entries: 2, 2, 2, 1 — and the short batch is how the pruner
        // knows the backlog is clear.
        Assert.Equal(7, deleted);
        Assert.Equal(4, store.PruneCalls.Count);
        Assert.All(store.PruneCalls, c => Assert.Equal(2, c.BatchSize));
        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task A_manual_prune_ignores_the_configured_retention()
    {
        var (pruner, store) = Build();

        await pruner.PruneOlderThanAsync(TimeSpan.FromDays(7), "Product");

        var call = Assert.Single(store.PruneCalls);
        Assert.Equal("Product", call.Scope.EntityName);
        Assert.Equal(Now.UtcDateTime.AddDays(-7), call.Cutoff);
    }

    [Fact]
    public async Task A_zero_or_negative_age_is_refused()
    {
        var (pruner, _) = Build();

        // Otherwise the cutoff is now, and a prune deletes the entry being written as it is written.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => pruner.PruneOlderThanAsync(TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => pruner.PruneOlderThanAsync(TimeSpan.FromDays(-1)));
    }

    [Fact]
    public async Task A_batch_size_below_one_is_refused()
    {
        var (pruner, _) = Build(o => o.Retention = TimeSpan.FromDays(30));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => pruner.PruneAsync(0));
    }

    [Fact]
    public async Task Erasing_an_entity_removes_its_entries_and_leaves_the_rest()
    {
        var (pruner, store) = Build();

        await store.SeedAsync(
            new AuditEntry { Id = Guid.NewGuid(), EntityName = "Product", EntityId = "1" },
            new AuditEntry { Id = Guid.NewGuid(), EntityName = "Product", EntityId = "2" });

        var erased = await pruner.EraseForAsync(new Product { Id = 1 });

        Assert.Equal(1, erased);
        Assert.Equal("2", Assert.Single(store.Entries).EntityId);
    }

    [Fact]
    public async Task Erasing_an_actor_removes_what_they_recorded()
    {
        var (pruner, store) = Build();

        await store.SeedAsync(
            new AuditEntry { Id = Guid.NewGuid(), Actor = "alice" },
            new AuditEntry { Id = Guid.NewGuid(), Actor = "bob" });

        Assert.Equal(1, await pruner.EraseForActorAsync("alice"));
        Assert.Equal("bob", Assert.Single(store.Entries).Actor);
    }
}
