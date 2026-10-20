using DotNetBoost.Auditing.Core.Models;

namespace DotNetBoost.Auditing.Core.Interfaces;

/// <summary>
/// The one service application code injects — recording, reading and retention on a single
/// interface, as <c>ISettingManager</c>, <c>IMediaManager</c> and <c>IMultiLingualManager</c> are in
/// the sibling libraries.
/// <code>
/// await audit.For(product).UpdatedAsync(
/// [
///     new AuditChange(nameof(Product.Price), 24.99m, 19.99m)
/// ], ct);
///
/// var page = await audit.HistoryAsync(product, ct: ct);
/// </code>
/// <para>
/// <b>What this costs, stated once here rather than discovered later:</b> a class holding this
/// interface can write the trail, read it, and erase it. There is no narrower type to inject, so
/// "this code displays history and cannot alter it" is not something a constructor can express in
/// this library — it is a convention, enforced by review. <c>docs/scope.md</c> §10 has the
/// reasoning and what was given up for it.
/// </para>
/// </summary>
public interface IAuditManager
{
    // ---- Recording ------------------------------------------------------------------------

    /// <summary>
    /// A handle on one entity's trail, with its name and key already resolved.
    /// </summary>
    /// <param name="entity">The entity. Its name comes from <c>[AuditEntity]</c> or its type name, its key from the registered <see cref="IEntityKeyAccessor"/>.</param>
    /// <returns>The subject to record against.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The entity's key has not been assigned yet. An entity with no key cannot be addressed, and
    /// recording it under the default value's string form would file its history under <c>"0"</c>
    /// along with every other unsaved entity of its type. Save it first, or address it by
    /// <see cref="For(string, string)"/> with a key you compose yourself.
    /// </exception>
    AuditSubject For(object entity);

    /// <summary>
    /// A handle on one entity's trail, addressed by hand. The path for an entity this process has
    /// no instance of — a row a stored procedure changed, a key composed from several columns.
    /// </summary>
    /// <param name="entityName">The stored entity name.</param>
    /// <param name="entityId">The stored entity key.</param>
    /// <returns>The subject to record against.</returns>
    AuditSubject For(string entityName, string entityId);

    /// <summary>
    /// Records one change, applying the entity's ignore and redaction rules and the engine's
    /// formatting.
    /// <para>
    /// This is the explicit path, for Dapper and MongoDB applications and for anything an EF Core
    /// change tracker cannot see — a raw <c>ExecuteUpdate</c>, a stored procedure, a domain event.
    /// An EF Core application gets the same entries written for it by the interceptor in
    /// <c>DotNetBoost.Auditing.EntityFrameworkCore</c>, and does not call this at all for ordinary
    /// saves.
    /// </para>
    /// </summary>
    /// <param name="write">What happened.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The entry as it was stored, or <c>null</c> when nothing was recorded — recording is off, the
    /// entity is <c>[AuditIgnore]</c>, opt-in is required and it is not <c>[Audited]</c>, or every
    /// reported property was ignored, which makes an <see cref="AuditAction.Updated"/> write a write
    /// of nothing.
    /// </returns>
    /// <remarks>
    /// A <c>null</c> return is the normal way of saying "deliberately not recorded" and never
    /// means failure; a failure throws. Callers that must know a change was captured should check
    /// for <c>null</c> rather than assume.
    /// </remarks>
    Task<AuditEntry?> RecordAsync(AuditWrite write, CancellationToken ct = default);

    /// <summary>
    /// Whether <paramref name="entityType"/> is audited at all, by its attributes and the current
    /// options. The question a capture source asks before doing the work of diffing an entity.
    /// </summary>
    /// <param name="entityType">The entity type.</param>
    /// <returns><c>false</c> when nothing about this type is recorded.</returns>
    bool IsAudited(Type entityType);

    // ---- Reading --------------------------------------------------------------------------

    /// <summary>
    /// One entry by id, with its property changes.
    /// <para>
    /// Every read on this interface is bounded: <see cref="AuditQuery.Take"/> is clamped to
    /// <see cref="AuditingOptions.MaxPageSize"/> and an unset <c>Take</c> becomes
    /// <see cref="AuditingOptions.DefaultPageSize"/>. There is no "read it all" verb, deliberately —
    /// the predecessor in this suite shipped an unbounded history call over a table that only grows,
    /// and that is on the short list of why it was deleted.
    /// </para>
    /// </summary>
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

    // ---- Retention and erasure ---------------------------------------------------------------

    /// <summary>
    /// Deletes everything past its retention — <see cref="AuditingOptions.Retention"/>, or an
    /// entity's own <c>[AuditRetention]</c> where it has one — and reports how many entries went.
    /// <para>
    /// Nothing runs a prune on its own. There is no hosted service in Core and no timer: a library
    /// that quietly deleted history in a process that never asked it to would be the wrong kind of
    /// helpful, and the one irreversible thing it does should be something an application
    /// schedules.
    /// </para>
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
