namespace DotNetBoost.Auditing.Core.Models;

/// <summary>
/// One property's change within an <see cref="AuditEntry"/>, as it is stored.
/// <para>
/// A row of its own rather than a JSON column on the entry, because "every change anyone ever made
/// to <c>Price</c>" is most of why a trail is kept, and in JSON that question is an unindexable
/// scan in SQL Server and PostgreSQL alike. The entry keeps a JSON
/// <see cref="AuditEntry.Metadata"/> column for the context nothing queries.
/// </para>
/// </summary>
public sealed class AuditPropertyChange
{
    /// <summary>
    /// The entry this change belongs to. With <see cref="PropertyName"/> it forms the primary key:
    /// one row per property per entry is already unique, so the table needs no surrogate of its own
    /// and no sequence to contend on.
    /// </summary>
    public Guid AuditEntryId { get; set; }

    /// <summary>
    /// The property that changed, as named on the class — not as named in the database. A trail is
    /// read by people who know the model, and a column name drifts from it.
    /// </summary>
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>
    /// The value before the change, formatted invariantly, or <c>null</c> when the property had no
    /// previous value (a <see cref="AuditAction.Created"/> entry, or a column added since).
    /// Always <c>null</c> when <see cref="IsRedacted"/> is set.
    /// </summary>
    public string? OldValue { get; set; }

    /// <summary>
    /// The value after the change, formatted invariantly, or <c>null</c> for a
    /// <see cref="AuditAction.Deleted"/> entry. Always <c>null</c> when <see cref="IsRedacted"/>
    /// is set.
    /// </summary>
    public string? NewValue { get; set; }

    /// <summary>
    /// Whether the property is <c>[AuditRedact]</c>. Its values are withheld rather than recorded:
    /// the model decides what is sensitive, and a reader only learns that it changed. A caller
    /// cannot opt out of this, which is the point — the audit table should not be the place a
    /// secret escapes.
    /// </summary>
    public bool IsRedacted { get; set; }

    /// <summary>
    /// Whether <see cref="OldValue"/> or <see cref="NewValue"/> was cut short at
    /// <see cref="AuditingOptions.MaxValueLength"/>. Stored rather than inferred, because a value
    /// that happens to be exactly the limit is not truncated and must not read as though it were.
    /// </summary>
    public bool IsTruncated { get; set; }
}
