using DotNetBoost.Auditing.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace DotNetBoost.Auditing.API;

/// <summary>
/// Reports the signed-in user of the current request as <see cref="Core.Models.AuditEntry.Actor"/>.
/// Register with <c>.UseHttpContextActor()</c>.
/// </summary>
/// <remarks>
/// Prefers <see cref="ClaimTypes.NameIdentifier"/> over <c>Identity.Name</c>: the subject identifier
/// is stable, while a display name can be changed by the person it names — which is the wrong
/// property for a record of who-did-what to be keyed on. A trail that says "Bob Smith" and a trail
/// that says which account acted are not the same record, and only the second survives a rename.
/// <para>
/// Returns <c>null</c> outside a request, for an unauthenticated one, and when neither value is
/// present. All three are real cases — a background job, a webhook, a migration — and <c>null</c>
/// means "not captured" rather than standing in for a user.
/// </para>
/// </remarks>
/// <param name="httpContextAccessor">Supplies the ambient request, if there is one.</param>
public sealed class HttpContextAuditActorAccessor(IHttpContextAccessor httpContextAccessor)
    : IAuditActorAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor =
        httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));

    /// <inheritdoc/>
    public string? GetActor()
    {
        var user = _httpContextAccessor.HttpContext?.User;

        if (user?.Identity is not { IsAuthenticated: true })
            return null;

        var subject = user.FindFirstValue(ClaimTypes.NameIdentifier);

        return string.IsNullOrWhiteSpace(subject) ? NullIfBlank(user.Identity.Name) : subject;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
