using DotNetBoost.Auditing.Core;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Metadata;
using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.Core.Services;

namespace DotNetBoost.Auditing.UnitTests;

/// <summary>
/// The explicit path's replacement for a change tracker. These assert the two things a caller of it
/// is trusting: that it reports every property that moved, and that it reports nothing a trail
/// cannot hold.
/// </summary>
public class AuditDiffTests
{
    [Fact]
    public void Only_the_properties_that_moved_are_reported_with_both_values()
    {
        var before = new Product { Id = 7, Name = "Desk lamp", Price = 24.99m };
        var after  = new Product { Id = 7, Name = "Desk lamp", Price = 19.99m };

        var change = Assert.Single(AuditDiff.Between(before, after));

        Assert.Equal(nameof(Product.Price), change.PropertyName);
        Assert.Equal(24.99m, change.OldValue);
        Assert.Equal(19.99m, change.NewValue);
    }

    [Fact]
    public void Nothing_moved_is_an_empty_list_rather_than_an_entry_that_says_nothing()
    {
        var before = new Product { Id = 7, Name = "Desk lamp", Price = 24.99m };
        var after  = new Product { Id = 7, Name = "Desk lamp", Price = 24.99m };

        Assert.Empty(AuditDiff.Between(before, after));
    }

    [Fact]
    public void The_key_is_never_a_property_change_because_it_is_the_entry_address()
    {
        var changes = AuditDiff.InitialValues(new Product { Id = 7, Name = "Desk lamp" });

        Assert.DoesNotContain(changes, c => c.PropertyName == nameof(Product.Id));
    }

    [Fact]
    public void A_composite_key_is_skipped_whole_rather_than_refused()
    {
        // Addressing an entity by a composite key has to fail — AuditEntry.EntityId holds one
        // value. Diffing one does not: both key properties are simply not property changes, and a
        // caller that addresses the write by hand can still record the rest.
        var changes = AuditDiff.InitialValues(new CompositeKeyed { TenantId = 1, LocalId = 2 });

        Assert.Empty(changes);
    }

    [Fact]
    public void An_ignored_property_moving_on_its_own_produces_nothing_to_record()
    {
        var before = new PersonEntity { Id = Guid.NewGuid(), Email = "ada@example.com" };
        var after  = new PersonEntity
        {
            Id          = before.Id,
            Email       = before.Email,
            LastSeenAt  = DateTime.UtcNow      // [AuditIgnore]
        };

        // The engine would drop it and refuse the write anyway. Reporting it as a change would make
        // the caller ask the store to do that work first.
        Assert.Empty(AuditDiff.Between(before, after));
    }

    [Fact]
    public void A_redacted_property_is_compared_and_reported_as_changed()
    {
        var before = new PersonEntity { Id = Guid.NewGuid(), PasswordHash = "old-hash" };
        var after  = new PersonEntity { Id = before.Id, PasswordHash = "new-hash" };

        var change = Assert.Single(AuditDiff.Between(before, after));

        // The values are in the list — the comparison happens in memory, where both values already
        // are. Withholding them is the engine's job, and the next test is the proof.
        Assert.Equal(nameof(PersonEntity.PasswordHash), change.PropertyName);
        Assert.Equal("old-hash", change.OldValue);
        Assert.Equal("new-hash", change.NewValue);
    }

    [Fact]
    public async Task A_diffed_change_list_is_held_to_the_same_model_rules_as_a_hand_written_one()
    {
        var (audit, store) = BuildEngine();

        var before = new PersonEntity { Id = Guid.NewGuid(), Email = "ada@example.com", PasswordHash = "old-hash" };
        var after  = new PersonEntity { Id = before.Id, Email = "ada@lovelace.test", PasswordHash = "new-hash" };

        await audit.For(after).UpdatedAsync(AuditDiff.Between(before, after));

        var entry = Assert.Single(store.Entries);

        var email = Assert.Single(entry.Changes, c => c.PropertyName == nameof(PersonEntity.Email));
        Assert.Equal("ada@example.com", email.OldValue);
        Assert.Equal("ada@lovelace.test", email.NewValue);

        // The whole claim of building the list by reflection: the model still decides what is
        // written down. A secret the diff read is recorded as having changed and nothing more.
        var hash = Assert.Single(entry.Changes, c => c.PropertyName == nameof(PersonEntity.PasswordHash));
        Assert.True(hash.IsRedacted);
        Assert.Null(hash.OldValue);
        Assert.Null(hash.NewValue);
    }

    [Fact]
    public void Byte_arrays_are_compared_by_content_not_by_reference()
    {
        var before = new Shipment { Id = 1, RowVersion = [1, 2, 3] };
        var after  = new Shipment { Id = 1, RowVersion = [1, 2, 3] };

        // Two reads of one row hold two arrays. By reference this would report a change on every
        // write, which is the trap EF Core's value comparers exist to avoid.
        Assert.Empty(AuditDiff.Between(before, after));

        after.RowVersion = [1, 2, 4];

        Assert.Single(AuditDiff.Between(before, after), c => c.PropertyName == nameof(Shipment.RowVersion));
    }

