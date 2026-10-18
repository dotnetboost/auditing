namespace DotNetBoost.Auditing.Core.Models;

/// <summary>
/// Which entities a prune applies to.
/// <para>
/// It exists for one reason, and the reason is not symmetry: an entity with a longer
/// <c>[AuditRetention]</c> than the global one must survive the global sweep. Without
/// <see cref="ExcludeEntityNames"/>, a global retention of 30 days and
/// <c>[AuditRetention(Days = 365)]</c> on <c>Order</c> would delete the orders' trail at 30 days —
/// the per-entity setting would be silently the opposite of what it says.
/// </para>
/// </summary>
public sealed record AuditPruneScope
{
    /// <summary>Only this entity's entries, or <c>null</c> for every entity.</summary>
    public string? EntityName { get; init; }

    /// <summary>
    /// Entity names to leave alone — the ones with a retention of their own, pruned by their own
    /// pass. Empty when <see cref="EntityName"/> names a single entity, since there is then nothing
    /// to exclude.
    /// </summary>
    public IReadOnlyCollection<string> ExcludeEntityNames { get; init; } = [];

    /// <summary>Every entity, excluding none.</summary>
    public static AuditPruneScope All { get; } = new();

    /// <summary>One entity.</summary>
    /// <param name="entityName">Its stored name.</param>
    /// <returns>A scope matching only that entity.</returns>
    public static AuditPruneScope For(string entityName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        return new AuditPruneScope { EntityName = entityName };
    }

    /// <summary>Every entity except the named ones.</summary>
    /// <param name="entityNames">The names to leave alone.</param>
    /// <returns>A scope matching everything else.</returns>
    public static AuditPruneScope Excluding(IReadOnlyCollection<string> entityNames)
    {
        ArgumentNullException.ThrowIfNull(entityNames);
        return new AuditPruneScope { ExcludeEntityNames = [.. entityNames] };
    }
}
