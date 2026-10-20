using DotNetBoost.Auditing.Core.Models;

namespace DotNetBoost.Auditing.Core.Interfaces;

/// <summary>
/// Where entries are kept. Implemented once per persistence technology —
/// <c>DotNetBoost.Auditing.EntityFrameworkCore</c>, <c>.Dapper</c>, <c>.MongoDb</c> — and never by
/// application code unless it is replacing all three.
/// <para>
/// A store is <b>append-only</b>. There is no update verb and there never will be: an audit entry
/// that can be edited is not an audit entry. The only deletions are the retention and erasure verbs
/// at the bottom, both of which an application has to call deliberately.
/// </para>
/// </summary>
/// <remarks>
/// <b>When a write becomes durable is the store's business, and they differ.</b> The EF Core store
/// adds entries to the caller's change tracker, so they commit inside the caller's own
/// <c>SaveChanges</c> and its transaction — the change and its trail land together or neither does.
/// The Dapper store executes against the caller's connection as the call is made, inside the
/// caller's transaction when one was handed to it. The MongoDB store writes its own document
/// immediately. Only the first gives the all-or-nothing guarantee, and the documentation says which
/// is which rather than implying they are alike.
/// </remarks>
public interface IAuditStore
{
    /// <summary>Appends one entry, with its property rows.</summary>
    /// <param name="entry">The entry to append. Its <see cref="AuditEntry.Id"/> is already set.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the store has accepted the entry.</returns>
    Task AppendAsync(AuditEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Appends several entries as one operation. One <c>SaveChanges</c> saves a whole unit of work,
    /// so the natural shape of a capture is many entries at once, and a store that could only take
    /// them one at a time would make a round trip per changed entity.
    /// </summary>
    /// <param name="entries">The entries to append. Each already has its <see cref="AuditEntry.Id"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the store has accepted them all.</returns>
    Task AppendRangeAsync(IReadOnlyCollection<AuditEntry> entries, CancellationToken ct = default);

    /// <summary>One entry by id, with its property rows, or <c>null</c> when there is no such entry.</summary>
    /// <param name="id">The entry's id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The entry, or <c>null</c>.</returns>
    Task<AuditEntry?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// A page of entries matching <paramref name="query"/>, newest first.
    /// </summary>
    /// <param name="query">
    /// The filters. <see cref="AuditQuery.Take"/> has already been clamped by the engine, so a
    /// store may trust it — and should still not interpret zero as "all of them".
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching page.</returns>
    Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken ct = default);

    /// <summary>
    /// Deletes entries older than <paramref name="cutoff"/>, at most
    /// <paramref name="batchSize"/> of them, and reports how many went.
    /// </summary>
    /// <param name="cutoff">Entries strictly older than this are eligible. UTC.</param>
    /// <param name="scope">Which entities the prune applies to — and, as importantly, which it must leave alone.</param>
    /// <param name="batchSize">The most entries to delete in this call.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many entries were deleted; fewer than <paramref name="batchSize"/> means the backlog is clear.</returns>
    /// <remarks>
    /// Batched and resumable rather than one statement, because the first prune of a trail that has
    /// been collecting for a year is a delete of millions of rows, and one statement would hold
    /// locks across the table that recording needs. The caller loops until the count comes back
    /// short; <see cref="IAuditManager.PruneAsync(int, CancellationToken)"/> is that loop.
    /// </remarks>
    Task<int> DeleteOlderThanAsync(DateTime cutoff, AuditPruneScope scope, int batchSize, CancellationToken ct = default);

    /// <summary>
    /// Deletes every entry for one entity, whatever its age, and reports how many went. The erasure
    /// path: a subject-access deletion request reaches the trail too, and a library that made that
    /// impossible would be a compliance problem rather than a compliance feature.
    /// </summary>
    /// <param name="entityName">The entity's stored name.</param>
    /// <param name="entityId">The entity's stored key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many entries were deleted.</returns>
    Task<int> DeleteForEntityAsync(string entityName, string entityId, CancellationToken ct = default);

    /// <summary>
    /// Deletes every entry recorded against one actor, and reports how many went. The other half of
    /// erasure: a person is a subject of the trail both as a row that changed and as the name that
    /// changed it.
    /// </summary>
    /// <param name="actor">The actor, matched exactly.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many entries were deleted.</returns>
    Task<int> DeleteForActorAsync(string actor, CancellationToken ct = default);
}
