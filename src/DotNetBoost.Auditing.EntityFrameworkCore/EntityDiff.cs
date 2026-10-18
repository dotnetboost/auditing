using DotNetBoost.Auditing.Core.Metadata;
using DotNetBoost.Auditing.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DotNetBoost.Auditing.EntityFrameworkCore;

/// <summary>
/// Turns a tracked entry into the change list the engine records. Reads the change tracker and
/// nothing else — the rules about what may be recorded live in the engine, so that every capture
/// source is held to the same ones.
/// </summary>
internal static class EntityDiff
{
    /// <summary>The action an entry's state means, or <c>null</c> for a state that is not a change.</summary>
    internal static AuditAction? ActionFor(EntityState state) => state switch
    {
        EntityState.Added    => AuditAction.Created,
        EntityState.Modified => AuditAction.Updated,
        EntityState.Deleted  => AuditAction.Deleted,
        _                    => null
    };

    /// <summary>
    /// Whether this entry is something the library can address at all.
    /// </summary>
    /// <remarks>
    /// Three exclusions, each for a different reason:
    /// <list type="bullet">
    /// <item><description>
    /// <b>The audit tables themselves.</b> Recording the trail would produce a trail of the trail,
    /// without end.
    /// </description></item>
    /// <item><description>
    /// <b>Keyless entity types.</b> There is no key, so there is no address to file the history
    /// under.
    /// </description></item>
    /// <item><description>
    /// <b>Owned types.</b> An owned entity shares its owner's key, so its entries would be filed
    /// under the owner's address but the owned type's name — a history split between two names for
    /// one row. Recording them under the owner instead means merging two entries into one, which
    /// the capture cannot do without knowing which owner property reached it. So an owned type's
    /// changes are <b>not recorded</b>, and that is a documented limitation rather than a quiet
    /// omission.
    /// </description></item>
    /// </list>
    /// </remarks>
    internal static bool IsAddressable(EntityEntry entry) =>
        entry.Entity is not (AuditEntry or AuditPropertyChange)
        && !entry.Metadata.IsOwned()
        && entry.Metadata.FindPrimaryKey() is not null;

    /// <summary>
    /// The properties that moved, as the engine's own change type.
    /// </summary>
    /// <remarks>
    /// The primary key is left out. It is already the entry's address, so recording it again as a
    /// property change would repeat it on every creation and deletion — and EF cannot change a key
    /// in place anyway, so there is no update in which it is news.
    /// </remarks>
    internal static List<AuditChange> Build(EntityEntry entry, AuditAction action, bool recordBlindUpdates)
    {
        var changes = new List<AuditChange>();
        var key     = entry.Metadata.FindPrimaryKey();

        // An update with nothing to diff against. Decided once per entry rather than per property,
        // because the signal is a property of the whole entry.
        var blind = action == AuditAction.Updated && HasNoBeforeImage(entry);

        if (blind && !recordBlindUpdates)
            return changes;

        foreach (var property in entry.Properties)
        {
            if (key is not null && key.Properties.Contains(property.Metadata))
                continue;

            switch (action)
            {
                case AuditAction.Created:
                    changes.Add(new AuditChange(property.Metadata.Name, null, property.CurrentValue));
                    break;

                case AuditAction.Deleted:
                    // The original values: for a deleted row the current ones are on their way out
                    // and the before-image is the whole point.
                    changes.Add(new AuditChange(property.Metadata.Name, property.OriginalValue, null));
                    break;

                case AuditAction.Updated when blind:
                    // No old value to report, and saying so is the honest answer: null means "no
                    // previous value was recorded", which is exactly the situation.
                    if (property.IsModified)
                        changes.Add(new AuditChange(property.Metadata.Name, null, property.CurrentValue));

                    break;

                case AuditAction.Updated:
                    if (HasReallyChanged(property))
                        changes.Add(new AuditChange(property.Metadata.Name, property.OriginalValue, property.CurrentValue));

                    break;

                default:
                    break;
            }
        }

        return changes;
    }

