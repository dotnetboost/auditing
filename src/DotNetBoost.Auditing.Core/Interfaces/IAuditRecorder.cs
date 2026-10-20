using DotNetBoost.Auditing.Core.Models;

namespace DotNetBoost.Auditing.Core.Interfaces;

/// <summary>
/// Records changes. One of the library's three services, and the only one that writes.
/// <code>
/// await audit.For(product).UpdatedAsync(
/// [
///     new AuditChange(nameof(Product.Price), 24.99m, 19.99m)
/// ], ct);
/// </code>
/// <para>
/// This is the explicit path, for Dapper and MongoDB applications and for anything an EF Core
/// change tracker cannot see — a raw <c>ExecuteUpdate</c>, a stored procedure, a domain event. An
/// EF Core application gets the same entries written for it by the interceptor in
/// <c>DotNetBoost.Auditing.EntityFrameworkCore</c>, and does not call this at all for ordinary
/// saves.
/// </para>
/// <para>
/// Reading is <see cref="IAuditReader"/>; deleting is <see cref="IAuditPruner"/>. A class is
/// given whichever of the three it needs and no more, so what it can do to the trail is visible at
/// its constructor rather than in its method bodies. There is deliberately no single service
/// combining them — see <c>docs/scope.md</c>.
/// </para>
/// </summary>
/// <seealso cref="IAuditReader"/>
/// <seealso cref="IAuditPruner"/>
public interface IAuditRecorder
{
    /// <summary>
    /// Records one change, applying the entity's ignore and redaction rules and the engine's
    /// formatting.
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
    /// Whether <paramref name="entityType"/> is audited at all, by its attributes and the current
    /// options. The question a capture source asks before doing the work of diffing an entity.
    /// </summary>
    /// <param name="entityType">The entity type.</param>
    /// <returns><c>false</c> when nothing about this type is recorded.</returns>
    bool IsAudited(Type entityType);
}