    [Fact]
    public void A_navigation_property_and_a_collection_are_skipped()
    {
        var before = new Shipment { Id = 1 };
        var after  = new Shipment
        {
            Id        = 1,
            Recipient = new PersonEntity { Id = Guid.NewGuid() },
            Labels    = ["fragile"]
        };

        // Skipped silently, which is the documented limit of a reflection diff: an entity whose
        // interesting state lives in a nested object reports those changes by hand.
        Assert.Empty(AuditDiff.Between(before, after));
    }

    [Fact]
    public void Nullable_values_and_enums_move_in_both_directions()
    {
        var dispatched = new DateOnly(2026, 10, 21);

        var before = new Shipment { Id = 1, Status = ShipmentStatus.Pending, DispatchedOn = null };
        var after  = new Shipment { Id = 1, Status = ShipmentStatus.Dispatched, DispatchedOn = dispatched };

        var forward = AuditDiff.Between(before, after);

        Assert.Single(forward, c => c.PropertyName == nameof(Shipment.Status)
            && Equals(c.OldValue, ShipmentStatus.Pending)
            && Equals(c.NewValue, ShipmentStatus.Dispatched));

        Assert.Single(forward, c => c.PropertyName == nameof(Shipment.DispatchedOn)
            && c.OldValue is null
            && Equals(c.NewValue, dispatched));

        // And back: a property cleared is a change, not an absence of one.
        var cleared = Assert.Single(
            AuditDiff.Between(after, before), c => c.PropertyName == nameof(Shipment.DispatchedOn));

        Assert.Equal(dispatched, cleared.OldValue);
        Assert.Null(cleared.NewValue);
    }

    [Fact]
    public void Initial_values_describe_the_whole_row_including_what_was_left_unset()
    {
        var changes = AuditDiff.InitialValues(new Product { Id = 7, Name = "Desk lamp" });

        Assert.Equal(2, changes.Count);

        var name = Assert.Single(changes, c => c.PropertyName == nameof(Product.Name));
        Assert.Null(name.OldValue);
        Assert.Equal("Desk lamp", name.NewValue);

        // The same thing the EF Core interceptor records for a creation: a property at its default
        // is part of the description of the row.
        var price = Assert.Single(changes, c => c.PropertyName == nameof(Product.Price));
        Assert.Equal(0m, price.NewValue);
    }

    [Fact]
    public void Final_values_are_the_same_properties_the_other_way_round()
    {
        var changes = AuditDiff.FinalValues(new Product { Id = 7, Name = "Desk lamp", Price = 19.99m });

        var name = Assert.Single(changes, c => c.PropertyName == nameof(Product.Name));
        Assert.Equal("Desk lamp", name.OldValue);
        Assert.Null(name.NewValue);
    }

    [Fact]
    public void An_ignored_property_is_in_neither_snapshot()
    {
        var person = new PersonEntity { Id = Guid.NewGuid(), LastSeenAt = DateTime.UtcNow };

        Assert.DoesNotContain(
            AuditDiff.InitialValues(person), c => c.PropertyName == nameof(PersonEntity.LastSeenAt));

        Assert.DoesNotContain(
            AuditDiff.FinalValues(person), c => c.PropertyName == nameof(PersonEntity.LastSeenAt));
    }

    [Fact]
    public void The_returned_list_is_the_callers_to_add_to()
    {
        var before = new Product { Id = 7, Name = "Desk lamp", Price = 24.99m };
        var after  = new Product { Id = 7, Name = "Desk lamp", Price = 19.99m };

        var changes = AuditDiff.Between(before, after);

        // A column a stored procedure moved, or a value inside a nested object: a diff over the
        // properties cannot see either, so the list it returns has to be open.
        changes.Add(new AuditChange("DiscountCode", null, "AUTUMN"));

        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public void Two_different_types_are_refused_rather_than_diffed()
    {
        var error = Assert.Throws<ArgumentException>(
            () => AuditDiff.Between(new Product { Id = 1 }, new Invoice { Id = 1 }));

        Assert.Contains("two states of one entity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nulls_are_refused()
    {
        var product = new Product { Id = 1 };

        Assert.Throws<ArgumentNullException>(() => AuditDiff.Between(null!, product));
        Assert.Throws<ArgumentNullException>(() => AuditDiff.Between(product, null!));
        Assert.Throws<ArgumentNullException>(() => AuditDiff.InitialValues(null!));
        Assert.Throws<ArgumentNullException>(() => AuditDiff.FinalValues(null!));
    }

    /// <summary>
    /// The engine as <c>AddAuditing</c> registers it, over a store that keeps what it was handed —
    /// the same arrangement <see cref="AuditRecorderTests"/> uses.
    /// </summary>
    private static (IAuditManager Audit, RecordingAuditStore Store) BuildEngine()
    {
        var options = new AuditingOptions();
        var store   = new RecordingAuditStore();
        var keys    = new ConventionEntityKeyAccessor();
        var time    = TimeProvider.System;
        var model   = AuditModelRegistry.Empty;

        var manager = new AuditManager(
            new AuditRecorder(store, new NullAuditActorAccessor(), keys, model, options, time, new NullAuditObserver()),
            new AuditReader(store, keys, options),
            new AuditPruner(store, keys, model, options, time));

        return (manager, store);
    }
}
