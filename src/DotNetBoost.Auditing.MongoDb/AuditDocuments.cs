using DotNetBoost.Auditing.Core.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DotNetBoost.Auditing.MongoDb;

/// <summary>
/// One recorded change, as it is stored: the entry with its property changes embedded.
/// </summary>
/// <remarks>
/// <b>Why a document type of its own rather than mapping the Core models.</b> Two reasons, and the
/// second is the one that decides it:
/// <list type="bullet">
/// <item><description>
/// <see cref="AuditEntry.Changes"/> has no setter — it is a read-only collection, which is what the
/// EF Core store and the analyzers want — and the BSON serializer deserializes by assigning the
/// member. Mapping it would mean either a backing-field map, which breaks when the compiler renames
/// one, or a setter on a Core model added for one store's benefit.
/// </description></item>
/// <item><description>
/// A document type of this package's own can carry its serialization on attributes, so this store
/// registers <b>no global class map at all</b>. The driver's map registry is static and
/// process-wide: a library that registered one for a Core type would be deciding how every other
/// piece of code in the application serializes that type.
/// </description></item>
/// </list>
/// </remarks>
internal sealed class AuditEntryDocument
{
    /// <summary>
    /// The entry's own id as the document <c>_id</c>: one unique index instead of two, and a lookup
    /// by id needs no secondary index at all.
    /// </summary>
    /// <remarks>
    /// The representation is stated rather than left to the driver's default. Without it a GUID
    /// goes in as the legacy binary subtype, which other drivers and the shell read as a different
    /// value — and an audit trail whose ids change meaning when read by another tool is not much of
    /// a record.
    /// </remarks>
    [BsonId]
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid Id { get; set; }

    public string EntityName { get; set; } = string.Empty;

    public string EntityId { get; set; } = string.Empty;

    /// <summary>
    /// Stored as its <b>name</b>. The relational stores keep an integer, which is what their index
    /// wants; a document is read by people as often as by code, and a renumbering cannot change what
    /// a stored document meant.
    /// </summary>
    [BsonRepresentation(BsonType.String)]
    public AuditAction Action { get; set; }

    /// <summary>
    /// Left out of the document when it was not captured, rather than stored as an explicit null.
    /// A query for a missing field matches both, so <c>ActorMissing</c> still works.
    /// </summary>
    [BsonIgnoreIfNull]
    public string? Actor { get; set; }

    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// When it happened, always UTC.
    /// </summary>
    /// <remarks>
    /// <b>BSON dates hold milliseconds.</b> The relational stores keep 100-nanosecond ticks, so a
    /// timestamp that round-trips through this store comes back truncated to the millisecond. It
    /// does not affect ordering — the entry id is the tiebreaker — but two trails of the same
    /// events, one here and one in SQL, will not compare equal to the tick.
    /// </remarks>
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime OccurredAt { get; set; }

    [BsonIgnoreIfNull]
    public string? Metadata { get; set; }

    /// <summary>
    /// The properties that moved, embedded. Always written, as an empty array when there are none:
    /// a missing field and an empty one read differently in an aggregation, and a reader should not
    /// have to know which it is.
    /// </summary>
    public List<AuditChangeDocument> Changes { get; set; } = [];

    /// <summary>The document for an entry.</summary>
    internal static AuditEntryDocument From(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new AuditEntryDocument
        {
            Id            = entry.Id,
            EntityName    = entry.EntityName,
            EntityId      = entry.EntityId,
            Action        = entry.Action,
            Actor         = entry.Actor,
            CorrelationId = entry.CorrelationId,
            OccurredAt    = Utc(entry.OccurredAt),
            Metadata      = entry.Metadata,
            Changes       = [.. entry.Changes.Select(AuditChangeDocument.From)]
        };
    }

    /// <summary>The model for a document.</summary>
    internal AuditEntry ToEntry()
    {
        var entry = new AuditEntry
        {
            Id            = Id,
            EntityName    = EntityName,
            EntityId      = EntityId,
            Action        = Action,
            Actor         = Actor,
            CorrelationId = CorrelationId,
            // The driver returns Kind.Utc for a BSON date, so this is belt and braces — and it is
            // what keeps this store's answer identical to the other two, which have to repair a
            // Kind their providers lose.
            OccurredAt    = Utc(OccurredAt),
            Metadata      = Metadata
        };

        foreach (var change in Changes)
            entry.Changes.Add(change.ToChange(Id));

        return entry;
    }

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc   => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _                  => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}

/// <summary>One property's change, embedded in its entry.</summary>
/// <remarks>
/// There is no <c>AuditEntryId</c> on the document: the entry it belongs to is the document it is
/// inside, and storing the parent's id in every element would be a foreign key to itself. It is
/// filled back in when the model is built, so a caller sees the same
/// <see cref="AuditPropertyChange"/> whichever store produced it.
/// </remarks>
internal sealed class AuditChangeDocument
{
    public string PropertyName { get; set; } = string.Empty;

    [BsonIgnoreIfNull]
    public string? OldValue { get; set; }

    [BsonIgnoreIfNull]
    public string? NewValue { get; set; }

    public bool IsRedacted { get; set; }

    public bool IsTruncated { get; set; }

    internal static AuditChangeDocument From(AuditPropertyChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        return new AuditChangeDocument
        {
            PropertyName = change.PropertyName,
            OldValue     = change.OldValue,
            NewValue     = change.NewValue,
            IsRedacted   = change.IsRedacted,
            IsTruncated  = change.IsTruncated
        };
    }

    internal AuditPropertyChange ToChange(Guid entryId) => new()
    {
        AuditEntryId = entryId,
        PropertyName = PropertyName,
        OldValue     = OldValue,
        NewValue     = NewValue,
        IsRedacted   = IsRedacted,
        IsTruncated  = IsTruncated
    };
}
