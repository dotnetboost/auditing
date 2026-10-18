namespace DotNetBoost.Auditing.Core.Metadata;

/// <summary>
/// The entity types an application has declared, by stored entity name.
/// <para>
/// Two jobs that both need the same thing — a way from a <b>name</b> back to a <b>type</b>:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Rules for a write addressed by hand.</b> <c>audit.For("Product", "42")</c> has no type to
/// read <c>[AuditIgnore]</c> and <c>[AuditRedact]</c> from, and a Dapper or MongoDB application
/// addresses everything that way. Declaring the entity lets the engine apply the model's rules to a
/// write it could otherwise only take at face value — which matters most for the one rule whose
/// failure is silent and permanent: redaction.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Retention.</b> A prune needs every entity that has a retention of its own, and a reflection
/// cache only knows the types it has already been handed.
/// </description>
/// </item>
/// </list>
/// <para>
/// Declared at <c>Build()</c> from the assemblies or types an application names — the same shape
/// <c>DotNetBoost.Media</c> declares its roles with, and for the same reason: half the ways of
/// recording have no type to read an attribute from.
/// </para>
/// </summary>
public sealed class AuditModelRegistry
{
    private readonly IReadOnlyDictionary<string, Type> _entityTypes;
    private readonly IReadOnlyDictionary<string, TimeSpan> _retentions;

    /// <summary>Creates a registry over <paramref name="entityTypes"/>, keyed by stored entity name.</summary>
    /// <param name="entityTypes">The declared types.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entityTypes"/> is <c>null</c>.</exception>
    public AuditModelRegistry(IReadOnlyDictionary<string, Type> entityTypes)
    {
        ArgumentNullException.ThrowIfNull(entityTypes);
        _entityTypes = entityTypes;

        var retentions = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

        foreach (var (name, type) in entityTypes)
        {
            if (AuditPlan.For(type).Retention is { } retention)
                retentions[name] = retention;
        }

        _retentions = retentions;
    }

    /// <summary>
    /// A registry with nothing declared. What an engine built without declarations uses, so that a
    /// prune falls back to the configured retention for everything rather than failing to resolve,
    /// and a write addressed by hand is recorded rather than refused.
    /// </summary>
    public static AuditModelRegistry Empty { get; } =
        new(new Dictionary<string, Type>(StringComparer.Ordinal));

    /// <summary>The declared entity names and their types.</summary>
    public IReadOnlyDictionary<string, Type> EntityTypes => _entityTypes;

    /// <summary>
    /// The declared entities that carry their own <c>[AuditRetention]</c>, and for how long. What a
    /// prune iterates, and what the global sweep has to exclude.
    /// </summary>
    public IReadOnlyDictionary<string, TimeSpan> Retentions => _retentions;

    /// <summary>Whether anything was declared at all.</summary>
    public bool IsEmpty => _entityTypes.Count == 0;

    /// <summary>
    /// The plan for the entity stored under <paramref name="entityName"/>, or <c>null</c> when
    /// nothing was declared under that name.
    /// </summary>
    /// <param name="entityName">The stored entity name.</param>
    /// <returns>Its plan, or <c>null</c>.</returns>
    public AuditPlan? PlanFor(string entityName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        return _entityTypes.TryGetValue(entityName, out var type) ? AuditPlan.For(type) : null;
    }
}
