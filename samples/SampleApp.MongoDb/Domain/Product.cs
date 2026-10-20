using DotNetBoost.Auditing.Core.Attributes;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace SampleApp.MongoDb.Domain;

/// <summary>
/// The EF Core sample's <c>Product</c>, as a MongoDB document. The auditing attributes are
/// <b>identical</b>, and that is the point of having them here: they are read by the engine, not by
/// a store, so the same three declarations hold whichever store is underneath.
/// <para>
/// Two differences from the EF Core version, both about documents rather than about this library.
/// The key is an <c>int</c> rather than an <c>ObjectId</c>, so the routes, the responses and the
/// dashboard are unchanged — MongoDB does not generate integers, so <c>Data/IdSequence.cs</c> does.
/// And the money fields say how they are stored: the driver's default for <c>decimal</c> is a
/// <b>string</b>, which sorts and compares lexicographically — so a query for "everything under
/// ten" would answer nonsense, and arithmetic in an aggregation would refuse outright.
/// </para>
/// <para>
/// Nothing here mentions the audit collection. It is a collection in this same database, beside
/// <c>products</c> — open mongo-express from the Aspire dashboard and it is there, one document per
/// entry with the property changes embedded in it.
/// </para>
/// </summary>
[AuditEntity("Product")]
public sealed class Product
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal Price { get; set; }

    public string Category { get; set; } = "";

    /// <summary>
    /// What the supplier charges. Recorded as <i>changed</i> and never with its values: a trail is
    /// a place a secret would sit in plain text for years.
    /// </summary>
    /// <remarks>
    /// The stake is a little higher here than in a relational store. An entry is one document, so a
    /// leaked value is a field inside a document that nothing else needs to join to — easy to read,
    /// and easy to forget is there.
    /// </remarks>
    [AuditRedact]
    [BsonRepresentation(BsonType.Decimal128)]
    public decimal SupplierCost { get; set; }

    /// <summary>
    /// Bumped on every page view. Ignored, or the trail would be a view counter with the price
    /// changes buried in it.
    /// </summary>
    /// <remarks>
    /// <c>GET /api/products/{id}</c> reports this change to the recorder deliberately, to show what
    /// the engine does with it: every reported property is ignored, so an <c>Updated</c> write of
    /// nothing records nothing and the call returns <c>null</c>.
    /// </remarks>
    [AuditIgnore]
    public int ViewCount { get; set; }
}

/// <summary>
/// A document written by a background job every few minutes, kept for a month.
/// </summary>
/// <remarks>
/// Here to show the two halves of retention at once: this entity keeps its history for 30 days
/// while everything else keeps it for the configured year, and a prune honours both — a global
/// sweep must not take the longer trail with it. The prune resolves that per-entity retention from
/// the <b>declared</b> types, which is one reason <c>DeclareEntitiesFrom</c> is not optional here.
/// </remarks>
[AuditEntity("StockCheck")]
[AuditRetention(Days = 30)]
public sealed class StockCheck
{
    public int Id { get; set; }

    public int ProductId { get; set; }

    public int OnHand { get; set; }

    public DateTime CheckedAt { get; set; }
}
