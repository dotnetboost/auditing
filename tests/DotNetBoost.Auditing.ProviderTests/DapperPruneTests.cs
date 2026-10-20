using Dapper;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using DotNetBoost.Auditing.Dapper;

namespace DotNetBoost.Auditing.ProviderTests;

public class DapperPruneTests
{
    private static async Task SeedAsync(DapperHarness harness, string entityName, int count, DateTime when)
    {
        await harness.InScopeAsync(async sp =>
        {
            var store = sp.GetRequiredService<IAuditStore>();

            for (var i = 0; i < count; i++)
            {
                await store.AppendAsync(new AuditEntry
                {
                    Id            = Guid.NewGuid(),
                    EntityName    = entityName,
                    EntityId      = i.ToString(CultureInfo.InvariantCulture),
                    Action        = AuditAction.Created,
                    Actor         = "alice",
                    CorrelationId = "seed",
                    OccurredAt    = when,
                    Changes       = { new AuditPropertyChange { PropertyName = "Name", NewValue = "x" } }
                });
            }
        });
    }

    private static async Task<int> CountAsync(DapperHarness harness, string table)
    {
        using var connection = harness.Connect();
        return await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM \"{table}\"");
    }

    [Fact]
    public async Task A_prune_deletes_past_the_retention_and_takes_the_property_rows()
    {
        using var harness = new DapperHarness(o => o.Retention = TimeSpan.FromDays(30));

        await SeedAsync(harness, "Product", 3, DateTime.UtcNow.AddDays(-90));
        await SeedAsync(harness, "Product", 2, DateTime.UtcNow);

        var deleted = await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IAuditManager>().PruneAsync(batchSize: 2));

        Assert.Equal(3, deleted);
        Assert.Equal(2, await CountAsync(harness, "AuditEntries"));

        // Deleted explicitly rather than left to the cascade: SQLite does not enforce foreign keys
        // unless asked, and an orphaned property row is invisible forever.
        Assert.Equal(2, await CountAsync(harness, "AuditPropertyChanges"));
    }

    [Fact]
    public async Task An_entity_with_its_own_retention_survives_the_shorter_global_sweep()
    {
        using var harness = new DapperHarness(o => o.Retention = TimeSpan.FromDays(30));

        // [AuditRetention(Days = 90)] on SearchQuery, declared through the assembly scan.
        await SeedAsync(harness, "Query", 2, DateTime.UtcNow.AddDays(-60));
        await SeedAsync(harness, "Product", 2, DateTime.UtcNow.AddDays(-60));

        var deleted = await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IAuditManager>().PruneAsync());

        // 60 days old: past the 30-day default, inside the 90-day override. Without the exclusion
        // in AuditPruneScope the global sweep would take the queries too, making [AuditRetention]
        // silently the opposite of what it says.
        Assert.Equal(2, deleted);

        using var connection = harness.Connect();
        var remaining = await connection.QueryAsync<string>("SELECT DISTINCT \"EntityName\" FROM \"AuditEntries\"");
        Assert.Equal("Query", Assert.Single(remaining));
    }

    [Fact]
    public async Task Deleting_keeps_going_until_a_batch_comes_back_short()
    {
        using var harness = new DapperHarness(o => o.Retention = TimeSpan.FromDays(30));
        await SeedAsync(harness, "Product", 7, DateTime.UtcNow.AddDays(-90));

        var deleted = await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IAuditManager>().PruneAsync(batchSize: 2));

        Assert.Equal(7, deleted);
        Assert.Equal(0, await CountAsync(harness, "AuditEntries"));
    }

    [Fact]
    public async Task A_manual_prune_ignores_the_configured_retention()
    {
        using var harness = new DapperHarness();
        await SeedAsync(harness, "Product", 2, DateTime.UtcNow.AddDays(-10));

        var deleted = await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IAuditManager>().PruneOlderThanAsync(TimeSpan.FromDays(5), "Product"));

        Assert.Equal(2, deleted);
    }

    [Fact]
    public async Task Erasing_an_entity_removes_its_entries_and_their_property_rows()
    {
        using var harness = new DapperHarness();
        await SeedAsync(harness, "Product", 3, DateTime.UtcNow);

        var erased = await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IAuditManager>().EraseForAsync("Product", "0"));

        Assert.Equal(1, erased);
        Assert.Equal(2, await CountAsync(harness, "AuditEntries"));
        Assert.Equal(2, await CountAsync(harness, "AuditPropertyChanges"));
    }

    [Fact]
    public async Task Erasing_an_actor_removes_what_they_recorded()
    {
        using var harness = new DapperHarness();
        await SeedAsync(harness, "Product", 2, DateTime.UtcNow);

        Assert.Equal(2, await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IAuditManager>().EraseForActorAsync("alice")));

        Assert.Equal(0, await CountAsync(harness, "AuditEntries"));
        Assert.Equal(0, await CountAsync(harness, "AuditPropertyChanges"));
    }

    [Fact]
    public async Task Erasing_a_trail_that_is_not_there_deletes_nothing()
    {
        using var harness = new DapperHarness();
        await SeedAsync(harness, "Product", 1, DateTime.UtcNow);

        Assert.Equal(0, await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IAuditManager>().EraseForAsync("Product", "999")));

        Assert.Equal(1, await CountAsync(harness, "AuditEntries"));
    }
}
