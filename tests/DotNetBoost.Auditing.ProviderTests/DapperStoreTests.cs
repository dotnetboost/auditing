using Dapper;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using DotNetBoost.Auditing.Dapper;

namespace DotNetBoost.Auditing.ProviderTests;

/// <summary>Recording and reading through Dapper, against a real SQLite database.</summary>
public class DapperStoreTests
{
    private static Task<AuditEntry?> RecordAsync(
        DapperHarness harness,
        string entityId = "42",
        AuditAction action = AuditAction.Updated,
        params AuditChange[] changes) =>
        harness.InScopeAsync(sp => sp.GetRequiredService<IAuditManager>()
            .For("Product", entityId)
            .RecordAsync(action, changes));

    [Fact]
    public async Task The_bootstrap_creates_tables_the_store_can_use()
    {
        using var harness = new DapperHarness();

        var entry = await RecordAsync(harness, changes: new AuditChange("Price", 24.99m, 19.99m));

        Assert.NotNull(entry);

        using var connection = harness.Connect();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM \"AuditEntries\""));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM \"AuditPropertyChanges\""));
    }

    [Fact]
    public async Task The_bootstrap_is_idempotent()
    {
        using var harness = new DapperHarness();

        using var connection = harness.Connect();
        await DapperSchema.EnsureCreatedAsync(connection);
        await DapperSchema.EnsureCreatedAsync(connection);

        var entry = await RecordAsync(harness, changes: new AuditChange("Price", 1m, 2m));
        Assert.NotNull(entry);
    }

    [Fact]
    public async Task An_entry_reads_back_with_everything_it_was_written_with()
    {
        using var harness = new DapperHarness();
        harness.Actors.Actor = () => "alice@example.com";

        var written = await RecordAsync(
            harness,
            changes:
            [
                new AuditChange("Price", 24.99m, 19.99m),
                new AuditChange("Name", "Widget", "Gadget")
            ]);

        Assert.NotNull(written);

        var read = await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IAuditManager>().GetAsync(written.Id));

        Assert.NotNull(read);
        Assert.Equal("Product", read.EntityName);
        Assert.Equal("42", read.EntityId);
        Assert.Equal(AuditAction.Updated, read.Action);
        Assert.Equal("alice@example.com", read.Actor);
        Assert.Equal(written.CorrelationId, read.CorrelationId);
        Assert.Equal(2, read.Changes.Count);

        var price = Assert.Single(read.Changes, c => c.PropertyName == "Price");
        Assert.Equal("24.99", price.OldValue);
        Assert.Equal("19.99", price.NewValue);
    }

    [Fact]
    public async Task A_timestamp_comes_back_as_UTC()
    {
        using var harness = new DapperHarness();

        var written = await RecordAsync(harness, changes: new AuditChange("Price", 1m, 2m));

        var read = await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IAuditManager>().GetAsync(written!.Id));

        // SQLite returns Kind.Unspecified. Left alone it serializes without a "Z" and every client
        // reads it as local time — and the two stores would then disagree about when a change
        // happened.
        Assert.Equal(DateTimeKind.Utc, read!.OccurredAt.Kind);
        Assert.Equal(written!.OccurredAt, read.OccurredAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_declared_entity_gets_its_redaction_applied()
    {
        using var harness = new DapperHarness();

        var entry = await RecordAsync(harness, changes: new AuditChange("SupplierSecret", "old", "swordfish"));

        // The case this package exists to get right: everything here is addressed by name, so
        // without the declaration the engine has no type to read [AuditRedact] from.
        var change = Assert.Single(entry!.Changes);
        Assert.True(change.IsRedacted);
        Assert.Null(change.NewValue);

        using var connection = harness.Connect();
        var stored = await connection.ExecuteScalarAsync<string?>(
            "SELECT \"NewValue\" FROM \"AuditPropertyChanges\" WHERE \"PropertyName\" = 'SupplierSecret'");

        // Asserted against the database, not the returned object: the secret must not be on disk.
        Assert.Null(stored);
    }

    [Fact]
    public async Task An_undeclared_entity_records_the_value_in_full()
    {
        using var harness = new DapperHarness(declareEntities: false);

        var entry = await RecordAsync(harness, changes: new AuditChange("SupplierSecret", "old", "swordfish"));

        // The documented price of addressing by hand with nothing declared. This is why
        // DeclareEntitiesFrom is not optional in a Dapper application, and why the test exists:
        // somebody should have to delete it deliberately to lose the warning.
        Assert.False(Assert.Single(entry!.Changes).IsRedacted);
    }

    [Fact]
    public async Task An_ignored_property_never_reaches_the_table()
    {
        using var harness = new DapperHarness();

        var entry = await RecordAsync(
            harness,
            changes:
            [
                new AuditChange("LastViewedAt", DateTime.UtcNow, DateTime.UtcNow.AddHours(1)),
                new AuditChange("Price", 1m, 2m)
            ]);

        Assert.Equal("Price", Assert.Single(entry!.Changes).PropertyName);
    }

    [Fact]
    public async Task An_update_of_only_ignored_properties_records_nothing()
    {
        using var harness = new DapperHarness();

        var entry = await RecordAsync(harness, changes: new AuditChange("LastViewedAt", 1, 2));

        Assert.Null(entry);

        using var connection = harness.Connect();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM \"AuditEntries\""));
    }

    [Fact]
    public async Task Several_entries_are_appended_in_one_call()
    {
        using var harness = new DapperHarness();

        await harness.InScopeAsync(async sp =>
        {
            var store = sp.GetRequiredService<IAuditStore>();

            AuditEntry Entry(string id) => new()
            {
                Id            = Guid.NewGuid(),
                EntityName    = "Product",
                EntityId      = id,
                Action        = AuditAction.Created,
                CorrelationId = "batch",
                OccurredAt    = DateTime.UtcNow,
                Changes       = { new AuditPropertyChange { PropertyName = "Name", NewValue = "Widget " + id } }
            };

            await store.AppendRangeAsync([Entry("1"), Entry("2"), Entry("3")]);
        });

        using var connection = harness.Connect();
        Assert.Equal(3, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM \"AuditEntries\""));
        Assert.Equal(3, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM \"AuditPropertyChanges\""));
    }

    [Fact]
    public async Task A_page_is_newest_first_and_clamped()
    {
        using var harness = new DapperHarness(o =>
        {
            o.MaxPageSize     = 2;
            o.DefaultPageSize = 2;
        });

        for (var i = 1; i <= 5; i++)
            await RecordAsync(harness, entityId: i.ToString(System.Globalization.CultureInfo.InvariantCulture), changes: new AuditChange("Price", i, i + 1));

        var page = await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IAuditManager>().QueryAsync(new AuditQuery { Take = 10_000 }));

        Assert.Equal(2, page.Entries.Count);
        Assert.True(page.Entries[0].OccurredAt >= page.Entries[^1].OccurredAt);
    }

    [Fact]
    public async Task Paging_is_stable_when_every_entry_shares_a_timestamp()
    {
        using var harness = new DapperHarness();

        // Written through the store directly with one timestamp, which is what the Id tiebreaker in
        // the ORDER BY exists for: without it two entries in the same tick can swap places between
        // pages, showing one twice and skipping another.
        var when = new DateTime(2026, 10, 17, 9, 30, 0, DateTimeKind.Utc);

        await harness.InScopeAsync(async sp =>
        {
            var store = sp.GetRequiredService<IAuditStore>();

            for (var i = 0; i < 6; i++)
            {
                await store.AppendAsync(new AuditEntry
                {
                    Id            = Guid.NewGuid(),
                    EntityName    = "Product",
                    EntityId      = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Action        = AuditAction.Created,
                    CorrelationId = "same-tick",
                    OccurredAt    = when
                });
            }
        });

        var first = await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IAuditManager>().QueryAsync(new AuditQuery { Take = 3 }));

        var second = await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IAuditManager>().QueryAsync(new AuditQuery { Skip = 3, Take = 3 }));

        var ids = first.Entries.Concat(second.Entries).Select(e => e.Id).ToList();
        Assert.Equal(6, ids.Distinct().Count());
    }

    [Fact]
    public async Task Filters_narrow_the_page()
    {
        using var harness = new DapperHarness();

        harness.Actors.Actor = () => "alice";
        await RecordAsync(harness, "1", AuditAction.Created, new AuditChange("Name", null, "First"));

        harness.Actors.Actor = () => null;
        await RecordAsync(harness, "2", AuditAction.Deleted, new AuditChange("Name", "Second", null));

        await harness.InScopeAsync(async sp =>
        {
            var reader = sp.GetRequiredService<IAuditManager>();

            Assert.Single((await reader.QueryAsync(new AuditQuery { Actor = "alice" })).Entries);
            Assert.Single((await reader.QueryAsync(new AuditQuery { ActorMissing = true })).Entries);
            Assert.Single((await reader.QueryAsync(new AuditQuery { Action = AuditAction.Deleted })).Entries);
            Assert.Single((await reader.QueryAsync(new AuditQuery { EntityId = "1", EntityName = "Product" })).Entries);
            Assert.Empty((await reader.QueryAsync(new AuditQuery { To = DateTime.UtcNow.AddDays(-1) })).Entries);
            Assert.Equal(2, (await reader.QueryAsync(new AuditQuery { From = DateTime.UtcNow.AddDays(-1) })).Entries.Count);
        });
    }

    [Fact]
    public async Task A_total_is_counted_only_when_it_is_asked_for()
    {
        using var harness = new DapperHarness();

        for (var i = 1; i <= 3; i++)
            await RecordAsync(harness, i.ToString(System.Globalization.CultureInfo.InvariantCulture), changes: new AuditChange("Price", i, i + 1));

        await harness.InScopeAsync(async sp =>
        {
            var reader = sp.GetRequiredService<IAuditManager>();

            Assert.Null((await reader.QueryAsync(new AuditQuery { Take = 2 })).Total);
            Assert.Equal(3, (await reader.QueryAsync(new AuditQuery { Take = 2, IncludeTotal = true })).Total);
        });
    }

    [Fact]
    public async Task Changes_can_be_left_out_of_a_page()
    {
        using var harness = new DapperHarness();
        await RecordAsync(harness, changes: new AuditChange("Price", 1m, 2m));

        var page = await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IAuditManager>().QueryAsync(new AuditQuery { IncludeChanges = false }));

        Assert.Empty(Assert.Single(page.Entries).Changes);
    }

    [Fact]
    public async Task An_unknown_id_reads_back_as_null()
    {
        using var harness = new DapperHarness();

        Assert.Null(await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IAuditManager>().GetAsync(Guid.NewGuid())));
    }
}
