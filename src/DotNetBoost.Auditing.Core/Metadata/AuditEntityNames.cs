using DotNetBoost.Auditing.Core.Attributes;
using System.Collections.Concurrent;
using System.Reflection;

namespace DotNetBoost.Auditing.Core.Metadata;

/// <summary>
/// Resolves the name an entity type's entries are recorded under: its
/// <see cref="AuditEntityAttribute"/> if it has one, otherwise the type name. Cached per type — the
/// lookup walks attributes and unwraps proxies, and it happens on every recorded change.
/// </summary>
public static class AuditEntityNames
{
    /// <summary>The longest an entity name may be, matching the stored column.</summary>
    public const int MaxLength = 128;

    private static readonly ConcurrentDictionary<Type, string> Cache = new();

    /// <summary>The name <paramref name="type"/>'s entries are recorded under.</summary>
    /// <param name="type">The entity type.</param>
    /// <returns>Its stored entity name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">Its <c>[AuditEntity]</c> name is too long.</exception>
    public static string For(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Cache.GetOrAdd(type, static t => Resolve(t));
    }

    /// <summary>The name <paramref name="entity"/>'s entries are recorded under.</summary>
    /// <param name="entity">The entity instance.</param>
    /// <returns>Its stored entity name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is <c>null</c>.</exception>
    public static string Of(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return For(entity.GetType());
    }

    private static string Resolve(Type type)
    {
        var declared = Unproxy(type);
        var name     = declared.GetCustomAttribute<AuditEntityAttribute>(inherit: true)?.Name ?? declared.Name;

        if (name.Length > MaxLength)
        {
            throw new InvalidOperationException(
                $"Entity name '{name}' on {declared.FullName} is {name.Length} characters; the column holds " +
                $"{MaxLength}. Shorten it with [AuditEntity(\"…\")].");
        }

        return name;
    }

    /// <summary>
    /// The application's own type behind a runtime-generated one. EF Core's lazy-loading and
    /// change-tracking proxies subclass the entity, and their generated names would record a
    /// proxied instance's history under a name nothing else ever looks it up by.
    /// </summary>
    private static Type Unproxy(Type type)
    {
        // Castle's proxies — the ones EF Core uses — are named "<Entity>Proxy" and sit in a
        // dynamic assembly. Both conditions together, so a real application type that happens to
        // end in "Proxy" is left alone.
        while (type.BaseType is { } parent
               && parent != typeof(object)
               && type.Assembly.IsDynamic)
        {
            type = parent;
        }

        return type;
    }
}
