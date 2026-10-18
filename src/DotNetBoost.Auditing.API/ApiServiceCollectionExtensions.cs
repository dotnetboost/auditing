using DotNetBoost.Auditing.API;
using DotNetBoost.Auditing.Core;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Builder methods that need ASP.NET Core.</summary>
public static class AuditingApiBuilderExtensions
{
    /// <summary>
    /// Captures the signed-in user of the current request as the actor, so the trail records who
    /// made a change instead of leaving it unknown. Also registers <c>IHttpContextAccessor</c>,
    /// which it needs.
    /// <code>
    /// services.AddAuditing()
    ///         .UseEntityFrameworkCore&lt;AppDbContext&gt;()
    ///         .UseHttpContextActor()
    ///         .Build();
    /// </code>
    /// <para>
    /// Without this the default accessor returns <c>null</c> and every entry's actor is honestly
    /// unknown. This is the one line that turns a change log into an audit trail.
    /// </para>
    /// </summary>
    /// <param name="builder">The auditing builder.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
    public static AuditingBuilder UseHttpContextActor(this AuditingBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHttpContextAccessor();
        return builder.UseActorAccessor<HttpContextAuditActorAccessor>();
    }
}
