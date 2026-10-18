using DotNetBoost.Auditing.Core.Attributes;

namespace SampleApp.Domain;

/// <summary>
/// A product, and the one class in this sample that says anything about auditing. Three attributes
/// are the whole of it — there is no base class, no interface and no column.
/// </summary>
[AuditEntity("Product")]
public class Product
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public decimal Price { get; set; }

    public string Category { get; set; } = "";

    /// <summary>
    /// What the supplier charges. Recorded as <i>changed</i> and never with its values: a trail is
    /// a place a secret would sit in plain text for years.
    /// </summary>
    [AuditRedact]
    public decimal SupplierCost { get; set; }

    /// <summary>
    /// Bumped on every page view. Ignored, or the trail would be a view counter with the price
    /// changes buried in it.
    /// </summary>
    [AuditIgnore]
    public int ViewCount { get; set; }
}

/// <summary>
/// A row written by a background job every few minutes, kept for a month.
/// </summary>
/// <remarks>
/// Here to show the two halves of retention at once: this entity keeps its history for 30 days
/// while everything else keeps it for the configured year, and a prune honours both — a global
/// sweep must not take the longer trail with it.
/// </remarks>
[AuditEntity("StockCheck")]
[AuditRetention(Days = 30)]
public class StockCheck
{
    public int Id { get; set; }

    public int ProductId { get; set; }

    public int OnHand { get; set; }

    public DateTime CheckedAt { get; set; }
}
