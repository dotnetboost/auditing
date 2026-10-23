using DotNetBoost.Auditing.Core.Metadata;
using DotNetBoost.Auditing.Core.Models;
using System.Collections.Concurrent;
using System.Reflection;

namespace DotNetBoost.Auditing.Core.Services;

/// <summary>
/// Builds a change list by comparing two instances of an entity, or by reading one — what the
/// explicit path has instead of a change tracker.
/// <code>
/// var changes = AuditDiff.Between(before, after);
///
/// if (changes.Count == 0)
///     return;                                   // nothing moved, so nothing to record
///
/// await audit.For(after).UpdatedAsync(changes, ct: ct);
/// </code>
/// <para>
/// A Dapper or MongoDB application has no change tracker, so every entry is written by hand — and
/// the part written by hand that is worth automating is the <b>comparison</b>, not the recording: a
/// four-line <c>if</c> chain per property is where a new property gets forgotten, silently, and the
/// trail simply stops mentioning it. The property list here comes from the type, so adding a
/// property to the entity adds it to the trail.
/// </para>
/// <para>
/// This is a convenience over the model, not a second set of rules. It reports what moved; whether
/// a moved property may be recorded, and in what form, is still decided in the engine, so a change
/// list built here and one built by hand are held to exactly the same
/// <c>[AuditIgnore]</c>/<c>[AuditRedact]</c> rules.
/// </para>
/// </summary>
/// <remarks>
/// <b>Which properties it compares.</b> The public instance properties that are readable and hold a
/// value type, a <see cref="string"/>, or a <see cref="byte"/> array — the same set
/// <see cref="AuditSchema.AuditableProperties"/> describes, less three kinds:
/// <list type="bullet">
/// <item><description>
/// <b>The key.</b> It is already the entry's address, and repeating it as a property change would
/// put it on every creation and deletion. The same key-discovery rule the addressing uses, so the
/// two agree about which property that is; for a composite key, all of its properties.
/// </description></item>
/// <item><description>
/// <b><c>[AuditIgnore]</c> properties.</b> The engine drops them anyway — this only saves the
/// comparison. The effect is worth knowing: an update whose only moved property is ignored produces
/// an <b>empty</b> list, which is the honest answer and lets a caller skip the write entirely.
/// </description></item>
/// <item><description>
/// <b>Everything that is not a simple value</b> — a navigation property, a collection, a nested
/// object. Comparing those by reference reports a change on every write, and a trail column is not
/// a place to put an object graph. They are <b>skipped silently</b>, which is the one thing to know
/// before reaching for this: an entity whose interesting state lives in a nested object needs its
/// changes reported by hand. <c>[AuditRedact]</c> properties <i>are</i> compared — the comparison
/// happens in memory and the engine withholds the values, so a redacted property is still recorded
/// as having changed.
/// </description></item>
/// </list>
/// <para>
/// <b>How it compares.</b> <see cref="object.Equals(object?, object?)"/>, except that two
/// <see cref="byte"/> arrays are compared by content — otherwise an untouched array reads as
/// changed every time, which is the same trap EF Core's value comparers exist to avoid.
/// </para>
/// </remarks>
public static class AuditDiff
{
    // Per type, because this is a fact about the class. Reflecting over every property of every
    // entity on every write would make the diff the most expensive part of recording.
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Comparable = new();

