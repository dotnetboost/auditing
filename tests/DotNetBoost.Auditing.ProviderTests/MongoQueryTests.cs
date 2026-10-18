using DotNetBoost.Auditing.Core.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using DotNetBoost.Auditing.MongoDb;

namespace DotNetBoost.Auditing.ProviderTests;

/// <summary>
/// The filters the store builds, rendered to BSON. Again no server: what is asserted here is the
/// query the store would send, which is where a filter means something other than it reads.
/// </summary>
public class MongoQueryTests
{
    private static BsonDocument Render(AuditQuery query)
    {
        var serializer = BsonSerializer.SerializerRegistry.GetSerializer<AuditEntryDocument>();

        return MongoAuditStore.Filter(query)
            .Render(new MongoDB.Driver.RenderArgs<AuditEntryDocument>(serializer, BsonSerializer.SerializerRegistry));
    }

    [Fact]
    public void An_empty_query_filters_nothing()
    {
        Assert.Empty(Render(new AuditQuery()));
    }

    [Fact]
    public void An_entitys_address_filters_on_both_halves()
    {
        var filter = Render(new AuditQuery { EntityName = "Product", EntityId = "42" });

        Assert.Equal("Product", filter["EntityName"].AsString);
        Assert.Equal("42", filter["EntityId"].AsString);
    }

    [Fact]
    public void An_action_filter_compares_against_the_stored_name()
    {
        // The document stores the name, so a filter built from the enum has to render as a string
        // or it matches nothing — silently, with no error anywhere.
        Assert.Equal("Deleted", Render(new AuditQuery { Action = AuditAction.Deleted })["Action"].AsString);
    }

    [Fact]
    public void ActorMissing_asks_for_a_null_actor()
    {
        var filter = Render(new AuditQuery { ActorMissing = true });

        // The field is left out of the document when no actor was captured, and an equality against
        // null matches a missing field as well as an explicit one.
        Assert.Equal(BsonNull.Value, filter["Actor"]);
    }

    [Fact]
    public void An_actor_filter_and_ActorMissing_are_different_questions()
    {
        Assert.Equal("alice", Render(new AuditQuery { Actor = "alice" })["Actor"].AsString);
    }

    [Fact]
    public void A_window_is_inclusive_at_the_start_and_exclusive_at_the_end()
    {
        var from = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var to   = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

        var filter = Render(new AuditQuery { From = from, To = to });

        // So consecutive windows do not double-count.
        var occurred = filter["OccurredAt"].AsBsonDocument;
        Assert.True(occurred.Contains("$gte"));
        Assert.True(occurred.Contains("$lt"));
        Assert.False(occurred.Contains("$lte"));
    }

    [Fact]
    public void An_unspecified_timestamp_is_treated_as_UTC()
    {
        var unspecified = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Unspecified);

        var filter = Render(new AuditQuery { From = unspecified });

        // Everything this library writes is UTC, so a caller handing over an unspecified value has
        // lost the kind, not the zone. Treating it as local would shift the window by the host's
        // offset and quietly return the wrong days.
        var expected = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, filter["OccurredAt"]["$gte"].ToUniversalTime());
    }

    [Fact]
    public void Every_filter_combines_with_AND()
    {
        var filter = Render(new AuditQuery
        {
            EntityName    = "Product",
            EntityId      = "42",
            Action        = AuditAction.Updated,
            Actor         = "alice",
            CorrelationId = "edit-7"
        });

        Assert.Equal(5, filter.ElementCount);
    }

    [Fact]
    public void Every_index_this_store_needs_is_named_and_defined()
    {
        var definitions = MongoAuditIndexes.Definitions();

        var names = definitions
            .Select(d => d.Options.Name)
            .ToList();

        Assert.Equal(
            ["entity_occurred", "actor_occurred", "occurred", "correlation", "change_property"],
            names);
    }

    [Fact]
    public void The_main_read_is_served_by_one_index_covering_the_filter_and_the_sort()
    {
        var serializer = BsonSerializer.SerializerRegistry.GetSerializer<AuditEntryDocumentShape>();

        var keys = MongoAuditIndexes.Definitions()[0].Keys
            .Render(new MongoDB.Driver.RenderArgs<AuditEntryDocumentShape>(serializer, BsonSerializer.SerializerRegistry));

        // Address ascending, then the sort descending: one index seek serves "this entity's
        // history, newest first" without a sort stage.
        Assert.Equal(1, keys["EntityName"].AsInt32);
        Assert.Equal(1, keys["EntityId"].AsInt32);
        Assert.Equal(-1, keys["OccurredAt"].AsInt32);
    }

    [Fact]
    public void The_embedded_array_is_indexed_so_the_per_property_question_survives_embedding()
    {
        var serializer = BsonSerializer.SerializerRegistry.GetSerializer<AuditEntryDocumentShape>();

        var keys = MongoAuditIndexes.Definitions()[^1].Keys
            .Render(new MongoDB.Driver.RenderArgs<AuditEntryDocumentShape>(serializer, BsonSerializer.SerializerRegistry));

        // The whole reason embedding costs nothing here: "every change anyone ever made to Price" is
        // a multikey index seek rather than a collection scan — the question the relational stores
        // give a table of their own to answer.
        Assert.Equal(1, keys["Changes.PropertyName"].AsInt32);
    }

    [Fact]
    public void The_actor_index_is_not_sparse()
    {
        var actorIndex = MongoAuditIndexes.Definitions()[1];

        // A sparse index would skip the documents with no actor — which are exactly the ones
        // ActorMissing asks for.
        Assert.NotEqual(true, actorIndex.Options.Sparse);
    }
}
