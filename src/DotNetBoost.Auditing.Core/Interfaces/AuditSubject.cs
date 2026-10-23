using DotNetBoost.Auditing.Core.Models;

namespace DotNetBoost.Auditing.Core.Interfaces;

/// <summary>
/// One entity's trail, as something to record against — the result of
/// <see cref="IAuditManager.For(object)"/>.
/// </summary>
/// <remarks>
/// A struct, and deliberately nothing but the manager and the two addressing strings: it exists to
/// stop a caller repeating the name and key on every call, not to hold state. Reading and erasing
/// an entity's trail stay on <see cref="IAuditManager"/> itself — <c>HistoryAsync</c> and
/// <c>EraseForAsync</c> both take the entity — so that the verbs reachable from a subject are the
/// ones that append to it.
/// </remarks>
public readonly struct AuditSubject : IEquatable<AuditSubject>
{
    private readonly IAuditManager _manager;

    /// <summary>Addresses <paramref name="entityName"/>/<paramref name="entityId"/> on <paramref name="manager"/>.</summary>
    /// <param name="manager">The manager to write through.</param>
    /// <param name="entityName">The stored entity name.</param>
    /// <param name="entityId">The stored entity key.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manager"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Either addressing part is empty.</exception>
    public AuditSubject(IAuditManager manager, string entityName, string entityId)
        : this(manager, entityName, entityId, entityType: null)
    {
    }

    /// <summary>
    /// As <see cref="AuditSubject(IAuditManager, string, string)"/>, carrying the entity's CLR
    /// type so that the model's <c>[AuditIgnore]</c> and <c>[AuditRedact]</c> rules can be applied
    /// to what is recorded.
    /// </summary>
    /// <param name="manager">The manager to write through.</param>
    /// <param name="entityName">The stored entity name.</param>
    /// <param name="entityId">The stored entity key.</param>
    /// <param name="entityType">The entity's type, or <c>null</c> when the caller has none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manager"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Either addressing part is empty.</exception>
    /// <remarks>
    /// Passing the type is what makes <c>audit.For(product)</c> stronger than
    /// <c>audit.For("Product", "42")</c>. <see cref="IAuditManager.For(object)"/> always passes it:
    /// dropping the type there would silently cost every write through it its redaction, which is
    /// the one rule whose failure is both invisible and permanent.
    /// </remarks>
    public AuditSubject(IAuditManager manager, string entityName, string entityId, Type? entityType)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        _manager   = manager;
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
    /// <exception cref="InvalidOperationException">The subject was not created through <see cref="IAuditManager.For(object)"/> and has no manager.</exception>
    public Task<AuditEntry?> RecordAsync(
        AuditAction action,
        IEnumerable<AuditChange>? changes = null,
        string? metadata = null,
        CancellationToken ct = default)
    {
        // default(AuditSubject) has no manager. Reachable only by constructing one deliberately,
        // which a caller has no reason to do, but a NullReferenceException from inside a library is
        // never the right way to say so.
        if (_manager is null)
        {
            throw new InvalidOperationException(
                "This AuditSubject has no manager. Get one from IAuditManager.For(…) rather than " +
                "default(AuditSubject).");
        }

        return _manager.RecordAsync(new AuditWrite
        {
            EntityName = EntityName,
            EntityId   = EntityId,
            EntityType = EntityType,
            Action     = action,
            Changes    = changes is null ? [] : [.. changes],
            Metadata   = metadata
        }, ct);
    }

    /// <summary>Whether <paramref name="other"/> addresses the same entity through the same manager.</summary>
    /// <param name="other">The subject to compare with.</param>
    /// <returns><c>true</c> when both address the same entity.</returns>
    public bool Equals(AuditSubject other) =>
        ReferenceEquals(_manager, other._manager)
        && string.Equals(EntityName, other.EntityName, StringComparison.Ordinal)
        && string.Equals(EntityId, other.EntityId, StringComparison.Ordinal)
        // The type is part of the identity because it changes what gets recorded: two subjects on
        // the same address, one with a type and one without, do not behave the same.
        && EntityType == other.EntityType;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is AuditSubject other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_manager, EntityName, EntityId, EntityType);

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
