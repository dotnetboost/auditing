using DotNetBoost.Auditing.Core.Interfaces;

namespace SampleApp;

/// <summary>
/// Records every change as <c>"system"</c>, because this sample has no users to name.
/// </summary>
/// <remarks>
/// <b>A stand-in, and nothing to copy.</b> The sample has no authentication at all, so there is no
/// signed-in user to read: <c>"system"</c> is the literal truth here — the API changed its own
/// catalogue and nobody asked it to.
/// <para>
/// In an application with users it would be a lie dressed as an answer, which is why the library
/// does not do this for you: its default accessor returns <c>null</c>, meaning "nobody captured
/// this", and <see cref="IAuditActorAccessor"/> says so at length. A constant actor is worse than
/// an absent one, because a reader cannot tell it apart from a real identity.
/// </para>
/// <para>
/// A real application registers <c>UseHttpContextActor()</c> from
/// <c>DotNetBoost.Auditing.API</c> instead, which reads the request's
/// <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/> claim. The library needs nothing
/// from authentication beyond that one claim.
/// </para>
/// </remarks>
public sealed class SystemAuditActorAccessor : IAuditActorAccessor
{
    /// <summary>The actor every entry in this sample is recorded against.</summary>
    public const string Actor = "system";

    /// <inheritdoc/>
    public string? GetActor() => Actor;
}
