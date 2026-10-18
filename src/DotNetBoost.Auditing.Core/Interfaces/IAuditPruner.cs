namespace DotNetBoost.Auditing.Core.Interfaces;

/// <summary>
/// Deletes entries that have outlived their retention, and erases an entity's or an actor's trail
/// on request.
/// <para>
/// An audit table only grows, and nothing else in this suite has that property. A library that
/// records every change and offers no way to stop is a library that fills a disk, so this ships in
/// Core rather than waiting for someone to ask.
/// </para>
/// </summary>
/// <remarks>
/// Nothing runs a prune on its own. There is no hosted service in Core and no timer: a library that
/// quietly deleted history in a process that never asked it to would be the wrong kind of helpful,
/// and the one irreversible thing it does should be something an application schedules.
/// </remarks>
public interface IAuditPruner
{
    /// <summary>
    /// Deletes everything past its retention — <see cref="AuditingOptions.Retention"/>, or an
    /// entity's own <c>[AuditRetention]</c> where it has one — and reports how many entries went.
    /// </summary>
    /// <param name="batchSize">How many entries to delete per round trip. Batched because the first prune of a year-old trail is millions of rows, and one statement would lock the table against recording.</param>
    /// <param name="ct">Cancellation token. A cancelled prune keeps what it has already deleted; the next run resumes.</param>
    /// <returns>How many entries were deleted in total.</returns>
    /// <remarks>
    /// Does nothing and returns zero when no retention is configured anywhere. Deleting on a
    /// default would be deleting history nobody asked to lose.
    /// </remarks>
    Task<int> PruneAsync(int batchSize = 1000, CancellationToken ct = default);

    /// <summary>
    /// Deletes entries older than <paramref name="olderThan"/>, whatever the configured retention,
    /// and reports how many went. The manual form, for a one-off cleanup.
    /// </summary>
    /// <param name="olderThan">The age past which entries are deleted.</param>
    /// <param name="entityName">Only this entity's entries, or <c>null</c> for every entity.</param>
    /// <param name="batchSize">How many entries to delete per round trip.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many entries were deleted.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="olderThan"/> is zero or negative, which would delete the entry being written as it is written.</exception>
    Task<int> PruneOlderThanAsync(
        TimeSpan olderThan,
        string? entityName = null,
        int batchSize = 1000,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes one entity's entire trail. The erasure path for a subject-access deletion request,
    /// which reaches the audit table too.
    /// </summary>
    /// <param name="entity">The entity whose trail to erase.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many entries were deleted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is <c>null</c>.</exception>
    Task<int> EraseForAsync(object entity, CancellationToken ct = default);

    /// <summary>
    /// Deletes one entity's entire trail, addressed by hand — for an entity this process has no
    /// instance of, or one whose row is already gone.
    /// </summary>
    /// <param name="entityName">The stored entity name.</param>
    /// <param name="entityId">The stored entity key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many entries were deleted.</returns>
    Task<int> EraseForAsync(string entityName, string entityId, CancellationToken ct = default);

    /// <summary>
    /// Deletes every entry recorded against one actor. The other half of erasure: a person appears
    /// in the trail both as the row that changed and as the name that changed it.
    /// </summary>
    /// <param name="actor">The actor, matched exactly.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many entries were deleted.</returns>
    Task<int> EraseForActorAsync(string actor, CancellationToken ct = default);
}
