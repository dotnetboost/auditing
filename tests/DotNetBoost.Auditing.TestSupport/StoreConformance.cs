using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using System.Globalization;
using Xunit;

namespace DotNetBoost.Auditing.TestSupport;

/// <summary>
/// The <see cref="IAuditStore"/> contract, run against every store on a real engine.
/// </summary>
/// <remarks>
/// One suite, inherited once per store, because the contract is what the engine and the reader rely
/// on: a filter that means something slightly different in one store, or a delete that leaves
/// orphans in another, is invisible until an application moves between them.
/// <para>
/// It lives in TestSupport rather than in one test project so that <b>both levels</b> run it:
/// <c>ProviderTests</c> derives from it for the in-memory double and the two relational stores over
/// SQLite, needing nothing installed, and <c>IntegrationTests</c> derives from it again for
/// PostgreSQL, SQL Server and MongoDB in containers. The double running it is what makes the unit
/// suites meaningful — a test built on a double is only worth anything if the double behaves like
/// the real thing.
/// </para>
/// <para>
/// <b><see cref="FlushAsync"/> is where the stores legitimately differ.</b> The EF Core store only
/// tracks an appended entry — the caller's <c>SaveChanges</c> commits it — while the Dapper and
/// MongoDB stores write when called. That difference is documented behaviour rather than a bug, so
/// it is a hook here instead of being hidden: every test appends and then flushes, and each subclass
/// says what flushing means for it.
/// </para>
/// </remarks>
public abstract class StoreConformance : IAsyncLifetime
{
    /// <summary>Timestamps far enough apart to order unambiguously on every engine.</summary>
    private static readonly DateTime Base = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    protected IAuditStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await PrepareAsync();
        Store = await OpenAsync();
    }

    public virtual Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Creates the schema if it needs creating, and leaves it empty.</summary>
    protected abstract Task PrepareAsync();

    /// <summary>A store over a connection, context or database of this test's own.</summary>
    protected abstract Task<IAuditStore> OpenAsync();

    /// <summary>What a caller must do after appending for the entry to be durable.</summary>
    protected abstract Task FlushAsync();

    /// <summary>
    /// How many property-change rows exist, however this store keeps them. The orphan check: the
    /// relational stores delete them explicitly, and MongoDB has them inside the entry.
    /// </summary>
    protected abstract Task<long> CountChangesAsync();

    private static AuditEntry Entry(
        string entityId = "42",
        string entityName = "Product",
        AuditAction action = AuditAction.Updated,
        string? actor = "alice@example.com",
        string correlationId = "edit-1",
        DateTime? occurredAt = null,
        string? metadata = null) =>
        new()
        {
            Id            = Guid.NewGuid(),
            EntityName    = entityName,
            EntityId      = entityId,
            Action        = action,
            Actor         = actor,
            CorrelationId = correlationId,
            OccurredAt    = occurredAt ?? Base,
            Metadata      = metadata
        };

    private static AuditPropertyChange Change(
        string name = "Price",
        string? oldValue = "24.99",
        string? newValue = "19.99",
        bool redacted = false,
        bool truncated = false) =>
        new()
        {
            PropertyName = name,
            OldValue     = oldValue,
            NewValue     = newValue,
            IsRedacted   = redacted,
            IsTruncated  = truncated
        };

    private async Task<AuditEntry> AppendAsync(AuditEntry entry, params AuditPropertyChange[] changes)
    {
        foreach (var change in changes)
        {
            change.AuditEntryId = entry.Id;
            entry.Changes.Add(change);
        }

        await Store.AppendAsync(entry);
        await FlushAsync();
        return entry;
    }

    // ── writing and reading back ────────────────────────────────────────────

    [Fact]
    public async Task An_entry_reads_back_with_everything_it_was_written_with()
    {
        var written = await AppendAsync(
            Entry(metadata: """{"ticket":"OPS-1"}"""),
            Change(),
            Change("Name", "Widget", "Gadget"));

        var read = await Store.GetAsync(written.Id);

        Assert.NotNull(read);
        Assert.Equal(written.Id, read.Id);
        Assert.Equal("Product", read.EntityName);
        Assert.Equal("42", read.EntityId);
        Assert.Equal(AuditAction.Updated, read.Action);
        Assert.Equal("alice@example.com", read.Actor);
        Assert.Equal("edit-1", read.CorrelationId);
        Assert.Equal("""{"ticket":"OPS-1"}""", read.Metadata);
        Assert.Equal(2, read.Changes.Count);

        var price = Assert.Single(read.Changes, c => c.PropertyName == "Price");
        Assert.Equal("24.99", price.OldValue);
        Assert.Equal("19.99", price.NewValue);
        Assert.Equal(written.Id, price.AuditEntryId);
    }

    [Fact]
    public async Task A_timestamp_comes_back_as_UTC_and_the_same_instant()
    {
        var written = await AppendAsync(Entry(), Change());

        var read = await Store.GetAsync(written.Id);

        Assert.Equal(DateTimeKind.Utc, read!.OccurredAt.Kind);

        // To the millisecond rather than the tick: BSON dates hold milliseconds, so the MongoDB
        // store is coarser than the relational ones by design. That is the documented difference,
        // and this is the assertion all three can honestly pass.
        Assert.Equal(Base, read.OccurredAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task An_uncaptured_actor_reads_back_as_null_rather_than_as_anything_else()
    {
        var written = await AppendAsync(Entry(actor: null), Change());

        Assert.Null((await Store.GetAsync(written.Id))!.Actor);
    }

    [Fact]
    public async Task The_redaction_and_truncation_flags_survive_the_round_trip()
    {
        var written = await AppendAsync(
            Entry(),
            Change("PasswordHash", null, null, redacted: true),
            Change("Notes", "a", "b", truncated: true));

        var read = await Store.GetAsync(written.Id);

        var redacted = Assert.Single(read!.Changes, c => c.PropertyName == "PasswordHash");
        Assert.True(redacted.IsRedacted);
        Assert.Null(redacted.OldValue);
        Assert.Null(redacted.NewValue);

        Assert.True(Assert.Single(read.Changes, c => c.PropertyName == "Notes").IsTruncated);
    }

    [Fact]
    public async Task An_entry_with_no_changes_is_legal()
    {
        var written = await AppendAsync(Entry(action: AuditAction.Deleted));

        var read = await Store.GetAsync(written.Id);

        // A deletion recorded without its values says the entity was deleted and nothing more.
        Assert.NotNull(read);
        Assert.Empty(read.Changes);
    }

    [Fact]
    public async Task Several_entries_can_be_appended_at_once()
    {
        var entries = new[]
        {
            Entry("1", occurredAt: Base),
            Entry("2", occurredAt: Base.AddMinutes(1)),
            Entry("3", occurredAt: Base.AddMinutes(2))
        };

        foreach (var entry in entries)
            entry.Changes.Add(Change());

        await Store.AppendRangeAsync(entries);
        await FlushAsync();

        var page = await Store.QueryAsync(new AuditQuery { Take = 10 });

        Assert.Equal(3, page.Entries.Count);
        Assert.Equal(3, await CountChangesAsync());
    }

    [Fact]
    public async Task An_unknown_id_reads_back_as_null() =>
        Assert.Null(await Store.GetAsync(Guid.NewGuid()));

    // ── querying ───────────────────────────────────────────────────────────

    private async Task SeedAsync()
    {
        await AppendAsync(Entry("1", action: AuditAction.Created, actor: "alice", correlationId: "c1", occurredAt: Base), Change());
        await AppendAsync(Entry("2", action: AuditAction.Updated, actor: "bob", correlationId: "c2", occurredAt: Base.AddHours(1)), Change());
        await AppendAsync(Entry("3", action: AuditAction.Deleted, actor: null, correlationId: "c3", occurredAt: Base.AddHours(2)), Change());
        await AppendAsync(Entry("9", entityName: "Order", actor: "alice", correlationId: "c4", occurredAt: Base.AddHours(3)), Change());
    }

    // ── paging by cursor ───────────────────────────────────────────────────

    [Fact]
    public async Task A_cursor_reads_the_page_after_the_one_it_points_at()
    {
        await SeedAsync();

        var first = await Store.QueryAsync(new AuditQuery { Take = 2 });
        Assert.Equal(2, first.Entries.Count);

        var second = await Store.QueryAsync(new AuditQuery
        {
            Take  = 2,
            After = AuditCursor.Of(first.Entries[^1])
        });

        Assert.Equal(2, second.Entries.Count);

        // No overlap and no gap: four entries seen exactly once across the two pages.
        var seen = first.Entries.Concat(second.Entries).Select(e => e.Id).ToList();
        Assert.Equal(4, seen.Distinct().Count());

        // And the walk ends rather than repeating the last page for ever.
        var third = await Store.QueryAsync(new AuditQuery
        {
            Take  = 2,
            After = AuditCursor.Of(second.Entries[^1])
        });

        Assert.Empty(third.Entries);
    }

    [Fact]
    public async Task A_cursor_steps_through_entries_that_share_a_timestamp()
    {
        // All four at the same instant, so only the id tiebreaker separates them. This is the case
        // a timestamp-only cursor gets wrong — it would return the whole group again, or skip it.
        for (var i = 0; i < 4; i++)
            await AppendAsync(Entry(i.ToString(CultureInfo.InvariantCulture), occurredAt: Base), Change());

        var seen   = new List<Guid>();
        AuditCursor? cursor = null;

        for (var page = 0; page < 5; page++)
        {
            var read = await Store.QueryAsync(new AuditQuery { Take = 1, After = cursor });

            if (read.Entries.Count == 0)
                break;

            seen.Add(read.Entries[0].Id);
            cursor = AuditCursor.Of(read.Entries[^1]);
        }

        // Each one once. The order within the group is the engine's own id ordering, which differs
        // between engines, so what is asserted is the set rather than the sequence.
        Assert.Equal(4, seen.Count);
        Assert.Equal(4, seen.Distinct().Count());
    }

    [Fact]
    public async Task A_cursor_combines_with_the_filters()
    {
        await SeedAsync();

        var query = new AuditQuery { EntityName = "Product", Take = 1 };

        var first = await Store.QueryAsync(query);
        Assert.Equal("Product", Assert.Single(first.Entries).EntityName);

        var rest = await Store.QueryAsync(query with { After = AuditCursor.Of(first.Entries[^1]), Take = 10 });

        // The filter still applies past the cursor: two Products left and never the Order, which
        // sorts between them by timestamp.
        Assert.Equal(2, rest.Entries.Count);
        Assert.All(rest.Entries, e => Assert.Equal("Product", e.EntityName));
    }

    [Fact]
    public async Task A_cursor_past_the_end_reads_nothing()
    {
        await SeedAsync();

        var page = await Store.QueryAsync(new AuditQuery
        {
            Take  = 10,
            // Older than anything seeded, and the lowest possible id, so nothing sorts after it.
            After = new AuditCursor(Base.AddYears(-1), Guid.Empty)
        });

        Assert.Empty(page.Entries);
    }

    [Fact]
    public async Task A_query_filters_by_entity_name_and_key()
    {
        await SeedAsync();

        var byName = await Store.QueryAsync(new AuditQuery { EntityName = "Order", Take = 10 });
        Assert.Equal("9", Assert.Single(byName.Entries).EntityId);

        var byAddress = await Store.QueryAsync(new AuditQuery { EntityName = "Product", EntityId = "2", Take = 10 });
        Assert.Equal("2", Assert.Single(byAddress.Entries).EntityId);
    }

    [Fact]
    public async Task A_query_filters_by_action()
    {
        await SeedAsync();

        var deleted = await Store.QueryAsync(new AuditQuery { Action = AuditAction.Deleted, Take = 10 });

        Assert.Equal(AuditAction.Deleted, Assert.Single(deleted.Entries).Action);
    }

    [Fact]
    public async Task A_query_tells_an_actor_filter_from_a_missing_actor()
    {
        await SeedAsync();

        var alice = await Store.QueryAsync(new AuditQuery { Actor = "alice", Take = 10 });
        Assert.Equal(2, alice.Entries.Count);

        var nobody = await Store.QueryAsync(new AuditQuery { ActorMissing = true, Take = 10 });
        Assert.Null(Assert.Single(nobody.Entries).Actor);
    }

    [Fact]
    public async Task A_query_filters_by_correlation_id()
    {
        await SeedAsync();

        var one = await Store.QueryAsync(new AuditQuery { CorrelationId = "c2", Take = 10 });

        Assert.Equal("2", Assert.Single(one.Entries).EntityId);
    }

    [Fact]
    public async Task A_window_includes_its_start_and_excludes_its_end()
    {
        await SeedAsync();

        var window = await Store.QueryAsync(new AuditQuery
        {
            From = Base.AddHours(1),
            To   = Base.AddHours(3),
            Take = 10
        });

        // Two of the four: the one at +1h (inclusive start) and the one at +2h. The +3h entry is
        // excluded, so consecutive windows do not double-count it.
        Assert.Equal(2, window.Entries.Count);
        Assert.DoesNotContain(window.Entries, e => e.EntityId == "9");
        Assert.DoesNotContain(window.Entries, e => e.EntityId == "1");
    }

    [Fact]
    public async Task A_page_comes_back_newest_first()
    {
        await SeedAsync();

        var page = await Store.QueryAsync(new AuditQuery { Take = 10 });

        var timestamps = page.Entries.Select(e => e.OccurredAt).ToList();
        Assert.Equal(timestamps.OrderByDescending(t => t), timestamps);
    }

    [Fact]
    public async Task Paging_skips_and_takes()
    {
        await SeedAsync();

        var first  = await Store.QueryAsync(new AuditQuery { Take = 2 });
        var second = await Store.QueryAsync(new AuditQuery { Skip = 2, Take = 2 });

        Assert.Equal(2, first.Entries.Count);
        Assert.Equal(2, second.Entries.Count);
        Assert.Empty(first.Entries.Select(e => e.Id).Intersect(second.Entries.Select(e => e.Id)));
    }

    [Fact]
    public async Task Paging_is_stable_when_every_entry_shares_a_timestamp()
    {
        for (var i = 0; i < 6; i++)
            await AppendAsync(Entry(i.ToString(CultureInfo.InvariantCulture), occurredAt: Base), Change());

        var first  = await Store.QueryAsync(new AuditQuery { Take = 3 });
        var second = await Store.QueryAsync(new AuditQuery { Skip = 3, Take = 3 });

        // The id tiebreaker in every store's ORDER BY. Without it, two entries in the same tick can
        // swap places between pages — showing one twice and skipping another.
        var seen = first.Entries.Concat(second.Entries).Select(e => e.Id).ToList();
        Assert.Equal(6, seen.Distinct().Count());
    }

    [Fact]
    public async Task A_total_counts_every_match_rather_than_the_page()
    {
        await SeedAsync();

        var page = await Store.QueryAsync(new AuditQuery { Take = 2, IncludeTotal = true });

        Assert.Equal(2, page.Entries.Count);
        Assert.Equal(4, page.Total);
    }

    [Fact]
    public async Task A_total_is_null_when_it_was_not_asked_for()
    {
        await SeedAsync();

        Assert.Null((await Store.QueryAsync(new AuditQuery { Take = 2 })).Total);
    }

    [Fact]
    public async Task A_page_can_leave_the_changes_out()
    {
        await SeedAsync();

        var page = await Store.QueryAsync(new AuditQuery { Take = 10, IncludeChanges = false });

        Assert.All(page.Entries, e => Assert.Empty(e.Changes));
    }

    [Fact]
    public async Task A_page_carries_the_changes_by_default()
    {
        await SeedAsync();

        var page = await Store.QueryAsync(new AuditQuery { Take = 10 });

        Assert.All(page.Entries, e => Assert.NotEmpty(e.Changes));
    }

    // ── deleting ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Deleting_by_age_takes_only_what_is_older_than_the_cutoff()
    {
        await SeedAsync();

        var deleted = await Store.DeleteOlderThanAsync(Base.AddHours(2), AuditPruneScope.All, 100);

        Assert.Equal(2, deleted);

        var left = await Store.QueryAsync(new AuditQuery { Take = 10 });
        Assert.Equal(2, left.Entries.Count);
    }

    [Fact]
    public async Task Deleting_by_age_can_be_scoped_to_one_entity()
    {
        await SeedAsync();

        var deleted = await Store.DeleteOlderThanAsync(
            Base.AddHours(10), AuditPruneScope.For("Order"), 100);

        Assert.Equal(1, deleted);

        var left = await Store.QueryAsync(new AuditQuery { Take = 10 });
        Assert.Equal(3, left.Entries.Count);
        Assert.DoesNotContain(left.Entries, e => e.EntityName == "Order");
    }

    [Fact]
    public async Task Deleting_by_age_can_exclude_the_entities_with_a_retention_of_their_own()
    {
        await SeedAsync();

        var deleted = await Store.DeleteOlderThanAsync(
            Base.AddHours(10), AuditPruneScope.Excluding(["Order"]), 100);

        // The whole reason AuditPruneScope exists: without the exclusion a global sweep would take
        // the entities whose own retention is longer, making [AuditRetention] silently the opposite
        // of what it says.
        Assert.Equal(3, deleted);
        Assert.Equal("Order", Assert.Single((await Store.QueryAsync(new AuditQuery { Take = 10 })).Entries).EntityName);
    }

    [Fact]
    public async Task Deleting_by_age_is_batched()
    {
        await SeedAsync();

        var first = await Store.DeleteOlderThanAsync(Base.AddHours(10), AuditPruneScope.All, 2);
        Assert.Equal(2, first);

        var second = await Store.DeleteOlderThanAsync(Base.AddHours(10), AuditPruneScope.All, 2);
        Assert.Equal(2, second);

        // Short of the batch size is how the pruner knows the backlog is clear.
        Assert.Equal(0, await Store.DeleteOlderThanAsync(Base.AddHours(10), AuditPruneScope.All, 2));
        Assert.Empty((await Store.QueryAsync(new AuditQuery { Take = 10 })).Entries);
    }

    [Fact]
    public async Task Deleting_the_oldest_batch_takes_the_oldest_entries()
    {
        await SeedAsync();

        await Store.DeleteOlderThanAsync(Base.AddHours(10), AuditPruneScope.All, 2);

        var left = await Store.QueryAsync(new AuditQuery { Take = 10 });

        // Oldest first, so a prune that cannot finish in one batch has still made progress from the
        // right end.
        Assert.All(left.Entries, e => Assert.True(e.OccurredAt >= Base.AddHours(2).AddMilliseconds(-1)));
    }

    [Fact]
    public async Task A_batch_too_large_for_one_statement_is_still_one_batch()
    {
        // Past SQL Server's 2,100-parameter ceiling, which a store that deletes by a list of ids
        // runs into. A batch size is the caller's to choose, so what is asserted here is that the
        // whole of it goes in this one call and is reported: returning less would tell the pruner
        // the backlog was clear and leave entries sitting past their retention.
        const int count = 2_500;

        var entries = new List<AuditEntry>(count);

        for (var i = 0; i < count; i++)
        {
            var entry = Entry(entityId: i.ToString(CultureInfo.InvariantCulture), occurredAt: Base);

            var change = Change();
            change.AuditEntryId = entry.Id;
            entry.Changes.Add(change);

            entries.Add(entry);
        }

        await Store.AppendRangeAsync(entries);
        await FlushAsync();

        Assert.Equal(count, await Store.DeleteOlderThanAsync(Base.AddHours(1), AuditPruneScope.All, count));

        Assert.Empty((await Store.QueryAsync(new AuditQuery { Take = 10 })).Entries);
        Assert.Equal(0, await CountChangesAsync());
    }

    [Fact]
    public async Task Erasing_one_entity_leaves_the_others()
    {
        await SeedAsync();

        Assert.Equal(1, await Store.DeleteForEntityAsync("Product", "2"));

        var left = await Store.QueryAsync(new AuditQuery { Take = 10 });
        Assert.Equal(3, left.Entries.Count);
        Assert.DoesNotContain(left.Entries, e => e is { EntityName: "Product", EntityId: "2" });
    }

    [Fact]
    public async Task Erasing_one_actor_leaves_the_others()
    {
        await SeedAsync();

        Assert.Equal(2, await Store.DeleteForActorAsync("alice"));

        var left = await Store.QueryAsync(new AuditQuery { Take = 10 });
        Assert.Equal(2, left.Entries.Count);
        Assert.DoesNotContain(left.Entries, e => e.Actor == "alice");
    }

    [Fact]
    public async Task Erasing_a_trail_larger_than_one_batch_removes_all_of_it()
    {
        // Past the point where a store that deletes by a list of ids has to go round more than
        // once, and past SQL Server's 2,100-parameter ceiling — which is what an unbatched erasure
        // used to hit, as a throw rather than a slow delete. Erasure takes no batch size, so
        // "however many there are" is the contract being asserted here.
        const int count = 2_500;

        var entries = new List<AuditEntry>(count);

        for (var i = 0; i < count; i++)
        {
            var entry = Entry(
                entityId: i.ToString(CultureInfo.InvariantCulture),
                actor: "leaver@example.com",
                correlationId: "bulk",
                occurredAt: Base.AddSeconds(i));

            var change = Change();
            change.AuditEntryId = entry.Id;
            entry.Changes.Add(change);

            entries.Add(entry);
        }

        await Store.AppendRangeAsync(entries);
        await FlushAsync();

        Assert.Equal(count, await Store.DeleteForActorAsync("leaver@example.com"));

        Assert.Empty((await Store.QueryAsync(new AuditQuery { Take = 10 })).Entries);

        // And the property rows went with them. An orphan at this scale is the same bug as an
        // orphan at three, but it is only reachable once the delete has to loop.
        Assert.Equal(0, await CountChangesAsync());
    }

    [Fact]
    public async Task Erasing_a_trail_that_is_not_there_deletes_nothing()
    {
        await SeedAsync();

        Assert.Equal(0, await Store.DeleteForEntityAsync("Product", "999"));
        Assert.Equal(4, (await Store.QueryAsync(new AuditQuery { Take = 10 })).Entries.Count);
    }

    [Fact]
    public async Task Deleting_an_entry_leaves_no_property_rows_behind()
    {
        await SeedAsync();

        Assert.Equal(4, await CountChangesAsync());

        await Store.DeleteForEntityAsync("Product", "2");
        Assert.Equal(3, await CountChangesAsync());

        await Store.DeleteOlderThanAsync(Base.AddHours(10), AuditPruneScope.All, 100);

        // The relational stores delete the child rows explicitly rather than trusting a cascade;
        // MongoDB has them inside the entry. Either way an orphan would be invisible forever, so
        // the count has to reach zero.
        Assert.Equal(0, await CountChangesAsync());
    }
}
