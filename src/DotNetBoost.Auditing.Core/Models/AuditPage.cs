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

    /// <summary>
    /// The position of the last entry on this page, to pass as <see cref="AuditQuery.After"/> for
    /// the next one. <c>null</c> when the page is empty, and therefore when the walk is over.
    /// </summary>
    /// <remarks>
    /// <b>Not a promise that more exist.</b> It is where this page ended, nothing more: the last
    /// full page of a trail carries a cursor, and the request after it comes back empty. Knowing in
    /// advance would mean reading a row past the page on every read to answer a question most
    /// callers never ask — the same reason a total is only counted when it is asked for.
    /// <para>
    /// Set by the engine rather than by each store, so the rule for what a cursor points at cannot
    /// differ between them. A store returning a page of its own leaves it <c>null</c>.
    /// </para>
    /// </remarks>
    public AuditCursor? NextCursor { get; init; }
}
