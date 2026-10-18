using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Metadata;

namespace DotNetBoost.Auditing.Core.Services;

/// <summary>
/// Resolves an entity instance to the <c>(name, key)</c> pair every verb in the library is
/// addressed by. One implementation, so recording, reading and erasing cannot disagree about which
/// rows belong to an entity.
/// </summary>
internal static class AuditAddress
{
    /// <summary>
    /// <paramref name="entity"/>'s stored name and key.
    /// </summary>
    /// <exception cref="InvalidOperationException">The key has not been assigned yet.</exception>
    internal static (string Name, string Id) Of(object entity, IEntityKeyAccessor keys)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(keys);

        var name = AuditEntityNames.Of(entity);
        var id   = keys.GetKey(entity);

        if (id is null)
        {
            // Recording under the unassigned key's string form would file this entity's history
            // under "0" together with every other unsaved entity of its type — and because nothing
            // ever rewrites an audit entry, those rows would stay wrong forever.
            throw new InvalidOperationException(
                $"'{name}' has no key assigned yet, so it has no address. Save it first, or address it " +
                "by audit.For(name, id) with a key you compose yourself. (An EF Core application does " +
                "not hit this: the interceptor records after the save, when the key exists.)");
        }

        return (name, id);
    }
}
