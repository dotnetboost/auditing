namespace DotNetBoost.Auditing.Core.Models;

/// <summary>
/// One page of a trail, newest first.
/// </summary>
/// <param name="Entries">The matching entries, ordered by <see cref="AuditEntry.OccurredAt"/> descending.</param>
/// <param name="Total">
/// How many entries matched the filters in total, or <c>null</c> when
/// <see cref="AuditQuery.IncludeTotal"/> was not set. <c>null</c> means <b>not counted</b>, never
/// zero — a page of entries alongside a total of zero would be a contradiction, and silently
/// returning the page length instead would be a lie an application would paginate against.
/// </param>
public sealed record AuditPage(IReadOnlyList<AuditEntry> Entries, long? Total)
{
    /// <summary>An empty page, with nothing counted.</summary>
    public static AuditPage Empty { get; } = new([], null);
}
