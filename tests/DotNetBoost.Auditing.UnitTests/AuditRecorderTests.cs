using DotNetBoost.Auditing.Core;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Metadata;
using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.Core.Services;
using DotNetBoost.Auditing.TestSupport;
using System.Diagnostics;

namespace DotNetBoost.Auditing.UnitTests;

public class AuditRecorderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 9, 30, 0, TimeSpan.Zero);

    // The subject is bound to the manager, so these exercise the public surface rather than the
    // recording engine directly. The engines underneath are the same ones AddAuditing registers.
    private static (IAuditManager Recorder, RecordingAuditStore Store) Build(
        Action<AuditingOptions>? configure = null,
        string? actor = null,
        AuditModelRegistry? registry = null)
    {
        var (manager, store, _) = Observed(configure, actor, registry);
        return (manager, store);
    }

    /// <summary>
    /// As <see cref="Build"/>, and with the observer's notebook — for the tests that assert on what
    /// was <i>not</i> recorded, which is the only way that is visible from outside.
    /// </summary>
    private static (IAuditManager Recorder, RecordingAuditStore Store, RecordingAuditObserver Observer) Observed(
        Action<AuditingOptions>? configure = null,
        string? actor = null,
        AuditModelRegistry? registry = null)
    {
        var options = new AuditingOptions();
        configure?.Invoke(options);

        var store    = new RecordingAuditStore();
        var keys     = new ConventionEntityKeyAccessor();
        var time     = new FixedTimeProvider(Now);
        var model    = registry ?? AuditModelRegistry.Empty;
        var observer = new RecordingAuditObserver();

        var manager = new AuditManager(
            new AuditRecorder(store, new FixedActorAccessor(actor), keys, model, options, time, observer),
            new AuditReader(store, keys, options),
            new AuditPruner(store, keys, model, options, time));

        return (manager, store, observer);
    }

    [Fact]
    public async Task A_recorded_change_carries_the_address_the_action_and_the_clock()
    {
        var (recorder, store) = Build();

        var entry = await recorder.For(new Product { Id = 42 }).UpdatedAsync(
        [
            new AuditChange(nameof(Product.Price), 24.99m, 19.99m)
        ]);

        Assert.NotNull(entry);
        Assert.Equal("Product", entry.EntityName);
        Assert.Equal("42", entry.EntityId);
        Assert.Equal(AuditAction.Updated, entry.Action);
        Assert.Equal(Now.UtcDateTime, entry.OccurredAt);

        // By id, not by reference. The double copies on the way in because a database does, so the
        // returned entry and the stored one are equal and not identical — and a test that relied on
        // identity was relying on the double being unrealistic.
        Assert.Equal(entry.Id, Assert.Single(store.Entries).Id);

        var change = Assert.Single(entry.Changes);
        Assert.Equal("24.99", change.OldValue);
        Assert.Equal("19.99", change.NewValue);
        Assert.Equal(entry.Id, change.AuditEntryId);
    }

    [Fact]
    public async Task A_redacted_property_is_recorded_as_changed_with_both_values_withheld()
    {
        var (recorder, _) = Build();

        var entry = await recorder.For(new PersonEntity { Id = Guid.NewGuid() }).UpdatedAsync(
        [
            new AuditChange(nameof(PersonEntity.PasswordHash), "old-hash", "new-hash")
        ]);

        var change = Assert.Single(entry!.Changes);
        Assert.True(change.IsRedacted);
        Assert.Null(change.OldValue);
        Assert.Null(change.NewValue);
        Assert.Equal(nameof(PersonEntity.PasswordHash), change.PropertyName);
    }

    [Fact]
    public async Task An_ignored_property_is_dropped_entirely()
    {
        var (recorder, _) = Build();

        var entry = await recorder.For(new PersonEntity { Id = Guid.NewGuid() }).UpdatedAsync(
        [
            new AuditChange(nameof(PersonEntity.LastSeenAt), Now.UtcDateTime, Now.UtcDateTime.AddHours(1)),
            new AuditChange(nameof(PersonEntity.Email), "a@example.com", "b@example.com")
        ]);

        var change = Assert.Single(entry!.Changes);
        Assert.Equal(nameof(PersonEntity.Email), change.PropertyName);
    }

    [Fact]
    public async Task An_update_whose_every_property_was_ignored_records_nothing_at_all()
    {
        var (recorder, store) = Build();

        var entry = await recorder.For(new PersonEntity { Id = Guid.NewGuid() }).UpdatedAsync(
        [
            new AuditChange(nameof(PersonEntity.LastSeenAt), 1, 2)
        ]);

        // Otherwise an entity whose only changing property is [AuditIgnore] produces an endless
        // trail of entries that say a change happened and never which.
        Assert.Null(entry);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task An_update_with_no_changes_records_nothing()
    {
        var (recorder, store) = Build();

        Assert.Null(await recorder.For(new Product { Id = 1 }).UpdatedAsync([]));
        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task An_ignored_entity_records_nothing()
    {
        var (recorder, store) = Build();

        Assert.Null(await recorder.For(new Heartbeat { Id = 7 }).UpdatedAsync(
        [
            new AuditChange("Id", 6, 7)
        ]));

        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task A_creation_drops_the_old_values_whatever_the_caller_reported()
    {
        var (recorder, _) = Build();

        var entry = await recorder.For(new Product { Id = 1 }).CreatedAsync(
        [
            new AuditChange(nameof(Product.Name), "should not be here", "Widget")
        ]);

        var change = Assert.Single(entry!.Changes);
        Assert.Null(change.OldValue);
        Assert.Equal("Widget", change.NewValue);
    }

    [Fact]
    public async Task A_deletion_drops_the_new_values_whatever_the_caller_reported()
    {
        var (recorder, _) = Build();

        var entry = await recorder.For(new Product { Id = 1 }).DeletedAsync(
        [
            new AuditChange(nameof(Product.Name), "Widget", "should not be here")
        ]);

        var change = Assert.Single(entry!.Changes);
        Assert.Equal("Widget", change.OldValue);
        Assert.Null(change.NewValue);
    }

    [Fact]
    public async Task A_deletion_still_records_an_entry_when_values_are_turned_off()
    {
        var (recorder, _) = Build(o => o.RecordDeletedValues = false);

        var entry = await recorder.For(new Product { Id = 1 }).DeletedAsync(
        [
            new AuditChange(nameof(Product.Name), "Widget", null)
        ]);

        // "It was deleted, and nothing more" is still a fact worth keeping — unlike an update of
        // nothing, which is not.
        Assert.NotNull(entry);
        Assert.Empty(entry.Changes);
    }

    [Fact]
    public async Task Recording_off_records_nothing_and_does_not_throw()
    {
        var (recorder, store) = Build(o => o.Enabled = false);

        Assert.Null(await recorder.For(new Product { Id = 1 }).UpdatedAsync(
        [
            new AuditChange(nameof(Product.Price), 1m, 2m)
        ]));

        Assert.Empty(store.Entries);
        Assert.False(recorder.IsAudited(typeof(Product)));
    }

    [Fact]
    public async Task Opt_in_records_only_the_marked_entities()
    {
        var (recorder, _) = Build(o => o.RequireOptIn = true);

        Assert.Null(await recorder.For(new Product { Id = 1 }).UpdatedAsync(
        [
            new AuditChange(nameof(Product.Price), 1m, 2m)
        ]));

        Assert.NotNull(await recorder.For(new Invoice { Id = 1 }).UpdatedAsync(
        [
            new AuditChange(nameof(Invoice.Total), 1m, 2m)
        ]));

        Assert.False(recorder.IsAudited(typeof(Product)));
        Assert.True(recorder.IsAudited(typeof(Invoice)));
    }

    [Fact]
    public async Task Opt_in_refuses_a_write_addressed_by_name_that_nothing_can_be_proven_about()
    {
        var (recorder, _) = Build(o => o.RequireOptIn = true);

        // An allow-list with a back door is not an allow-list.
        Assert.Null(await recorder.For("Unknown", "1").UpdatedAsync(
        [
            new AuditChange("Anything", 1, 2)
        ]));
    }

    [Fact]
    public async Task A_write_addressed_by_name_gets_the_models_rules_when_the_entity_is_declared()
    {
        var registry = new AuditModelRegistry(new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["Person"] = typeof(PersonEntity)
        });

        var (recorder, _) = Build(registry: registry);

        var entry = await recorder.For("Person", "42").UpdatedAsync(
        [
            new AuditChange(nameof(PersonEntity.PasswordHash), "old", "new")
        ]);

        // This is the case that matters: a Dapper application addresses everything by name, and
        // without the declaration the hash would have been written down in full.
        Assert.True(Assert.Single(entry!.Changes).IsRedacted);
    }

    [Fact]
    public async Task A_write_addressed_by_name_with_nothing_declared_records_the_values_as_given()
    {
        var (recorder, _) = Build();

        var entry = await recorder.For("Person", "42").UpdatedAsync(
        [
            new AuditChange(nameof(PersonEntity.PasswordHash), "old", "new")
        ]);

        // The documented price of addressing by hand. Declare your entities.
        Assert.False(Assert.Single(entry!.Changes).IsRedacted);
    }

    [Fact]
    public async Task The_actor_comes_from_the_accessor_and_null_stays_null()
    {
        var (named, _) = Build(actor: "alice@example.com");
        var (anonymous, _) = Build();

        var withActor = await named.For(new Product { Id = 1 }).CreatedAsync();
        var without   = await anonymous.For(new Product { Id = 1 }).CreatedAsync();

        Assert.Equal("alice@example.com", withActor!.Actor);
        // Never "system": a placeholder would turn "nobody captured this" into a claim.
        Assert.Null(without!.Actor);
    }

    [Fact]
    public async Task A_caller_can_name_the_actor_itself()
    {
        var (recorder, _) = Build(actor: "ambient");

        var entry = await recorder.RecordAsync(new AuditWrite
        {
            EntityName = "Product",
            EntityId   = "1",
            Action     = AuditAction.Updated,
            Actor      = "import:prices.csv",
            Changes    = [new AuditChange(nameof(Product.Price), 1m, 2m)]
        });

        Assert.Equal("import:prices.csv", entry!.Actor);
    }

    [Fact]
    public async Task An_over_long_actor_is_refused_rather_than_cut()
    {
        var (recorder, _) = Build(actor: new string('a', AuditColumns.ActorMaxLength + 1));

        // Half an identity is a different identity.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => recorder.For(new Product { Id = 1 }).CreatedAsync());
    }

    [Fact]
    public async Task The_correlation_id_is_the_ambient_activity_when_there_is_one()
    {
        var (recorder, _) = Build();

        using var activity = new Activity("test").Start();

        var first  = await recorder.For(new Product { Id = 1 }).CreatedAsync();
        var second = await recorder.For(new Product { Id = 2 }).CreatedAsync();

        Assert.Equal(activity.Id, first!.CorrelationId);
        // The point of it: the several entries of one unit of work are identifiable as one edit.
        Assert.Equal(first.CorrelationId, second!.CorrelationId);
    }

    [Fact]
    public async Task A_supplied_correlation_id_wins_over_the_ambient_activity()
    {
        var (recorder, _) = Build();

        using var activity = new Activity("test").Start();

        var entry = await recorder.RecordAsync(new AuditWrite
        {
            EntityName    = "Product",
            EntityId      = "1",
            Action        = AuditAction.Created,
            CorrelationId = "save-7"
        });

        // A capture source that knows where one edit ends beats the ambient activity, which spans
        // every save a whole request makes.
        Assert.Equal("save-7", entry!.CorrelationId);
    }

    [Fact]
    public async Task An_over_long_correlation_id_is_cut_rather_than_refused()
    {
        var (recorder, _) = Build();

        var entry = await recorder.RecordAsync(new AuditWrite
        {
            EntityName    = "Product",
            EntityId      = "1",
            Action        = AuditAction.Created,
            CorrelationId = new string('c', AuditColumns.CorrelationIdMaxLength + 50)
        });

        // Unlike the actor: a correlation id is a diagnostic aid, and failing a business write over
        // one would be the library breaking the application over its own bookkeeping.
        Assert.Equal(AuditColumns.CorrelationIdMaxLength, entry!.CorrelationId.Length);
    }

    [Fact]
    public async Task Without_an_activity_each_entry_gets_its_own_correlation_id()
    {
        Activity.Current = null;
        var (recorder, _) = Build();

        var first  = await recorder.For(new Product { Id = 1 }).CreatedAsync();
        var second = await recorder.For(new Product { Id = 2 }).CreatedAsync();

        Assert.NotEqual(first!.CorrelationId, second!.CorrelationId);
        Assert.NotEmpty(first.CorrelationId);
    }

    [Fact]
    public void An_unsaved_entity_cannot_be_addressed()
    {
        var (recorder, _) = Build();

        var error = Assert.Throws<InvalidOperationException>(() => recorder.For(new Product { Id = 0 }));
        Assert.Contains("no key assigned", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_key_is_refused_with_the_workaround_in_the_message()
    {
        var (recorder, _) = Build();

        var error = Assert.Throws<NotSupportedException>(() => recorder.For(new CompositeKeyed()));
        Assert.Contains("audit.For(name, id)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entity_with_no_discoverable_key_is_refused()
    {
        var (recorder, _) = Build();

        Assert.Throws<InvalidOperationException>(() => recorder.For(new Unkeyed()));
    }

    [Fact]
    public async Task An_oversized_value_is_truncated_and_says_so()
    {
        var (recorder, _) = Build(o => o.MaxValueLength = 16);

        var entry = await recorder.For(new Product { Id = 1 }).UpdatedAsync(
        [
            new AuditChange(nameof(Product.Name), "short", new string('x', 100))
        ]);

        var change = Assert.Single(entry!.Changes);
        Assert.True(change.IsTruncated);
        Assert.Equal(16, change.NewValue!.Length);
        Assert.Equal("short", change.OldValue);
    }

    // ── what the engine decided NOT to record ──────────────────────────────
    //
    // A null return says "deliberately not recorded" to a caller who checks it, and the EF Core
    // interceptor has no caller to tell. The observer is the only way a suppression is visible from
    // outside the recorder, so each reason gets a test: a suppression that stopped reporting would
    // otherwise be a silent regression, which is the exact failure the interface was added for.

    [Fact]
    public async Task The_kill_switch_reports_why_nothing_was_recorded()
    {
        var (recorder, _, observer) = Observed(o => o.Enabled = false);

        Assert.Null(await recorder.For(new Product { Id = 7 }).UpdatedAsync(
        [
            new AuditChange(nameof(Product.Price), 1m, 2m)
        ]));

        var report = Assert.Single(observer.Suppressed);
        Assert.Equal(AuditSuppression.RecordingDisabled, report.Reason);
        Assert.Equal("Product", report.EntityName);
        Assert.Equal("7", report.EntityId);
        Assert.Equal(AuditAction.Updated, report.Action);
    }

    [Fact]
    public async Task An_ignored_entity_reports_that_it_was_ignored()
    {
        var (recorder, _, observer) = Observed();

        Assert.Null(await recorder.For(new Heartbeat { Id = 1 }).UpdatedAsync(
        [
            new AuditChange(nameof(Heartbeat.Id), 1, 1)
        ]));

        Assert.Equal(AuditSuppression.EntityIgnored, Assert.Single(observer.Suppressed).Reason);
    }

    [Fact]
    public async Task Opt_in_reports_an_entity_that_is_not_marked()
    {
        var (recorder, _, observer) = Observed(o => o.RequireOptIn = true);

        Assert.Null(await recorder.For(new Product { Id = 1 }).UpdatedAsync(
        [
            new AuditChange(nameof(Product.Price), 1m, 2m)
        ]));

        Assert.Equal(AuditSuppression.NotOptedIn, Assert.Single(observer.Suppressed).Reason);

        // And the entity that IS marked is recorded, reporting nothing.
        Assert.NotNull(await recorder.For(new Invoice { Id = 1 }).UpdatedAsync(
        [
            new AuditChange(nameof(Invoice.Total), 1m, 2m)
        ]));

        Assert.Single(observer.Suppressed);
    }

    [Fact]
    public async Task Opt_in_distinguishes_an_undeclared_entity_from_an_unmarked_one()
    {
        var (recorder, _, observer) = Observed(o => o.RequireOptIn = true);

        Assert.Null(await recorder.For("Unknown", "1").UpdatedAsync(
        [
            new AuditChange("Anything", 1, 2)
        ]));

        // Its own reason, not NotOptedIn: nothing is declared under that name, so this is a missing
        // DeclareEntitiesFrom rather than a decision — which is why the logging observer warns on
        // this one and not on the other.
        Assert.Equal(AuditSuppression.EntityNotDeclared, Assert.Single(observer.Suppressed).Reason);
    }

    [Fact]
    public async Task An_update_with_nothing_left_to_say_reports_that_too()
    {
        var registry = new AuditModelRegistry(new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["Person"] = typeof(PersonEntity)
        });

        var (recorder, store, observer) = Observed(registry: registry);

        // The only property that moved is [AuditIgnore], so there is nothing recordable left.
        Assert.Null(await recorder.For("Person", "42").UpdatedAsync(
        [
            new AuditChange(nameof(PersonEntity.LastSeenAt), DateTime.UnixEpoch, DateTime.UnixEpoch.AddDays(1))
        ]));

        Assert.Empty(store.Entries);
        Assert.Equal(AuditSuppression.NothingChanged, Assert.Single(observer.Suppressed).Reason);
    }

    [Fact]
    public async Task A_recorded_change_reports_nothing()
    {
        var (recorder, _, observer) = Observed();

        Assert.NotNull(await recorder.For(new Product { Id = 1 }).UpdatedAsync(
        [
            new AuditChange(nameof(Product.Price), 1m, 2m)
        ]));

        Assert.Empty(observer.Suppressed);
    }

    [Fact]
    public async Task A_write_with_no_declared_entity_reports_that_its_rules_were_not_applied()
    {
        var (recorder, store, observer) = Observed();

        // A Dapper application that never called DeclareEntitiesFrom: addressed by name, no type,
        // nothing in the registry. The entry IS written — and had this entity a [AuditRedact]
        // property, its value would have gone in with it.
        Assert.NotNull(await recorder.For("Person", "42").UpdatedAsync(
        [
            new AuditChange(nameof(PersonEntity.PasswordHash), "old", "new")
        ]));

        var report = Assert.Single(observer.WithoutModelRules);
        Assert.Equal("Person", report.EntityName);
        Assert.Equal("42", report.EntityId);

        // The proof that the warning is not hypothetical: the redacted value is in the table.
        var change = Assert.Single(Assert.Single(store.Entries).Changes);
        Assert.False(change.IsRedacted);
        Assert.Equal("new", change.NewValue);

        Assert.Empty(observer.Suppressed);
    }

    [Fact]
    public async Task A_declared_entity_applies_its_rules_and_reports_nothing()
    {
        var registry = new AuditModelRegistry(new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["Person"] = typeof(PersonEntity)
        });

        var (recorder, store, observer) = Observed(registry: registry);

        Assert.NotNull(await recorder.For("Person", "42").UpdatedAsync(
        [
            new AuditChange(nameof(PersonEntity.PasswordHash), "old", "new")
        ]));

        // Declared, so the rules were applied — and there is nothing to report.
        Assert.True(Assert.Single(Assert.Single(store.Entries).Changes).IsRedacted);
        Assert.Empty(observer.WithoutModelRules);
        Assert.Empty(observer.Suppressed);
    }

    [Fact]
    public async Task Addressing_a_write_with_the_entity_itself_needs_no_declaration()
    {
        var (recorder, store, observer) = Observed();

        // The type travels with the subject, so the rules come from the attributes directly and
        // there is nothing missing to report. This is why an EF Core application never sees the
        // warning: it always has the entity.
        Assert.NotNull(await recorder.For(new PersonEntity { Id = Guid.NewGuid() }).UpdatedAsync(
        [
            new AuditChange(nameof(PersonEntity.PasswordHash), "old", "new")
        ]));

        Assert.True(Assert.Single(Assert.Single(store.Entries).Changes).IsRedacted);
        Assert.Empty(observer.WithoutModelRules);
    }

    [Fact]
    public async Task A_default_subject_says_so_rather_than_throwing_a_null_reference() =>
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => default(AuditSubject).UpdatedAsync([]));
}
