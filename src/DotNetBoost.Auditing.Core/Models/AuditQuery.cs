namespace DotNetBoost.Auditing.Core.Models;

/// <summary>
/// Filters for reading a trail. Every field is optional and they combine with AND; an empty query
/// reads the newest page of everything.
/// <para>
/// <b>Always bounded.</b> <see cref="Take"/> is clamped to
/// <see cref="AuditingOptions.MaxPageSize"/> by the engine, because an unbounded read of a table
/// that only grows is a query that works for a year and then takes an application down.
/// </para>
/// </summary>
public sealed record AuditQuery
{
    /// <summary>The entity's stored name — <c>"Product"</c>. Null matches every entity.</summary>
    public string? EntityName { get; init; }

    /// <summary>
    /// The entity's stored key. Only meaningful together with <see cref="EntityName"/>: keys are
    /// unique per entity, not across them, so <c>"42"</c> alone matches one row of every table.
    /// </summary>
    public string? EntityId { get; init; }

    /// <summary>Only this kind of change. Null matches all three.</summary>
    public AuditAction? Action { get; init; }

    /// <summary>
    /// Only changes by this actor, matched exactly. Note that <c>null</c> here means "any actor",
    /// not "entries with no actor" — use <see cref="ActorMissing"/> for those.
    /// </summary>
    public string? Actor { get; init; }

    /// <summary>
    /// Only entries whose actor was never captured. The two ways of having no actor — nobody asked
    /// for a filter, and the filter is "nobody was recorded" — are different questions, and one
    /// nullable string cannot ask both.
    /// </summary>
    public bool ActorMissing { get; init; }

    /// <summary>Only entries sharing this correlation id — the other halves of one edit.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Only entries at or after this instant. UTC.</summary>
    public DateTime? From { get; init; }

    /// <summary>Only entries strictly before this instant. UTC — exclusive, so consecutive windows do not double-count.</summary>
    public DateTime? To { get; init; }

    /// <summary>Entries to skip. Negative values are treated as zero.</summary>
    public int Skip { get; init; }

    /// <summary>
    /// Entries to return, clamped to <see cref="AuditingOptions.MaxPageSize"/>. Zero or negative
    /// means <see cref="AuditingOptions.DefaultPageSize"/> rather than "all of them".
    /// </summary>
    public int Take { get; init; }

    /// <summary>
    /// Whether to count the matching entries as well as return a page of them. Off by default: the
    /// count is a second pass over the index, and most callers paging a history never show a total.
    /// </summary>
    public bool IncludeTotal { get; init; }

    /// <summary>
    /// Whether to load each entry's <see cref="AuditEntry.Changes"/>. On by default, because a
    /// trail without the changes is a list of timestamps. Turn it off for a count-like read over a
    /// wide window, where the property rows outnumber the entries by an order of magnitude.
    /// </summary>
    public bool IncludeChanges { get; init; } = true;
}
