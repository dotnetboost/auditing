using System.Collections.Concurrent;
using System.Reflection;

namespace DotNetBoost.Auditing.Core.Metadata;

/// <summary>One property of an audited entity, as the model sees it.</summary>
/// <param name="Name">
/// The property name <b>as named on the class</b> — which is exactly how
/// <see cref="Models.AuditPropertyChange.PropertyName"/> spells it, so a client can match a
/// description to a recorded change without guessing at a casing convention.
/// </param>
/// <param name="ClrType">The property's type name — the underlying type for a <see cref="Nullable{T}"/>.</param>
/// <param name="IsNullable">Whether the property is a <see cref="Nullable{T}"/>.</param>
/// <param name="Rule">What the model does with it: record it, redact it, or ignore it.</param>
public sealed record AuditPropertyInfo(
    string Name,
    string ClrType,
    bool IsNullable,
    AuditPropertyRule Rule);

/// <summary>
/// Describes the shape of an audited entity — the properties the engine may record, and the rule
/// each one falls under.
/// </summary>
/// <remarks>
/// This is also where the rule for <em>which</em> properties count lives, so the description and
/// the engine cannot drift apart: <see cref="AuditPlan"/> reads its attributes from the same
/// <see cref="AuditableProperties"/>. A description that listed a property the engine never looks
/// at — or omitted one it redacts — would be worse than no description.
/// </remarks>
public static class AuditSchema
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<AuditPropertyInfo>> Descriptions = new();

    /// <summary>
    /// The properties of <paramref name="entityType"/> the model has anything to say about: public
    /// instance properties that are not indexers.
    /// </summary>
    /// <param name="entityType">The entity type.</param>
    /// <returns>Its properties.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entityType"/> is <c>null</c>.</exception>
    public static IEnumerable<PropertyInfo> AuditableProperties(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        return entityType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static p => p.GetIndexParameters().Length == 0);
    }

    /// <summary>
    /// Describes every auditable property of <paramref name="entityType"/>. Cached per type — it is
    /// a fact about the class, not about what has been recorded.
    /// </summary>
    /// <param name="entityType">The entity type.</param>
    /// <returns>Its properties and their rules.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entityType"/> is <c>null</c>.</exception>
    public static IReadOnlyList<AuditPropertyInfo> Describe(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        return Descriptions.GetOrAdd(entityType, static t => Build(t));
    }

    private static IReadOnlyList<AuditPropertyInfo> Build(Type entityType)
    {
        // Through the plan rather than off the attributes again: the plan is what the recorder
        // consults, and it is the half that throws on a property carrying both [AuditIgnore] and
        // [AuditRedact]. Reading the attributes a second time here would be a second opinion.
        var plan = AuditPlan.For(entityType);

        return
        [
            .. AuditableProperties(entityType).Select(p =>
            {
                var underlying = Nullable.GetUnderlyingType(p.PropertyType);

                return new AuditPropertyInfo(
                    Name:       p.Name,
                    ClrType:    (underlying ?? p.PropertyType).Name,
                    IsNullable: underlying is not null,
                    Rule:       plan.RuleFor(p.Name));
            })
        ];
    }
}
