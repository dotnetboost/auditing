namespace DotNetBoost.Auditing.Core.Models;

/// <summary>
/// Why the engine deliberately did not record a change, as reported to
/// <see cref="Interfaces.IAuditObserver"/>.
/// </summary>
/// <remarks>
/// Every value here is correct behaviour rather than a failure — a failure throws. They are
/// reported because <b>a trail that quietly stops covering a table looks exactly like a table
/// nobody changed</b>, and an audit trail is the one thing that cannot be backfilled once the
/// window has passed. Two of them are usually a decision and three of them are usually a mistake,
/// which is what the levels in the logging observer are chosen from.
/// </remarks>
public enum AuditSuppression
{
    /// <summary>
    /// <see cref="AuditingOptions.Enabled"/> is off — the kill switch. Expected while it is on
    /// purpose, and worth noticing if nobody remembers turning it on.
    /// </summary>
    RecordingDisabled = 0,

    /// <summary>
    /// The entity is <c>[AuditIgnore]</c>. A decision, and the one suppression that is reported
    /// most often: a high-churn ignored entity reports this on every save.
    /// </summary>
    EntityIgnored = 1,

    /// <summary>
    /// <see cref="AuditingOptions.RequireOptIn"/> is on and the entity is not <c>[Audited]</c>.
    /// Also a decision — this is what opt-in mode is for — but the usual way an operator discovers
    /// opt-in is on at all.
    /// </summary>
    NotOptedIn = 2,

    /// <summary>
    /// <see cref="AuditingOptions.RequireOptIn"/> is on and <b>nothing is declared</b> under the
    /// entity's name, so there is nothing to prove it opted in. Opt-in is an allow-list and an
    /// entity nothing is known about is not on it.
    /// <para>
    /// Nearly always a missing <c>DeclareEntitiesFrom(assembly)</c> rather than an intention: the
    /// entity looks audited in the source, carries <c>[Audited]</c>, and records nothing.
    /// </para>
    /// </summary>
    EntityNotDeclared = 3,

    /// <summary>
    /// An <see cref="AuditAction.Updated"/> write with nothing left to say — either no properties
    /// were reported, or every reported property was <c>[AuditIgnore]</c>.
    /// <para>
    /// The second case is the one worth seeing: an entity whose only changing property is ignored
    /// would otherwise produce an endless trail of entries that record that something changed and
    /// never what.
    /// </para>
    /// </summary>
    NothingChanged = 4
}
