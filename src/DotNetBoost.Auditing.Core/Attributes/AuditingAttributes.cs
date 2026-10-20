namespace DotNetBoost.Auditing.Core.Attributes;

/// <summary>
/// Names the entity in <see cref="Models.AuditEntry.EntityName"/>, instead of using the type name.
/// Put it on an entity whose class you may rename, or on two unrelated types that would otherwise
/// share a name:
/// <code>
/// [AuditEntity("Product")]
/// public class ProductEntity { public int Id { get; set; } }
/// </code>
/// <para>
/// The stored name is what a trail is addressed by, so changing it later orphans every entry
/// already recorded under the old one — and unlike media, an audit entry is never rewritten, so
/// the old rows keep the old name forever. Pick it once.
/// </para>
/// <para>
/// Inherited, deliberately: a hierarchy audited under its base's name is one entity as far as its
/// history is concerned, which is what a TPH table already looks like.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class AuditEntityAttribute : Attribute
{
    /// <summary>Names the entity.</summary>
    /// <param name="name">1–128 characters. Validated when the type is first used.</param>
    public AuditEntityAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    /// <summary>The name entries are recorded under.</summary>
    public string Name { get; }
}

/// <summary>
/// Marks a class as audited. Only consulted when
/// <see cref="AuditingOptions.RequireOptIn"/> is set, which turns recording into an allow-list:
/// an entity without this attribute is not audited at all.
/// <para>
/// Off by default, because the useful default for an application that installs an auditing library
/// is that it audits. Turn it on when the trail should cover a named few tables rather than
/// everything the change tracker sees.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class AuditedAttribute : Attribute;

/// <summary>
/// Excludes something from the trail entirely.
/// <para>
/// On a <b>class</b>: the entity is never audited, whatever else is configured. On a
/// <b>property</b>: the property is never recorded — not its values and not the fact that it
/// changed. A change to only ignored properties produces no entry at all.
/// </para>
/// <para>
/// Use it for the noise that would otherwise swamp the trail — a <c>LastSeenAt</c> written on
/// every request, a denormalised counter — and <see cref="AuditRedactAttribute"/> rather than this
/// for a secret, so that the trail still shows a secret was rotated.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class AuditIgnoreAttribute : Attribute;

/// <summary>
/// Records that the property changed, without recording what it changed from or to:
/// <see cref="Models.AuditPropertyChange.IsRedacted"/> is set and both values are <c>null</c>.
/// <code>
/// public class User
/// {
///     public int Id { get; set; }
///     public string Email { get; set; } = "";
///     [AuditRedact] public string PasswordHash { get; set; } = "";
/// }
/// </code>
/// <para>
/// Redaction follows this attribute and nothing else — never whether the value happened to be
/// encrypted at rest — so it holds in an application with no encryption configured. A caller
/// cannot opt out of it, which is the point: an auditing library is the likeliest place in an
/// application for a secret to be written down in plain text and kept forever.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class AuditRedactAttribute : Attribute;

/// <summary>
/// How long this entity's entries are kept, overriding
/// <see cref="AuditingOptions.Retention"/> for one entity:
/// <code>
/// [AuditRetention(Days = 90)]
/// public class SearchQuery { public long Id { get; set; } }
/// </code>
/// <para>
/// An order's trail and a search query's trail are not kept for the same length of time, and a
/// single global retention forces the shorter one on both. Read by
/// <see cref="Interfaces.IAuditManager.PruneAsync(int, CancellationToken)"/>; nothing deletes
/// anything until a prune runs.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class AuditRetentionAttribute : Attribute
{
    /// <summary>
    /// Days to keep this entity's entries. Must be greater than zero; a retention of nothing is
    /// <see cref="AuditIgnoreAttribute"/>, said more clearly.
    /// </summary>
    public int Days { get; init; }
}
