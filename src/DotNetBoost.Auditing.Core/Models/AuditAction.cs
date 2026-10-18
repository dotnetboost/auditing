namespace DotNetBoost.Auditing.Core.Models;

/// <summary>What an <see cref="AuditEntry"/> says happened to the entity.</summary>
public enum AuditAction
{
    /// <summary>
    /// The entity came into existence. <see cref="AuditEntry.Changes"/> carries its initial values
    /// as <c>NewValue</c> with <c>OldValue</c> <c>null</c>, unless
    /// <see cref="AuditingOptions.RecordCreatedValues"/> is turned off.
    /// </summary>
    Created = 0,

    /// <summary>
    /// The entity changed. <see cref="AuditEntry.Changes"/> carries one entry per property that
    /// actually changed; a write that changed nothing records no entry at all.
    /// </summary>
    Updated = 1,

    /// <summary>
    /// The entity was deleted. <see cref="AuditEntry.Changes"/> carries its final values as
    /// <c>OldValue</c> with <c>NewValue</c> <c>null</c>, unless
    /// <see cref="AuditingOptions.RecordDeletedValues"/> is turned off.
    /// <para>
    /// This is the most valuable row in the table, and the one a foreign key would have taken away:
    /// nothing cascades to the trail, so a deleted entity's history outlives it.
    /// </para>
    /// </summary>
    Deleted = 2
}
