namespace DotNetBoost.Auditing.Core.Models;

/// <summary>
/// One property's change as a caller reports it, before the engine formats it.
/// <para>
/// The values are <c>object?</c> rather than strings on purpose: formatting them is where culture
/// bugs and oversized values come from, so it happens once, in the engine, under
/// <see cref="System.Globalization.CultureInfo.InvariantCulture"/> and
/// <see cref="AuditingOptions.MaxValueLength"/> — never in each capture source.
/// </para>
/// </summary>
/// <param name="PropertyName">The property that changed, as named on the class.</param>
/// <param name="OldValue">Its value before the change, or <c>null</c>.</param>
/// <param name="NewValue">Its value after the change, or <c>null</c>.</param>
public sealed record AuditChange(string PropertyName, object? OldValue, object? NewValue);

/// <summary>
/// A change to record. The engine fills in everything it owns — the id, the actor, the correlation
/// id and the timestamp — so a caller reports only what happened.
/// </summary>
public sealed record AuditWrite
{
    /// <summary>
    /// The entity's stored name. Set it through <c>IAuditManager.For(entity)</c> rather than by
    /// hand where there is a type to read it from: that applies
    /// <see cref="Metadata.AuditEntityNames"/>, which unwraps proxies and honours
    /// <c>[AuditEntity]</c>.
    /// </summary>
    public required string EntityName { get; init; }

    /// <summary>The entity's key as its stored string.</summary>
    public required string EntityId { get; init; }

    /// <summary>
    /// The entity's CLR type, when the caller has one. Set by <c>IAuditManager.For(entity)</c>;
    /// <c>null</c> for a write addressed by name and key alone.
    /// <para>
    /// It is how the engine finds the model's <c>[AuditIgnore]</c> and <c>[AuditRedact]</c> rules.
    /// Without it the engine falls back to the declared types in
    /// <see cref="Metadata.AuditModelRegistry"/>, and with neither there are <b>no model rules to
    /// apply</b> — a redacted property reported by name would be recorded in full. An application
    /// that addresses entities by hand and keeps secrets in them should declare its entities at
    /// <c>Build()</c>.
    /// </para>
    /// <para>
    /// Never stored. The row records the entity's name, which is the stable address; a type name is
    /// not, and an assembly-qualified one even less so.
    /// </para>
    /// </summary>
    public Type? EntityType { get; init; }

    /// <summary>What happened to the entity.</summary>
    public required AuditAction Action { get; init; }

    /// <summary>
    /// One entry per property that changed. May be empty: a <see cref="AuditAction.Deleted"/>
    /// entry often is.
    /// <para>
    /// Properties the model ignores or redacts are filtered and blanked by the engine, not by the
    /// caller — so every capture source gets the same rules, and a new one cannot forget them.
    /// </para>
    /// </summary>
    public IReadOnlyList<AuditChange> Changes { get; init; } = [];

    /// <summary>
    /// Who made the change, when the caller knows better than the ambient accessor — a background
    /// job acting for a user, an import naming its source file. <c>null</c> (the default) asks the
    /// registered <see cref="Interfaces.IAuditActorAccessor"/>, which is the normal path.
    /// </summary>
    public string? Actor { get; init; }

    /// <summary>
    /// Ties this entry to the others of the same edit. <c>null</c> (the default) takes the ambient
    /// <see cref="System.Diagnostics.Activity"/> id, or a fresh value when there is no activity.
    /// <para>
    /// A capture source that knows the boundary of an edit should set it, because the ambient
    /// default does not always find one: outside a request there is no activity, so each entry would
    /// get an id of its own and nothing would identify them as one save — and inside a request there
    /// is exactly one activity, so two separate saves would share an id and look like one. The EF
    /// Core interceptor therefore stamps one value per <c>SaveChanges</c>.
    /// </para>
    /// <para>
    /// Over-long values are cut to the column rather than refused: a correlation id is a diagnostic
    /// aid, and failing a business write over one would be the wrong trade.
    /// </para>
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>Application-supplied context as JSON, stored as given. See <see cref="AuditEntry.Metadata"/>.</summary>
    public string? Metadata { get; init; }
}
