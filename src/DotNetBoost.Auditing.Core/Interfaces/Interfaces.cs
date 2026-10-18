namespace DotNetBoost.Auditing.Core.Interfaces;

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