    /// <summary>
    /// The properties that moved between two states of one entity, as the engine's own change
    /// type — the list for an <see cref="AuditAction.Updated"/> write.
    /// </summary>
    /// <param name="before">The entity as it was. For a store with no change tracker this is a read, and it is the whole reason the read is worth its round trip.</param>
    /// <param name="after">The entity as it now is.</param>
    /// <returns>
    /// One change per moved property, empty when nothing moved. A fresh list, so a caller can
    /// append a change this cannot see — a column a stored procedure moved, a nested value.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="before"/> and <paramref name="after"/> are of different types.</exception>
    /// <remarks>
    /// An empty list is a result, not a failure: nothing changed. Recording it would write an entry
    /// that says a change happened and never which, so the engine refuses an
    /// <see cref="AuditAction.Updated"/> write of nothing — a caller that checks
    /// <c>Count == 0</c> itself saves the round trip as well.
    /// </remarks>
    public static List<AuditChange> Between(object before, object after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var type = after.GetType();

        if (before.GetType() != type)
        {
            // Not resolved to a common base type. A before-image and an after-image are two states
            // of ONE entity; two different types are two entities, and diffing them would report
            // every property of each as having moved.
            throw new ArgumentException(
                $"Cannot compare a {before.GetType().Name} with a {type.Name}: a before-image and an " +
                "after-image are two states of one entity.",
                nameof(before));
        }

        var changes = new List<AuditChange>();

        foreach (var property in PropertiesOf(type))
        {
            var old = property.GetValue(before);
            var now = property.GetValue(after);

            if (!AreEqual(old, now))
                changes.Add(new AuditChange(property.Name, old, now));
        }

        return changes;
    }

    /// <summary>
    /// Every comparable property of <paramref name="entity"/> as a <c>NewValue</c> — the list for
    /// an <see cref="AuditAction.Created"/> write, where there is no before-image because there was
    /// no entity.
    /// </summary>
    /// <param name="entity">The entity as it was created, with its key already assigned.</param>
    /// <returns>One change per property, each with a <c>null</c> <c>OldValue</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is <c>null</c>.</exception>
    /// <remarks>
    /// A property left at its default is included with both values <c>null</c>, which is what the
    /// EF Core interceptor records for the same entity — a creation's entry describes the whole
    /// row, and "this property was not set" is part of that description.
    /// </remarks>
    public static List<AuditChange> InitialValues(object entity) => Snapshot(entity, created: true);

    /// <summary>
    /// Every comparable property of <paramref name="entity"/> as an <c>OldValue</c> — the list for
    /// an <see cref="AuditAction.Deleted"/> write.
    /// </summary>
    /// <param name="entity">The entity as it was before it was deleted.</param>
    /// <returns>One change per property, each with a <c>null</c> <c>NewValue</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is <c>null</c>.</exception>
    /// <remarks>
    /// Worth the reflection: after the row is gone this entry is the only copy of what it said.
    /// </remarks>
    public static List<AuditChange> FinalValues(object entity) => Snapshot(entity, created: false);

    private static List<AuditChange> Snapshot(object entity, bool created)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var properties = PropertiesOf(entity.GetType());
        var changes    = new List<AuditChange>(properties.Length);

        foreach (var property in properties)
        {
            var value = property.GetValue(entity);

            changes.Add(created
                ? new AuditChange(property.Name, null, value)
                : new AuditChange(property.Name, value, null));
        }

        return changes;
    }

    private static PropertyInfo[] PropertiesOf(Type type) => Comparable.GetOrAdd(type, static t =>
    {
        var plan = AuditPlan.For(t);
        var keys = ConventionKeys.Of(t);

        return
        [
            .. AuditSchema.AuditableProperties(t)
                .Where(p => p.GetMethod is not null)
                .Where(p => IsSimpleValue(p.PropertyType))
                .Where(p => !keys.Any(k => string.Equals(k.Name, p.Name, StringComparison.Ordinal)))
                .Where(p => plan.RuleFor(p.Name) != AuditPropertyRule.Ignore)
        ];
    });

    /// <summary>
    /// Whether a property of this type holds one value a change can be stated about.
    /// </summary>
    /// <remarks>
    /// A value type (which covers <see cref="Nullable{T}"/>, enums, <see cref="decimal"/>, the date
    /// and time types, <see cref="Guid"/> and a strongly-typed id struct), a string, or a byte
    /// array — the types <c>AuditValues</c> has a stored form for. Everything else is a reference to
    /// something else, and the diff of a reference is a different question.
    /// </remarks>
    private static bool IsSimpleValue(Type type) =>
        type.IsValueType || type == typeof(string) || type == typeof(byte[]);

    private static bool AreEqual(object? left, object? right) => (left, right) switch
    {
        (null, null)         => true,
        (null, _) or (_, null) => false,
        // By content. Two arrays holding the same bytes are the same value in a trail, and an
        // entity read twice from a store holds two arrays.
        (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
        _                    => left.Equals(right)
    };
}