    /// <summary>
    /// Whether this update arrived with no before-image: every property marked modified, and every
    /// original value equal to its current one.
    /// </summary>
    /// <remarks>
    /// That is the fingerprint of <c>Update(entity)</c> or <c>State = Modified</c> on an entity the
    /// context never loaded — EF fabricates the original values from the current ones, so the diff
    /// is empty however carefully it is taken.
    /// <para>
    /// It cannot be told apart from a fully-marked entity that genuinely did not change, because
    /// from the change tracker the two are the same observation. A loaded entity whose property was
    /// set to a new value never looks like this: that property's original differs.
    /// </para>
    /// </remarks>
    private static bool HasNoBeforeImage(EntityEntry entry)
    {
        var any = false;
        var key = entry.Metadata.FindPrimaryKey();

        foreach (var property in entry.Properties)
        {
            // The key is never marked modified — EF does not update keys in place — so including it
            // here would make every entry look as though it had a before-image.
            if (key is not null && key.Properties.Contains(property.Metadata))
                continue;

            if (!property.IsModified)
                return false;

            if (HasReallyChanged(property))
                return false;

            any = true;
        }

        return any;
    }

    /// <summary>
    /// Whether a property marked modified actually holds a different value.
    /// </summary>
    /// <remarks>
    /// <c>IsModified</c> alone is not enough: <c>Update(entity)</c> and an attached-then-saved graph
    /// mark <b>every</b> property modified, which would fill the trail with properties that did not
    /// move and bury the one that did. The model's own value comparer does the comparison where
    /// there is one, so a <c>byte[]</c> is compared by content rather than by reference — otherwise
    /// an untouched array reads as changed on every save.
    /// </remarks>
    private static bool HasReallyChanged(PropertyEntry property)
    {
        if (!property.IsModified)
            return false;

        var original = property.OriginalValue;
        var current  = property.CurrentValue;

        var comparer = property.Metadata.GetValueComparer();

        return comparer is not null
            ? !comparer.Equals(original, current)
            : !Equals(original, current);
    }
}

/// <summary>
/// Key reading over a tracked entry, which is the only place a shadow key exists.
/// </summary>
internal static class TrackedKeys
{
    /// <summary>
    /// The entry's key as its stored string, or <c>null</c> when the key has not been generated yet
    /// — an <c>Added</c> entity before its INSERT has run. That <c>null</c> is what makes the
    /// deferred pass necessary.
    /// </summary>
    /// <exception cref="InvalidOperationException">The entity type has a composite primary key.</exception>
    internal static string? TryGetKey(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey()
            ?? throw new InvalidOperationException(
                $"{entry.Metadata.DisplayName()} has no primary key, so it cannot be addressed.");

        if (key.Properties.Count > 1)
        {
            throw new InvalidOperationException(
                $"{entry.Metadata.DisplayName()} has a composite primary key " +
                $"({string.Join(", ", key.Properties.Select(p => p.Name))}). An audit entry records a " +
                "single key. Give the entity a surrogate key, exclude it with [AuditIgnore], or record " +
                "against it by hand with audit.For(name, id).");
        }

        var property = entry.Property(key.Properties[0].Name);

        // EF does not leave a store-generated key at its CLR default while the entity is Added: it
        // assigns a TEMPORARY value — a large negative int — so the graph can be wired up before the
        // INSERT returns the real one. EntityKey.IsUnset cannot see that (-2147482611 is not 0), so
        // asking it alone would record the temporary value as the entity's address and file the
        // creation under an id that exists nowhere and is never looked up again.
        //
        // IsTemporary is the precise signal; IsKeySet is EF's own answer to "is this key meaningful
        // yet", and both are cheap. The CLR-default check stays for a key that is neither.
        if (property.IsTemporary || !entry.IsKeySet || EntityKey.IsUnset(property.CurrentValue))
            return null;

        return EntityKey.ToKeyString(property.CurrentValue!);
    }
}
