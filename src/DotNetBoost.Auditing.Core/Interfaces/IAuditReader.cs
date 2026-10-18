using DotNetBoost.Auditing.Core.Models;

namespace DotNetBoost.Auditing.Core.Interfaces;

/// <summary>
/// Reads trails. Separate from <see cref="IAuditRecorder"/> so that the code which only displays a
/// history cannot write one.
/// <para>
/// Every read through this service is bounded: <see cref="AuditQuery.Take"/> is clamped to
/// <see cref="AuditingOptions.MaxPageSize"/> and an unset <c>Take</c> becomes
/// <see cref="AuditingOptions.DefaultPageSize"/>. There is no "read it all" verb, deliberately —
/// the predecessor in this suite shipped an unbounded history call over a table that only grows,
/// and that is on the short list of why it was deleted.
/// </para>
/// </summary>
public interface IAuditReader
{
    /// <summary>One entry by id, with its property changes.</summary>
    /// <param name="id">The entry's id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The entry, or <c>null</c> when there is no such entry.</returns>
    Task<AuditEntry?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>A page of entries matching <paramref name="query"/>, newest first.</summary>
    /// <param name="query">The filters and the page. Clamped before it reaches the store.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching page.</returns>
    Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken ct = default);

    /// <summary>
    /// One entity's history, newest first — the common case, without composing a query.
    /// </summary>
    /// <param name="entity">The entity whose trail to read. Addressed the same way recording addresses it.</param>
    /// <param name="skip">Entries to skip.</param>
    /// <param name="take">Entries to return. Clamped; zero means the default page size.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching page.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">The entity's key has not been assigned yet, so it has no trail to read.</exception>
    Task<AuditPage> HistoryAsync(object entity, int skip = 0, int take = 0, CancellationToken ct = default);
}
