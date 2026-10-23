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

    /// <summary>
    /// Logs what the engine decides not to record, so that a trail which has stopped covering
    /// something says so somewhere.
    /// <code>
    /// services.AddAuditing()
    ///         .UseEntityFrameworkCore&lt;AppDbContext&gt;()
    ///         .UseHttpContextActor()
    ///         .UseLoggingObserver()
    ///         .Build();
    /// </code>
    /// <para>
    /// Without it the default observer does nothing, and a change that was deliberately not
    /// recorded leaves no trace at all — which is indistinguishable from a change nobody made. The
    /// two suppressions that are usually a misconfiguration are logged as warnings and the rest at
    /// Debug; <see cref="LoggingAuditObserver"/> says which and why.
    /// </para>
    /// </summary>
    /// <param name="builder">The auditing builder.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
    /// <remarks>
    /// Lives in this package rather than in Core because the logging dependency does.
    /// <c>DotNetBoost.Auditing.Core</c> has one NuGet reference and keeps it; an application that
    /// is not an ASP.NET one registers its own <c>IAuditObserver</c> with <c>UseObserver&lt;T&gt;()</c>.
    /// </remarks>
    public static AuditingBuilder UseLoggingObserver(this AuditingBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseObserver<LoggingAuditObserver>();
    }
}
