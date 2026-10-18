using DotNetBoost.Auditing.Core.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using DotNetBoost.Auditing.MongoDb;

namespace DotNetBoost.Auditing.ProviderTests;

/// <summary>
/// What goes into the collection, asserted by serializing it. No server is involved and none is
/// needed: these are the decisions that would be hardest to change later — how an id is
/// represented, how an enum is spelled, what an absent actor looks like.
/// </summary>
public class MongoDocumentTests
{
    private static readonly Guid EntryId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    private static AuditEntry Entry(Action<AuditEntry>? configure = null)
    {
        var entry = new AuditEntry
        {
            Id            = EntryId,
            EntityName    = "Product",
            EntityId      = "42",
            Action        = AuditAction.Updated,
            Actor         = "alice@example.com",
            CorrelationId = "edit-7",
            OccurredAt    = new DateTime(2026, 10, 17, 9, 30, 0, DateTimeKind.Utc),
            Metadata      = """{"ticket":"OPS-1"}""",
            Changes =
            {
                new AuditPropertyChange
                {
                    AuditEntryId = EntryId,
                    PropertyName = "Price",
                    OldValue     = "24.99",
                    NewValue     = "19.99"
                }
            }
        };

        configure?.Invoke(entry);
        return entry;
    }

    private static BsonDocument Document(AuditEntry entry) =>
        AuditEntryDocument.From(entry).ToBsonDocument();

    [Fact]
    public void The_entrys_id_is_the_documents_id()
    {
        var document = Document(Entry());

        // One unique index instead of two, and a lookup by id needs no secondary index.
        Assert.True(document.Contains("_id"));
        Assert.False(document.Contains("Id"));
    }

    [Fact]
    public void The_id_is_a_standard_uuid_rather_than_the_legacy_binary_subtype()
    {
        var binary = Document(Entry())["_id"].AsBsonBinaryData;

        // Left to the driver's default this would go in as the legacy subtype, which other drivers
        // and the shell read as a DIFFERENT value — and an audit trail whose ids change meaning
        // when read by another tool is not much of a record.
        Assert.Equal(BsonBinarySubType.UuidStandard, binary.SubType);
        Assert.Equal(EntryId, binary.ToGuid());
    }

    [Fact]
    public void The_action_is_stored_as_its_name()
    {
        // The relational stores keep an integer, which is what their index wants. A document is read
        // by people as often as by code, and a renumbering cannot change what a stored document
        // meant.
        Assert.Equal("Updated", Document(Entry())["Action"].AsString);
    }

    [Fact]
    public void The_changes_are_embedded_in_the_entry()
    {
        var changes = Document(Entry())["Changes"].AsBsonArray;

        // One document, not two collections: an entry is written once, read whole and never
        // updated, and one insert then writes all of it.
        var change = Assert.Single(changes).AsBsonDocument;
        Assert.Equal("Price", change["PropertyName"].AsString);
        Assert.Equal("24.99", change["OldValue"].AsString);

        // No parent id on the element: the entry it belongs to is the document it is inside.
        Assert.False(change.Contains("AuditEntryId"));
    }

    [Fact]
    public void An_empty_change_list_is_still_an_array()
    {
        var document = Document(Entry(e => e.Changes.Clear()));

        // A missing field and an empty array read differently in an aggregation, and a reader
        // should not have to know which it is.
        Assert.True(document.Contains("Changes"));
        Assert.Empty(document["Changes"].AsBsonArray);
    }

    [Fact]
    public void An_uncaptured_actor_is_left_out_of_the_document()
    {
        var document = Document(Entry(e => e.Actor = null));

        // Smaller documents, and a query for a missing field matches this as well as an explicit
        // null — so ActorMissing still finds exactly these.
        Assert.False(document.Contains("Actor"));
    }

    [Fact]
    public void Absent_values_on_a_redacted_change_are_left_out_too()
    {
        var entry = Entry(e =>
        {
            e.Changes.Clear();
            e.Changes.Add(new AuditPropertyChange
            {
                PropertyName = "PasswordHash",
                IsRedacted   = true
            });
        });

        var change = Document(entry)["Changes"].AsBsonArray[0].AsBsonDocument;

        Assert.False(change.Contains("OldValue"));
        Assert.False(change.Contains("NewValue"));
        Assert.True(change["IsRedacted"].AsBoolean);
    }

    [Fact]
    public void A_document_round_trips_back_to_the_model_it_came_from()
    {
        var original = Entry();
        var bson     = Document(original);

        var restored = BsonSerializer.Deserialize<AuditEntryDocument>(bson).ToEntry();

        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.EntityName, restored.EntityName);
        Assert.Equal(original.EntityId, restored.EntityId);
        Assert.Equal(original.Action, restored.Action);
        Assert.Equal(original.Actor, restored.Actor);
        Assert.Equal(original.CorrelationId, restored.CorrelationId);
        Assert.Equal(original.Metadata, restored.Metadata);
        Assert.Equal(original.OccurredAt, restored.OccurredAt);
        Assert.Equal(DateTimeKind.Utc, restored.OccurredAt.Kind);

        var change = Assert.Single(restored.Changes);
        Assert.Equal("Price", change.PropertyName);
        Assert.Equal("19.99", change.NewValue);

        // Filled back in from the document it was inside, so a caller sees the same model whichever
        // store produced it.
        Assert.Equal(original.Id, change.AuditEntryId);
    }

    [Fact]
    public void A_timestamp_loses_everything_below_the_millisecond()
    {
        // BSON dates hold milliseconds; the relational stores keep 100-nanosecond ticks. This is a
        // real difference between the backends and it is documented rather than hidden — a trail
        // here and a trail in SQL will not compare equal to the tick.
        var precise = new DateTime(2026, 10, 17, 9, 30, 0, DateTimeKind.Utc).AddTicks(1234567);

        var restored = BsonSerializer
            .Deserialize<AuditEntryDocument>(Document(Entry(e => e.OccurredAt = precise)))
            .ToEntry();

        Assert.NotEqual(precise, restored.OccurredAt);
        Assert.Equal(precise, restored.OccurredAt, TimeSpan.FromMilliseconds(1));

        // Ordering is unaffected: the entry id is the tiebreaker in every sort this store issues.
        Assert.Equal(0, restored.OccurredAt.Ticks % TimeSpan.TicksPerMillisecond);
    }

    [Fact]
    public void A_local_timestamp_is_stored_as_UTC()
    {
        var local = new DateTime(2026, 10, 17, 9, 30, 0, DateTimeKind.Local);

        var restored = BsonSerializer
            .Deserialize<AuditEntryDocument>(Document(Entry(e => e.OccurredAt = local)))
            .ToEntry();

        Assert.Equal(DateTimeKind.Utc, restored.OccurredAt.Kind);
        Assert.Equal(local.ToUniversalTime(), restored.OccurredAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void No_class_map_is_registered_for_the_Core_models()
    {
        // The design promise this package makes: the driver's map registry is static and
        // process-wide, so a library that registered a map for a Core type would be deciding how
        // every other piece of code in the application serializes it. Touching the document type is
        // enough to trigger any registration that was going to happen.
        _ = Document(Entry());

        Assert.False(BsonClassMap.IsClassMapRegistered(typeof(AuditEntry)));
        Assert.False(BsonClassMap.IsClassMapRegistered(typeof(AuditPropertyChange)));
    }
}
