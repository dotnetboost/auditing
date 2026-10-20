using DotNetBoost.Auditing.Core.Attributes;

namespace SampleApp.Dapper.Domain;

/// <summary>
/// The EF Core sample's <c>Product</c>, as a plain object Dapper maps. The attributes are
/// <b>identical</b>, and that is the point of having them here: they are read by the engine, not by
/// a provider, so the same three declarations hold whichever store is underneath.
/// <para>
/// What changes is who notices a change. In the EF Core sample these attributes are consulted by an
/// interceptor reading the change tracker; here every entry is written by hand in
/// <c>Program.cs</c> — and the attributes still decide what reaches the table, because redaction and
/// ignoring are applied in the engine rather than in the capture source.
/// </para>
/// <para>
/// One thing is <b>not</b> optional for this sample and is for the EF Core one:
/// <c>DeclareEntitiesFrom(typeof(Product).Assembly)</c>. An entry addressed by name —
/// <c>audit.For("Product", "42")</c>, which is how a prune finds this type's retention and how a
/// write with no instance is recorded — has no type for the engine to read these attributes from.
/// </para>
/// </summary>
[AuditEntity("Product")]
public sealed class Product
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public decimal Price { get; set; }

    public string Category { get; set; } = "";

    /// <summary>
    /// What the supplier charges. Recorded as <i>changed</i> and never with its values: a trail is
    /// a place a secret would sit in plain text for years.
    /// </summary>
    /// <remarks>
    /// Every write in this sample goes through <c>audit.For(entity)</c>, which carries the type, so
    /// this stays redacted even with <c>DeclareEntitiesFrom</c> commented out. The declaration is
    /// what protects a write that has <b>no instance to carry a type</b> —
    /// <c>audit.For("Product", "42")</c>, which is what a composite key or a row changed by a
    /// stored procedure forces. Undeclared, that write puts this value in the table in full:
    /// silently, and permanently.
    /// </remarks>
    [AuditRedact]
    public decimal SupplierCost { get; set; }

    /// <summary>
    /// Bumped on every page view. Ignored, or the trail would be a view counter with the price
    /// changes buried in it.
    /// </summary>
    /// <remarks>
    /// <c>GET /api/products/{id}</c> reports this change to the recorder deliberately, to show what
    /// the engine does with it: every reported property is ignored, so an <c>Updated</c> write of
    /// nothing records nothing and the call returns <c>null</c>. An application cannot opt out of
    /// that by addressing the write itself.
    /// </remarks>
    [AuditIgnore]
    public int ViewCount { get; set; }
}

/// <summary>
/// A row written by a background job every few minutes, kept for a month.
/// </summary>
/// <remarks>
/// Here to show the two halves of retention at once: this entity keeps its history for 30 days
/// while everything else keeps it for the configured year, and a prune honours both — a global
/// sweep must not take the longer trail with it. The prune resolves that per-entity retention from
/// the <b>declared</b> types, which is the second reason <c>DeclareEntitiesFrom</c> is not optional
/// here.
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
