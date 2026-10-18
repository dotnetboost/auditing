using DotNetBoost.Auditing.Core.Attributes;
using System.Collections.Concurrent;
using System.Reflection;

namespace DotNetBoost.Auditing.Core.Metadata;

/// <summary>What the model says about one property.</summary>
public enum AuditPropertyRule
{
    /// <summary>Recorded with its values. The default for a property with no attribute.</summary>
    Record = 0,

    /// <summary>Recorded as changed, with both values withheld — <c>[AuditRedact]</c>.</summary>
    Redact = 1,

    /// <summary>Not recorded at all, not even its name — <c>[AuditIgnore]</c>.</summary>
    Ignore = 2
}

/// <summary>
/// What one entity type's attributes say about auditing it: the stored name, whether it is excluded
/// or opted in, its retention, and the rule for each property that carries an attribute.
/// <para>
/// Attribute <b>facts</b> only — no policy. Whether <see cref="OptedIn"/> matters at all depends on
/// <see cref="AuditingOptions.RequireOptIn"/>, which is configuration and can differ between two
/// hosts reading the same assembly, so the engine combines the two rather than the cache baking one
/// into the other.
/// </para>
/// </summary>
public sealed class AuditPlan
{
    private readonly IReadOnlyDictionary<string, AuditPropertyRule> _rules;

    private AuditPlan(
        string entityName,
        bool ignored,
        bool optedIn,
        TimeSpan? retention,
        IReadOnlyDictionary<string, AuditPropertyRule> rules)
    {
        EntityName = entityName;
        Ignored    = ignored;
        OptedIn    = optedIn;
        Retention  = retention;
        _rules     = rules;
    }

    /// <summary>The name entries are recorded under.</summary>
    public string EntityName { get; }

    /// <summary>Whether the class carries <c>[AuditIgnore]</c> and is never recorded.</summary>
    public bool Ignored { get; }

    /// <summary>Whether the class carries <c>[Audited]</c>, which only matters under <see cref="AuditingOptions.RequireOptIn"/>.</summary>
    public bool OptedIn { get; }

    /// <summary>Its own retention from <c>[AuditRetention]</c>, or <c>null</c> to use the configured default.</summary>
    public TimeSpan? Retention { get; }

    /// <summary>Whether any property is ignored or redacted — the cheap check before filtering a change list.</summary>
    public bool HasPropertyRules => _rules.Count > 0;

    /// <summary>
    /// The rule for <paramref name="propertyName"/>.
    /// </summary>
    /// <param name="propertyName">The property, as named on the class.</param>
    /// <returns>Its rule, or <see cref="AuditPropertyRule.Record"/> for a property with no attribute.</returns>
    /// <remarks>
    /// A name that is not a property of the type reads as <see cref="AuditPropertyRule.Record"/>.
    /// That is the honest answer rather than an error: a caller may be reporting a shadow property,
    /// a column with no CLR property behind it, or a computed field, and none of those can carry an
    /// attribute to be read. It does mean a <b>misspelled</b> property name is recorded rather than
    /// rejected — which is the price of letting a store report what the model cannot see.
    /// </remarks>
    public AuditPropertyRule RuleFor(string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        return _rules.TryGetValue(propertyName, out var rule) ? rule : AuditPropertyRule.Record;
    }

    private static readonly ConcurrentDictionary<Type, AuditPlan> Cache = new();

    /// <summary>
    /// The plan for <paramref name="type"/>, read once and cached. Reflection over every property
    /// on every recorded change would be the library's dominant cost.
    /// </summary>
    /// <param name="type">The entity type.</param>
    /// <returns>Its plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is <c>null</c>.</exception>
    public static AuditPlan For(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Cache.GetOrAdd(type, static t => Build(t));
    }

    /// <summary>The plan for <paramref name="entity"/>'s type.</summary>
    /// <param name="entity">The entity instance.</param>
    /// <returns>Its plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is <c>null</c>.</exception>
    public static AuditPlan Of(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return For(entity.GetType());
    }

    private static AuditPlan Build(Type type)
    {
        // The same unproxying AuditEntityNames does, and for the same reason: a change-tracking
        // proxy's attributes are the entity's, but its name is not.
        var entityName = AuditEntityNames.For(type);
        var declared   = type;

        while (declared.BaseType is { } parent && parent != typeof(object) && declared.Assembly.IsDynamic)
            declared = parent;

        var ignored = declared.GetCustomAttribute<AuditIgnoreAttribute>(inherit: true) is not null;
        var optedIn = declared.GetCustomAttribute<AuditedAttribute>(inherit: true) is not null;

        TimeSpan? retention = null;
        if (declared.GetCustomAttribute<AuditRetentionAttribute>(inherit: true) is { } attribute)
        {
            if (attribute.Days <= 0)
            {
                throw new InvalidOperationException(
                    $"[AuditRetention] on {declared.FullName} has Days = {attribute.Days}. A retention of " +
                    "nothing is [AuditIgnore], said more clearly — use that if the entity should not be " +
                    "audited, or give Days a positive value.");
            }

            retention = TimeSpan.FromDays(attribute.Days);
        }

        var rules = new Dictionary<string, AuditPropertyRule>(StringComparer.Ordinal);

        foreach (var property in declared.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            var isIgnored  = property.GetCustomAttribute<AuditIgnoreAttribute>(inherit: true) is not null;
            var isRedacted = property.GetCustomAttribute<AuditRedactAttribute>(inherit: true) is not null;

            if (isIgnored && isRedacted)
            {
                // Refused rather than resolved either way. Both attributes on one property is
                // someone saying two different things about a secret, and guessing which they meant
                // risks writing it down.
                throw new InvalidOperationException(
                    $"{declared.Name}.{property.Name} carries both [AuditIgnore] and [AuditRedact]. " +
                    "Ignore drops the property entirely; redact records that it changed without its " +
                    "values. Keep one.");
            }

            if (isIgnored)
                rules[property.Name] = AuditPropertyRule.Ignore;
            else if (isRedacted)
                rules[property.Name] = AuditPropertyRule.Redact;
        }

        return new AuditPlan(entityName, ignored, optedIn, retention, rules);
    }
}
