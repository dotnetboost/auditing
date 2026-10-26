using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Metadata;
using DotNetBoost.Auditing.Core.Models;
using System.Diagnostics;
using System.Globalization;

namespace DotNetBoost.Auditing.Core.Services;

/// <summary>
/// The recording half of <see cref="Interfaces.IAuditManager"/>: it decides whether a change is
/// recorded, applies
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
    TimeProvider timeProvider,
    IAuditObserver observer,
    AuditMetrics metrics)
{
    private readonly IAuditStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IAuditActorAccessor _actors = actors ?? throw new ArgumentNullException(nameof(actors));
    private readonly IEntityKeyAccessor _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly AuditModelRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly AuditingOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly IAuditObserver _observer = observer ?? throw new ArgumentNullException(nameof(observer));
    private readonly AuditMetrics _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));

    /// <summary>
    /// <paramref name="entity"/>'s stored name and key. The manager turns this into an
    /// <see cref="AuditSubject"/> bound to itself; the engine cannot, because a subject records
    /// through the public interface and this class is not it.
    /// </summary>
    internal (string Name, string Id) Address(object entity) => AuditAddress.Of(entity, _keys);

    /// <summary>
    /// Whether <paramref name="entityType"/> is audited at all, by its attributes and the current
    /// options.
    /// </summary>
    /// <remarks>
    /// A pure question, and deliberately silent: the discovery endpoint asks it of every declared
    /// entity on every request, so reporting from here would describe an HTTP read as a suppressed
    /// write. A capture source that is about to skip an entity wants
    /// <see cref="SuppressionFor(Type)"/> instead, which gives the reason to report.
    /// </remarks>
    internal bool IsAudited(Type entityType) => SuppressionFor(entityType) is null;

    /// <summary>
    /// Why <paramref name="entityType"/> is not audited, or <c>null</c> when it is.
    /// </summary>
    /// <remarks>
    /// The same rules <see cref="RecordAsync"/> applies, in one place so a capture source
    /// that skips an entity early and the engine that would have skipped it late cannot disagree
    /// about why. Reporting is the caller's, because only the caller knows whether it is about to
    /// skip the entity or merely asking about it.
    /// </remarks>
    internal AuditSuppression? SuppressionFor(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        if (!_options.Enabled)
            return AuditSuppression.RecordingDisabled;

        var plan = AuditPlan.For(entityType);

        // The attribute and IgnoreEntity<T>() are one rule from two sources: the plan caches what
        // the type says, the options hold what this host was configured with.
        if (plan.Ignored || _options.IsIgnored(entityType))
            return AuditSuppression.EntityIgnored;

        if (_options.RequireOptIn && !plan.OptedIn)
            return AuditSuppression.NotOptedIn;

        return null;
    }

    /// <summary>
    /// Reports a suppression the caller is acting on — to the observer and to the counter. The seam
    /// a capture source uses, so that neither is reached separately by every source and a new source
    /// cannot report one of the two and forget the other.
    /// </summary>
    internal void ReportSuppressed(
        AuditSuppression reason,
        string entityName,
        string entityId,
        AuditAction action)
    {
        _observer.WriteSuppressed(reason, entityName, entityId, action);
        _metrics.Suppressed(reason, entityName, action);
    }

    /// <summary>Records one change, applying the model's rules and the engine's formatting.</summary>
    internal async Task<AuditEntry?> RecordAsync(AuditWrite write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);

        // Reported before the addressing is validated, because the kill switch is the one
        // suppression that does not depend on which entity this is — and an over-long name should
        // still throw rather than be swallowed by a disabled engine.
        if (!_options.Enabled)
            return Suppressed(AuditSuppression.RecordingDisabled, write.EntityName, write.EntityId, write.Action);

        var entityName = Require(write.EntityName, nameof(write.EntityName), AuditEntityNames.MaxLength);
        var entityId   = Require(write.EntityId, nameof(write.EntityId), EntityKey.MaxLength);

        // The type when the caller had one, else whatever was declared under this name. With
        // neither there are no model rules to apply — see AuditWrite.EntityType.
        var plan = write.EntityType is { } type ? AuditPlan.For(type) : _registry.PlanFor(entityName);

        // IgnoreEntity<T>(), ahead of everything that depends on a plan: an ignored type is never
        // declared (Build() refuses both), so a write addressed by name has no plan to find, and
        // without this it would be recorded — or, under opt-in, reported as undeclared — when the
        // application said plainly not to. By type when there is one, so a subclass is caught; by
        // name otherwise, which Build() has made unambiguous.
        if (write.EntityType is { } written ? _options.IsIgnored(written) : _options.IsIgnored(entityName))
            return Suppressed(AuditSuppression.EntityIgnored, entityName, entityId, write.Action);

        if (plan is not null)
        {
            if (plan.Ignored)
                return Suppressed(AuditSuppression.EntityIgnored, entityName, entityId, write.Action);

            if (_options.RequireOptIn && !plan.OptedIn)
                return Suppressed(AuditSuppression.NotOptedIn, entityName, entityId, write.Action);
        }
        else if (_options.RequireOptIn)
        {
            // Opt-in is an allow-list, and an entity nothing can be proven about is not on it.
            // Recording it anyway would make RequireOptIn mean "allow-list, except by the back
            // door", which is not a setting anyone would choose.
            return Suppressed(AuditSuppression.EntityNotDeclared, entityName, entityId, write.Action);
        }
        else if (write.EntityType is null)
        {
            // Recorded, but with no [AuditIgnore] or [AuditRedact] to apply — so a redacted
            // property of this entity has just been written in full. Reported rather than inferred:
            // with nothing declared there are no attributes to read, so the engine cannot tell
            // whether this entity has one. See IAuditObserver.RecordedWithoutModelRules.
            _observer.RecordedWithoutModelRules(entityName, entityId, write.Action);
        }

        var changes = Project(write, plan);

        // A write that changed nothing records nothing. This covers both an empty Updated write and
        // one whose every property was ignored — the second is the case worth being careful about,
        // because an entity whose only changing property is [AuditIgnore] would otherwise produce an
        // endless trail of entries that say a change happened and never which.
        if (write.Action == AuditAction.Updated && changes.Count == 0)
            return Suppressed(AuditSuppression.NothingChanged, entityName, entityId, write.Action);

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

        // Counted here, which is "handed to the store" and not "committed" — the EF Core store only
        // tracks an entry, and the caller's SaveChanges is what makes it durable. See
        // AuditingTelemetry.EntriesRecorded.
        _metrics.Recorded(entityName, write.Action);

        return entry;
    }

    /// <summary>
    /// Tells the observer, then returns the <c>null</c> that means "deliberately not recorded".
    /// </summary>
    /// <remarks>
    /// One helper rather than a report beside each <c>return null</c>, so that a suppression added
    /// later cannot return <c>null</c> without saying why — which is the failure this whole
    /// interface exists to prevent, and it would be a quiet one.
    /// </remarks>
    private AuditEntry? Suppressed(
        AuditSuppression reason,
        string entityName,
        string entityId,
        AuditAction action)
    {
        ReportSuppressed(reason, entityName, entityId, action);
        return null;
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

    /// <summary>
    /// <paramref name="value"/> if it is present and fits, else the exception that says which field
    /// was wrong.
    /// </summary>
    /// <remarks>
    /// <paramref name="field"/> is the <c>paramName</c>, not <c>nameof(value)</c>: the field is what
    /// the caller passed and <c>value</c> is only what this method happens to call it. The
    /// difference reaches a client — <c>AuditProblemFilter</c> puts <c>paramName</c> in the problem
    /// response, so the 400 for an over-long key said <c>"parameter": "value"</c> instead of naming
    /// <c>EntityId</c>.
    /// </remarks>
    private static string Require(string value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{field} is required.", field);

        if (value.Length > maxLength)
        {
            throw new ArgumentException(
                $"{field} is {value.Length.ToString(CultureInfo.InvariantCulture)} characters; the column " +
                $"holds {maxLength.ToString(CultureInfo.InvariantCulture)}.", field);
        }

        return value;
    }
}
