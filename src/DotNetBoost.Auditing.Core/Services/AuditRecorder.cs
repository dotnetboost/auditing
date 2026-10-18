using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Metadata;
using DotNetBoost.Auditing.Core.Models;
using System.Diagnostics;
using System.Globalization;

namespace DotNetBoost.Auditing.Core.Services;

/// <summary>
/// The engine behind <see cref="IAuditRecorder"/>: it decides whether a change is recorded, applies
/// the model's ignore and redaction rules, formats the values, stamps the actor, correlation and
/// time, and hands the entry to the store.
/// <para>
/// Every capture source goes through here — the explicit calls, the EF Core interceptor, anything
/// added later — so the rules cannot be implemented twice and a new source cannot forget
/// redaction.
/// </para>
/// </summary>
internal sealed class AuditRecorder(
    IAuditStore store,
    IAuditActorAccessor actors,
    IEntityKeyAccessor keys,
    AuditModelRegistry registry,
    AuditingOptions options,
    TimeProvider timeProvider) : IAuditRecorder
{
    private readonly IAuditStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IAuditActorAccessor _actors = actors ?? throw new ArgumentNullException(nameof(actors));
    private readonly IEntityKeyAccessor _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly AuditModelRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly AuditingOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc/>
    public AuditSubject For(object entity)
    {
        var (name, id) = AuditAddress.Of(entity, _keys);

        // The type travels with the subject. Resolving the address and then dropping the type would
        // cost every write through this subject its [AuditIgnore] and [AuditRedact] rules, silently.
        return new AuditSubject(this, name, id, entity.GetType());
    }

    /// <inheritdoc/>
    public AuditSubject For(string entityName, string entityId) => new(this, entityName, entityId);

    /// <inheritdoc/>
    public bool IsAudited(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        if (!_options.Enabled)
            return false;

        var plan = AuditPlan.For(entityType);
        return !plan.Ignored && (!_options.RequireOptIn || plan.OptedIn);
    }

    /// <inheritdoc/>
    public async Task<AuditEntry?> RecordAsync(AuditWrite write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);

        if (!_options.Enabled)
            return null;

        var entityName = Require(write.EntityName, nameof(write.EntityName), AuditEntityNames.MaxLength);
        var entityId   = Require(write.EntityId, nameof(write.EntityId), EntityKey.MaxLength);

        // The type when the caller had one, else whatever was declared under this name. With
        // neither there are no model rules to apply — see AuditWrite.EntityType.
        var plan = write.EntityType is { } type ? AuditPlan.For(type) : _registry.PlanFor(entityName);

        if (plan is not null)
        {
            if (plan.Ignored)
                return null;

            if (_options.RequireOptIn && !plan.OptedIn)
                return null;
        }
        else if (_options.RequireOptIn)
        {
            // Opt-in is an allow-list, and an entity nothing can be proven about is not on it.
            // Recording it anyway would make RequireOptIn mean "allow-list, except by the back
            // door", which is not a setting anyone would choose.
            return null;
        }

        var changes = Project(write, plan);

        // A write that changed nothing records nothing. This covers both an empty Updated write and
        // one whose every property was ignored — the second is the case worth being careful about,
        // because an entity whose only changing property is [AuditIgnore] would otherwise produce an
        // endless trail of entries that say a change happened and never which.
        if (write.Action == AuditAction.Updated && changes.Count == 0)
            return null;

        var entry = new AuditEntry
        {
            Id            = Guid.NewGuid(),
            EntityName    = entityName,
            EntityId      = entityId,
            Action        = write.Action,
            Actor         = ResolveActor(write.Actor),
            CorrelationId = Correlate(write.CorrelationId),
            OccurredAt    = _time.GetUtcNow().UtcDateTime,
            Metadata      = write.Metadata
        };

        foreach (var change in changes)
        {
            change.AuditEntryId = entry.Id;
            entry.Changes.Add(change);
        }

        await _store.AppendAsync(entry, ct).ConfigureAwait(false);
        return entry;
    }

    /// <summary>
    /// Applies the model's rules and the engine's formatting to the reported changes. Values that
    /// the action says are not kept — a creation's old values, a deletion's new ones — are dropped
    /// here rather than trusted from the caller, so the shape of a row does not depend on which
    /// capture source wrote it.
    /// </summary>
    private List<AuditPropertyChange> Project(AuditWrite write, AuditPlan? plan)
    {
        var keepValues = write.Action switch
        {
            AuditAction.Created => _options.RecordCreatedValues,
            AuditAction.Deleted => _options.RecordDeletedValues,
            _                   => true
        };

        if (!keepValues)
            return [];

        var projected = new List<AuditPropertyChange>(write.Changes.Count);

        foreach (var change in write.Changes)
        {
            if (change is null)
                continue;

            var propertyName = Require(
                change.PropertyName, $"{nameof(write.Changes)}.{nameof(AuditChange.PropertyName)}",
                AuditColumns.PropertyNameMaxLength);

            var rule = plan?.RuleFor(propertyName) ?? AuditPropertyRule.Record;

            if (rule == AuditPropertyRule.Ignore)
                continue;

            if (rule == AuditPropertyRule.Redact)
            {
                projected.Add(new AuditPropertyChange
                {
                    PropertyName = propertyName,
                    OldValue     = null,
                    NewValue     = null,
                    IsRedacted   = true
                });

                continue;
            }

            // A creation has no before-image and a deletion no after-image. Taking these from the
            // caller would let one source record a Created entry with old values and another not.
            var oldValue = write.Action == AuditAction.Created ? null : change.OldValue;
            var newValue = write.Action == AuditAction.Deleted ? null : change.NewValue;

            var (oldText, oldTruncated) = AuditValues.Format(oldValue, _options.MaxValueLength);
            var (newText, newTruncated) = AuditValues.Format(newValue, _options.MaxValueLength);

            projected.Add(new AuditPropertyChange
            {
                PropertyName = propertyName,
                OldValue     = oldText,
                NewValue     = newText,
                IsRedacted   = false,
                IsTruncated  = oldTruncated || newTruncated
            });
        }

        return projected;
    }

    /// <summary>
    /// The actor the caller named, else the ambient one. Over-long is refused rather than cut: an
    /// actor is identity, and half an identity is a different one.
    /// </summary>
    private string? ResolveActor(string? supplied)
    {
        var actor = supplied ?? _actors.GetActor();

        if (actor is null)
            return null;

        if (actor.Length > AuditColumns.ActorMaxLength)
        {
            throw new InvalidOperationException(
                $"The actor is {actor.Length.ToString(CultureInfo.InvariantCulture)} characters; the column " +
                $"holds {AuditColumns.ActorMaxLength.ToString(CultureInfo.InvariantCulture)}. Shorten what " +
                "the IAuditActorAccessor returns — a user id or a name, not a serialised principal.");
        }

        return actor;
    }

    /// <summary>
    /// The ambient trace id, so a trail joins to the traces of the request that caused it, else a
    /// fresh value.
    /// </summary>
    /// <remarks>
    /// Truncated rather than refused, unlike the actor: a correlation id is a diagnostic aid, and
    /// failing a business write because a legacy hierarchical <c>Activity</c> id grew past the
    /// column would be the library breaking the application over its own bookkeeping.
    /// </remarks>
    private static string Correlate(string? supplied)
    {
        // A capture source that knows where one edit ends wins over the ambient activity: outside a
        // request there is no activity at all, and inside one there is a single activity spanning
        // every save the request makes.
        var id = supplied is { Length: > 0 } given
            ? given
            : Activity.Current?.Id ?? Guid.NewGuid().ToString("n", CultureInfo.InvariantCulture);

        return id.Length <= AuditColumns.CorrelationIdMaxLength
            ? id
            : id[..AuditColumns.CorrelationIdMaxLength];
    }

    private static string Require(string value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{field} is required.", nameof(value));

        if (value.Length > maxLength)
        {
            throw new ArgumentException(
                $"{field} is {value.Length.ToString(CultureInfo.InvariantCulture)} characters; the column " +
                $"holds {maxLength.ToString(CultureInfo.InvariantCulture)}.", nameof(value));
        }

        return value;
    }
}