/// <summary>
/// One entity's trail, as something to record against — the result of
/// <see cref="IAuditRecorder.For(object)"/>.
/// </summary>
/// <remarks>
/// A struct, and deliberately nothing but the recorder and the two addressing strings: it exists to
/// stop a caller repeating the name and key on every call, not to hold state. Reads are not on it,
/// because reading is <see cref="IAuditReader"/>'s job and a recorder that could also read would be
/// injected by code that only wants to read.
/// </remarks>
public readonly struct AuditSubject : IEquatable<AuditSubject>
{
    private readonly IAuditRecorder _recorder;

    /// <summary>Addresses <paramref name="entityName"/>/<paramref name="entityId"/> on <paramref name="recorder"/>.</summary>
    /// <param name="recorder">The recorder to write through.</param>
    /// <param name="entityName">The stored entity name.</param>
    /// <param name="entityId">The stored entity key.</param>
    /// <exception cref="ArgumentNullException"><paramref name="recorder"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Either addressing part is empty.</exception>
    public AuditSubject(IAuditRecorder recorder, string entityName, string entityId)
        : this(recorder, entityName, entityId, entityType: null)
    {
    }

    /// <summary>
    /// As <see cref="AuditSubject(IAuditRecorder, string, string)"/>, carrying the entity's CLR
    /// type so that the model's <c>[AuditIgnore]</c> and <c>[AuditRedact]</c> rules can be applied
    /// to what is recorded.
    /// </summary>
    /// <param name="recorder">The recorder to write through.</param>
    /// <param name="entityName">The stored entity name.</param>
    /// <param name="entityId">The stored entity key.</param>
    /// <param name="entityType">The entity's type, or <c>null</c> when the caller has none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="recorder"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Either addressing part is empty.</exception>
    /// <remarks>
    /// Passing the type is what makes <c>audit.For(product)</c> stronger than
    /// <c>audit.For("Product", "42")</c>. <see cref="IAuditRecorder.For(object)"/> always passes it:
    /// dropping the type there would silently cost every write through it its redaction, which is
    /// the one rule whose failure is both invisible and permanent.
    /// </remarks>
    public AuditSubject(IAuditRecorder recorder, string entityName, string entityId, Type? entityType)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        _recorder  = recorder;
        EntityName = entityName;
        EntityId   = entityId;
        EntityType = entityType;
    }

    /// <summary>The entity's stored name.</summary>
    public string EntityName { get; }

    /// <summary>The entity's stored key.</summary>
    public string EntityId { get; }

    /// <summary>
    /// The entity's CLR type when the subject was made from an instance, else <c>null</c>. Carried
    /// so the engine can read the model's rules; never stored on the entry.
    /// </summary>
    public Type? EntityType { get; }

    /// <summary>Records that the entity was created.</summary>
    /// <param name="changes">Its initial values, as <c>NewValue</c>. Optional.</param>
    /// <param name="metadata">Application context as JSON. Optional.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored entry, or <c>null</c> when nothing was recorded.</returns>
    public Task<AuditEntry?> CreatedAsync(
        IEnumerable<AuditChange>? changes = null,
        string? metadata = null,
        CancellationToken ct = default) =>
        RecordAsync(AuditAction.Created, changes, metadata, ct);

    /// <summary>Records that the entity changed.</summary>
    /// <param name="changes">One per property that actually changed.</param>
    /// <param name="metadata">Application context as JSON. Optional.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored entry, or <c>null</c> when nothing was recorded.</returns>
    public Task<AuditEntry?> UpdatedAsync(
        IEnumerable<AuditChange> changes,
        string? metadata = null,
        CancellationToken ct = default) =>
        RecordAsync(AuditAction.Updated, changes, metadata, ct);

    /// <summary>Records that the entity was deleted.</summary>
    /// <param name="changes">Its final values, as <c>OldValue</c>. Optional, and worth supplying: after the row is gone this is the only copy.</param>
    /// <param name="metadata">Application context as JSON. Optional.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored entry, or <c>null</c> when nothing was recorded.</returns>
    public Task<AuditEntry?> DeletedAsync(
        IEnumerable<AuditChange>? changes = null,
        string? metadata = null,
        CancellationToken ct = default) =>
        RecordAsync(AuditAction.Deleted, changes, metadata, ct);

    /// <summary>Records a change of any kind.</summary>
    /// <param name="action">What happened.</param>
    /// <param name="changes">The properties that moved. Optional.</param>
    /// <param name="metadata">Application context as JSON. Optional.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored entry, or <c>null</c> when nothing was recorded.</returns>
    /// <exception cref="InvalidOperationException">The subject was not created through <see cref="IAuditRecorder.For(object)"/> and has no recorder.</exception>
    public Task<AuditEntry?> RecordAsync(
        AuditAction action,
        IEnumerable<AuditChange>? changes = null,
        string? metadata = null,
        CancellationToken ct = default)
    {
        // default(AuditSubject) has no recorder. Reachable only by constructing one deliberately,
        // which a caller has no reason to do, but a NullReferenceException from inside a library is
        // never the right way to say so.
        if (_recorder is null)
        {
            throw new InvalidOperationException(
                "This AuditSubject has no recorder. Get one from IAuditRecorder.For(…) rather than " +
                "default(AuditSubject).");
        }

        return _recorder.RecordAsync(new AuditWrite
        {
            EntityName = EntityName,
            EntityId   = EntityId,
            EntityType = EntityType,
            Action     = action,
            Changes    = changes is null ? [] : [.. changes],
            Metadata   = metadata
        }, ct);
    }

    /// <summary>Whether <paramref name="other"/> addresses the same entity through the same recorder.</summary>
    /// <param name="other">The subject to compare with.</param>
    /// <returns><c>true</c> when both address the same entity.</returns>
    public bool Equals(AuditSubject other) =>
        ReferenceEquals(_recorder, other._recorder)
        && string.Equals(EntityName, other.EntityName, StringComparison.Ordinal)
        && string.Equals(EntityId, other.EntityId, StringComparison.Ordinal)
        // The type is part of the identity because it changes what gets recorded: two subjects on
        // the same address, one with a type and one without, do not behave the same.
        && EntityType == other.EntityType;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is AuditSubject other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_recorder, EntityName, EntityId, EntityType);

    /// <summary>Whether two subjects address the same entity.</summary>
    /// <param name="left">The first subject.</param>
    /// <param name="right">The second subject.</param>
    /// <returns><c>true</c> when they are equal.</returns>
    public static bool operator ==(AuditSubject left, AuditSubject right) => left.Equals(right);

    /// <summary>Whether two subjects address different entities.</summary>
    /// <param name="left">The first subject.</param>
    /// <param name="right">The second subject.</param>
    /// <returns><c>true</c> when they differ.</returns>
    public static bool operator !=(AuditSubject left, AuditSubject right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => $"{EntityName}/{EntityId}";
}
