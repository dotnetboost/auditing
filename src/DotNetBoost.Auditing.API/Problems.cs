using Microsoft.AspNetCore.Http;
using System.Globalization;

namespace DotNetBoost.Auditing.API;

/// <summary>
/// Turns the engine's argument failures into <c>400</c> with a ProblemDetails body, so a bad query
/// string reads as the client's mistake rather than the server's.
/// </summary>
/// <remarks>
/// Deliberately narrow. Only the exceptions that can only mean "the request was wrong" are mapped:
/// <list type="bullet">
/// <item><description><see cref="ArgumentException"/> — an empty or over-long addressing value.</description></item>
/// <item><description><see cref="ArgumentOutOfRangeException"/> — a prune age or batch size that is zero or negative.</description></item>
/// <item><description><see cref="NotSupportedException"/> — an entity whose key cannot be addressed.</description></item>
/// </list>
/// Everything else is left to the application's own handler. An
/// <see cref="InvalidOperationException"/> from this library usually means a misconfiguration — an
/// actor accessor returning something too long for the column, a store that is not registered — and
/// reporting that as the caller's fault would hide a fault the operator needs to see as a 500.
/// </remarks>
internal sealed class AuditProblemFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        try
        {
            return await next(context).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException error)
        {
            return BadRequest(error.Message, error.ParamName);
        }
        catch (ArgumentException error)
        {
            return BadRequest(error.Message, error.ParamName);
        }
        catch (NotSupportedException error)
        {
            return BadRequest(error.Message, parameter: null);
        }
    }

    private static IResult BadRequest(string detail, string? parameter)
    {
        var extensions = parameter is { Length: > 0 }
            ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["parameter"] = parameter }
            : null;

        return Results.Problem(
            // The exception messages in this library name the limit and the way out, which is the
            // useful half of a 400. They describe the request, never the stored data.
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: "The audit request could not be served.",
            type: string.Create(CultureInfo.InvariantCulture, $"https://httpstatuses.io/400"),
            extensions: extensions);
    }
}
