using DotNetBoost.Auditing.Core.Models;

namespace DotNetBoost.Auditing.Core.Interfaces;

/// <summary>
/// Told when the engine does something an application cannot otherwise find out about: a change it
/// deliberately did not record, and a change it recorded without the model's rules.
/// </summary>
/// <remarks>
/// <b>Why this exists.</b> Recording returns <c>null</c> to say "deliberately not recorded", which a
/// caller on the explicit path may check and the EF Core interceptor cannot — it writes entries for
/// changes nobody asked it about, so there is no return value anywhere for it to hand back. Without
/// this interface, an application that stops being audited — opt-in turned on, <c>[AuditIgnore]</c>
/// added to a base class, <c>DeclareEntitiesFrom</c> forgotten — carries on looking healthy. Logs
/// clean, health checks green, and the trail simply stops mentioning a table. That is discovered
/// months later by someone looking for a change that was never recorded, and an audit trail is the
/// one thing that cannot be backfilled.
/// <para>
/// The default registered by <c>AddAuditing()</c> does nothing, so this costs an application that
/// does not want it a virtual call per suppressed write. <c>DotNetBoost.Auditing.API</c> ships an
/// <c>ILogger</c>-backed one — <c>.UseLoggingObserver()</c> — which is why Core needs no logging
/// dependency to have something to say.
/// </para>
/// <para>
/// <b>Both methods must return quickly and must not throw.</b> They are called on the recording
/// path, synchronously, including from inside an EF Core <c>SaveChanges</c> — so there is nowhere
/// to await and nothing to catch an exception: one thrown here fails the business write that was
/// being audited, which is the library breaking an application over its own bookkeeping. Swallowing
/// it instead would need somewhere to report the swallow, which is the problem this interface was
/// added to solve. Keep implementations to logging, counters and the like.
/// </para>
/// </remarks>
public interface IAuditObserver
{
    /// <summary>
    /// A change the engine decided not to record, and why.
    /// </summary>
    /// <param name="reason">Which rule suppressed it.</param>
    /// <param name="entityName">The entity's stored name.</param>
    /// <param name="entityId">
    /// The entity's stored key, or empty when the suppression was decided before a key existed —
    /// an EF Core creation whose id arrives with the INSERT, skipped before it was ever addressed.
    /// </param>
    /// <param name="action">What would have been recorded.</param>
    void WriteSuppressed(AuditSuppression reason, string entityName, string entityId, AuditAction action);

    /// <summary>
    /// A change recorded with <b>no model rules applied</b>, because the write was addressed by
    /// name and nothing is declared under that name.
    /// </summary>
    /// <param name="entityName">The entity's stored name.</param>
    /// <param name="entityId">The entity's stored key.</param>
    /// <param name="action">What was recorded.</param>
    /// <remarks>
    /// The entry was written. What did not happen is <c>[AuditIgnore]</c> and <c>[AuditRedact]</c>
    /// — so <b>if that entity has a redacted property, its value is now in the table in full</b>.
    /// The engine cannot tell whether it does, because without a declared type there are no
    /// attributes to read; that is exactly why this is worth reporting rather than inferring.
    /// <para>
    /// Reachable only from the explicit path in a Dapper or MongoDB application that has not called
    /// <c>DeclareEntitiesFrom(assembly)</c>. The fix is that one line at <c>Build()</c>, and it is
    /// the only failure in this library that is both silent and permanent — the values cannot be
    /// unwritten.
    /// </para>
    /// </remarks>
    void RecordedWithoutModelRules(string entityName, string entityId, AuditAction action);
}

/// <summary>
/// Supplies who is making the current change, for <see cref="Models.AuditEntry.Actor"/>.
/// </summary>
/// <remarks>
/// The default registered by <c>AddAuditing()</c> returns <c>null</c>. ASP.NET applications
/// register the <c>HttpContext</c>-backed one from <c>DotNetBoost.Auditing.API</c> with
/// <c>.UseHttpContextActor()</c>.
/// <para>
/// This is the library's own accessor and depends on nothing. The suite deliberately has no shared
/// <c>ICurrentUser</c>: an application running two DotNetBoost libraries registers two accessors,
/// usually both one line over the same <c>HttpContext</c>, and in exchange neither library can
/// break the other's release.
/// </para>
/// </remarks>
public interface IAuditActorAccessor
{
    /// <summary>
    /// The current actor, or <c>null</c> when it cannot be determined. Returning a placeholder
    /// instead of <c>null</c> would turn "nobody captured this" into a claim about who acted, so
    /// do not — a trail that says <c>"system"</c> for every row is the trail this library's
    /// predecessor shipped, and it carried no information at all.
    /// </summary>
    /// <returns>The actor, or <c>null</c> when unknown.</returns>
    string? GetActor();
}

/// <summary>
/// Reads an entity's primary key, for <see cref="Models.AuditEntry.EntityId"/>.
/// </summary>
/// <remarks>
/// The default is <see cref="Services.ConventionEntityKeyAccessor"/>, which finds the key by
/// naming convention. The EF Core package replaces it with one that reads the actual model and is
/// therefore right about keys the convention cannot see — a shadow key, or one configured
/// fluently. Where the two disagree, the model wins.
/// </remarks>
public interface IEntityKeyAccessor
{
    /// <summary>
    /// <paramref name="entity"/>'s key as its stored string, or <c>null</c> when the key has not
    /// been assigned yet — an entity whose store-generated id arrives on save. A caller decides
    /// whether it can wait or has to fail; returning <c>null</c> is not an error here.
    /// </summary>
    /// <param name="entity">The entity to read.</param>
    /// <returns>The stored key, or <c>null</c> when it is not assigned yet.</returns>
    string? GetKey(object entity);
}
