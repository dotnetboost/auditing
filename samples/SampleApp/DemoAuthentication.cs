using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace SampleApp;

/// <summary>
/// Signs the caller in as whoever the <c>X-Demo-User</c> header names.
/// </summary>
/// <remarks>
/// <b>A stand-in, and nothing to copy.</b> It exists because the actor is the point of an audit
/// trail: without any authentication every entry's actor would be <c>null</c>, honestly, and the
/// sample would demonstrate a change log rather than an audit trail. A header is the smallest thing
/// that makes <c>UseHttpContextActor()</c> visible.
/// <para>
/// A real application has real authentication, and the library needs nothing from it beyond a
/// <see cref="ClaimTypes.NameIdentifier"/> claim.
/// </para>
/// </remarks>
public sealed class DemoAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>The scheme's name.</summary>
    public const string SchemeName = "Demo";

    /// <inheritdoc/>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Demo-User", out var header) || header.Count == 0)
        {
            // Not a failure: an unauthenticated request is allowed through and its entries record
            // no actor, which is what a background job or a webhook looks like.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var user = header[0]!;

        var identity = new ClaimsIdentity(
            [
                // The subject identifier is what the library reads. A display name can be changed
                // by the person it names, which is the wrong key for a record of who acted.
                new Claim(ClaimTypes.NameIdentifier, user),
                new Claim(ClaimTypes.Name, user)
            ],
            SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
