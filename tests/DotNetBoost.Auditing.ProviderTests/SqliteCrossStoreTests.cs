using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DotNetBoost.Auditing.Dapper;
using DotNetBoost.Auditing.EntityFrameworkCore;

namespace DotNetBoost.Auditing.ProviderTests;

/// <summary>
/// The claim that one database serves either store, tested rather than asserted in a README.
/// </summary>
/// <remarks>
/// This is the kind of promise that rots quietly: a column width, a GUID format or a timestamp
/// format changes on one side, and nothing fails until somebody migrates between the two stores and
/// finds a trail that reads back empty.
/// </remarks>
public class SqliteCrossStoreTests
{
    private static AuditEntry Entry(string entityId, DateTime when) => new()
    {
        Id            = Guid.NewGuid(),
        EntityName    = "Product",
        EntityId      = entityId,
        Action        = AuditAction.Updated,
        Actor         = "alice@example.com",
        CorrelationId = "cross-store",
        OccurredAt    = when,
        Metadata      = """{"ticket":"OPS-1"}""",
        Changes       =
        {
            new AuditPropertyChange
            {
                PropertyName = "Price",
                OldValue     = "24.99",
                NewValue     = "19.99",
                IsRedacted   = false,
                IsTruncated  = false
            }
        }
    };

    [Fact]
    public async Task What_the_EF_Core_store_writes_the_Dapper_store_reads()
    {
        using var harness = new DapperHarness(migrateSchema: false);

        var when = new DateTime(2026, 10, 17, 9, 30, 0, DateTimeKind.Utc);
        var written = Entry("42", when);

        // EF Core creates the schema and writes the entry.
        await using (var db = harness.EfContext())
        {
            await db.Database.EnsureCreatedAsync();

            var store = new EntityFrameworkCore.EfCoreAuditStore(db);
            await store.AppendAsync(written);
            await db.SaveChangesAsync();
        }

        // Dapper reads it out of the same database.
        var read = await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IAuditManager>().GetAsync(written.Id));

        Assert.NotNull(read);
        Assert.Equal("Product", read.EntityName);
        Assert.Equal("42", read.EntityId);
        Assert.Equal(AuditAction.Updated, read.Action);
        Assert.Equal("alice@example.com", read.Actor);
        Assert.Equal("cross-store", read.CorrelationId);
        Assert.Equal("""{"ticket":"OPS-1"}""", read.Metadata);

        // The format question: a GUID and a timestamp have to mean the same thing to both.
        Assert.Equal(when, read.OccurredAt);
        Assert.Equal(DateTimeKind.Utc, read.OccurredAt.Kind);

        var change = Assert.Single(read.Changes);
        Assert.Equal("Price", change.PropertyName);
        Assert.Equal("24.99", change.OldValue);
        Assert.Equal("19.99", change.NewValue);
    }

    [Fact]
    public async Task What_the_Dapper_store_writes_the_EF_Core_store_reads()
    {
        using var harness = new DapperHarness();

        var when    = new DateTime(2026, 10, 17, 9, 30, 0, DateTimeKind.Utc);
        var written = Entry("7", when);

        await harness.InScopeAsync(sp => sp.GetRequiredService<IAuditStore>().AppendAsync(written));

        await using var db = harness.EfContext();

        var read = await db.Set<AuditEntry>()
            .AsNoTracking()
            .Include(e => e.Changes)
            .FirstOrDefaultAsync(e => e.Id == written.Id);

        Assert.NotNull(read);
        Assert.Equal("7", read.EntityId);
        Assert.Equal(when, read.OccurredAt);

        var change = Assert.Single(read.Changes);
        Assert.Equal("19.99", change.NewValue);
        Assert.False(change.IsRedacted);
    }

    [Fact]
    public async Task The_Dapper_bootstrap_builds_a_schema_EF_Core_can_read_and_write()
    {
        // The bootstrap's DDL, not EF's EnsureCreated.
        using var harness = new DapperHarness();

        var when    = new DateTime(2026, 10, 17, 9, 30, 0, DateTimeKind.Utc);
        var written = Entry("99", when);

        await using (var db = harness.EfContext())
        {
            var store = new EntityFrameworkCore.EfCoreAuditStore(db);
            await store.AppendAsync(written);
            await db.SaveChangesAsync();
        }

        var read = await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IAuditManager>().GetAsync(written.Id));

        Assert.NotNull(read);
        Assert.Equal("99", read.EntityId);
        Assert.Single(read.Changes);
    }

    [Fact]
    public async Task Booleans_survive_the_round_trip_in_both_directions()
    {
        using var harness = new DapperHarness();

        var entry = Entry("5", DateTime.UtcNow);
        entry.Changes.Clear();
        entry.Changes.Add(new AuditPropertyChange
        {
            PropertyName = "SupplierSecret",
            IsRedacted   = true,
            IsTruncated  = true
        });

        await harness.InScopeAsync(sp => sp.GetRequiredService<IAuditStore>().AppendAsync(entry));

        // SQLite has no boolean type, so this is a real question rather than a formality.
        await using var db = harness.EfContext();

        var read = await db.Set<AuditPropertyChange>()
            .AsNoTracking()
            .FirstAsync(c => c.AuditEntryId == entry.Id);

        Assert.True(read.IsRedacted);
        Assert.True(read.IsTruncated);
    }
}
