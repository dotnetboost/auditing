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
    public async Task A_cursor_reaches_the_store_and_the_page_carries_the_next_one()
    {
        var (reader, store) = Build();

        var newest = new AuditEntry
        {
            Id            = Guid.NewGuid(),
            EntityName    = "Product",
            EntityId      = "1",
            Action        = AuditAction.Updated,
            CorrelationId = "c1",
            OccurredAt    = new DateTime(2026, 10, 22, 9, 30, 0, DateTimeKind.Utc)
        };

        await store.SeedAsync(newest);

        var cursor = new AuditCursor(newest.OccurredAt.AddHours(1), Guid.NewGuid());
        var page   = await reader.QueryAsync(new AuditQuery { After = cursor });

        // Passed through the clamp untouched — it is not a size, so there is nothing to clamp.
        Assert.Equal(cursor, store.LastQuery!.After);

        // And the page says where it ended, so a caller can ask for the next one without knowing
        // how a cursor is built.
        Assert.Equal(AuditCursor.Of(newest), page.NextCursor);
    }

    [Fact]
    public async Task An_empty_page_carries_no_cursor_because_the_walk_is_over()
    {
        var (reader, _) = Build();

        var page = await reader.QueryAsync(new AuditQuery());

        Assert.Empty(page.Entries);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Skip_and_After_together_are_refused_rather_than_one_winning()
    {
        var (reader, store) = Build();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => reader.QueryAsync(new AuditQuery
        {
            Skip  = 10,
            After = new AuditCursor(DateTime.UtcNow, Guid.NewGuid())
        }));

        Assert.Contains("Skip and After", error.Message, StringComparison.Ordinal);

        // Nothing reached the store: a read that paged from the wrong place would be wrong quietly.
        Assert.Null(store.LastQuery);
    }

    [Fact]
    public async Task A_cursor_with_skip_zero_is_not_a_contradiction()
    {
        var (reader, store) = Build();

        // Skip defaults to zero, so requiring it to be unset would make After unusable with any
        // query built by `with`.
        await reader.QueryAsync(new AuditQuery { Skip = 0, After = new AuditCursor(DateTime.UtcNow, Guid.NewGuid()) });

        Assert.NotNull(store.LastQuery!.After);
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
