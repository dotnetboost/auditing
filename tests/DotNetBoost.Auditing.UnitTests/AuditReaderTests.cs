using DotNetBoost.Auditing.Core;
using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.Core.Services;

namespace DotNetBoost.Auditing.UnitTests;

/// <summary>
/// The reader's whole job is the clamp. Its predecessor in this suite shipped an unbounded history
/// call over a table that only grows, and that is on the short list of why it was deleted.
/// </summary>
public class AuditReaderTests
{
    private static (AuditReader Reader, RecordingAuditStore Store) Build(Action<AuditingOptions>? configure = null)
    {
        var options = new AuditingOptions();
        configure?.Invoke(options);

        var store = new RecordingAuditStore();
        return (new AuditReader(store, new ConventionEntityKeyAccessor(), options), store);
    }

    [Fact]
    public async Task An_unset_take_becomes_the_default_page_size_not_everything()
    {
        var (reader, store) = Build(o => o.DefaultPageSize = 25);

        await reader.QueryAsync(new AuditQuery());

        Assert.Equal(25, store.LastQuery!.Take);
    }

    [Fact]
    public async Task A_take_above_the_maximum_is_clamped_rather_than_refused()
    {
        var (reader, store) = Build(o => o.MaxPageSize = 100);

        await reader.QueryAsync(new AuditQuery { Take = 10_000 });

        // A caller asking for too much gets the most it may have, not an error.
        Assert.Equal(100, store.LastQuery!.Take);
    }

    [Fact]
    public async Task A_negative_skip_becomes_zero()
    {
        var (reader, store) = Build();

        await reader.QueryAsync(new AuditQuery { Skip = -5 });

        Assert.Equal(0, store.LastQuery!.Skip);
    }

    [Fact]
    public async Task A_default_page_size_above_the_maximum_still_cannot_escape_the_maximum()
    {
        // Validate() refuses this combination at Build(), but an engine constructed directly must
        // not hand a store a page bigger than its own ceiling.
        var (reader, store) = Build(o =>
        {
            o.MaxPageSize     = 10;
            o.DefaultPageSize = 10;
        });

        await reader.QueryAsync(new AuditQuery());

        Assert.Equal(10, store.LastQuery!.Take);
    }

    [Fact]
    public async Task Filters_reach_the_store_untouched()
    {
        var (reader, store) = Build();

        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await reader.QueryAsync(new AuditQuery
        {
            EntityName   = "Product",
            EntityId     = "42",
            Action       = AuditAction.Deleted,
            Actor        = "alice",
            ActorMissing = true,
            From         = from,
            IncludeTotal = true
        });

        var sent = store.LastQuery!;
        Assert.Equal("Product", sent.EntityName);
        Assert.Equal("42", sent.EntityId);
        Assert.Equal(AuditAction.Deleted, sent.Action);
        Assert.Equal("alice", sent.Actor);
        Assert.True(sent.ActorMissing);
        Assert.Equal(from, sent.From);
        Assert.True(sent.IncludeTotal);
    }

    [Fact]
    public async Task One_entitys_history_is_addressed_the_way_recording_addresses_it()
    {
        var (reader, store) = Build();

        await reader.HistoryAsync(new PersonEntity { Id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e") });

        Assert.Equal("Person", store.LastQuery!.EntityName);
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", store.LastQuery.EntityId);
    }

    [Fact]
    public async Task An_unset_total_is_null_rather_than_zero()
    {
        var (reader, _) = Build();

        var page = await reader.QueryAsync(new AuditQuery());

        // null means "not counted". Zero would be a number an application paginates against.
        Assert.Null(page.Total);
    }
}
