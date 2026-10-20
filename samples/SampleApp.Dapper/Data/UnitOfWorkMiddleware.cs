namespace SampleApp.Dapper.Data;

/// <summary>
/// Commits <see cref="UnitOfWork"/> when a request has succeeded, and leaves it alone when it has
/// not — an uncommitted transaction is rolled back as the request scope disposes.
/// <para>
/// Middleware rather than an endpoint filter, deliberately: it has to cover the library's own
/// <c>MapAuditEndpoints()</c> as well as this sample's endpoints. A <c>POST /api/audit/prune</c>
/// would otherwise delete entries inside a transaction nobody commits, report how many it deleted,
/// and leave them all in place.
/// </para>
/// </summary>
/// <param name="next">The rest of the pipeline.</param>
public sealed class UnitOfWorkMiddleware(RequestDelegate next)
{
    /// <summary>Runs the rest of the pipeline, then commits if it produced a success.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // An exception thrown downstream propagates through here, so the commit is skipped and the
        // scope rolls the transaction back. UseExceptionHandler is registered upstream of this, so
        // a failure it turns into ProblemDetails arrives back here as a 4xx/5xx instead — also not
        // a commit. That is what POST /api/products/{id}/fail relies on.
        await next(context);

        if (context.Response.StatusCode < 400)
        {
            await context.RequestServices.GetRequiredService<UnitOfWork>().CommitAsync(context.RequestAborted);
        }
    }
}
