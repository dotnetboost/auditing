using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Data;
using System.Data.Common;

namespace SampleApp.Dapper.Data;

/// <summary>
/// Readiness, as <c>AddDbContextCheck&lt;AppDbContext&gt;()</c> is for the EF Core sample: without
/// it <c>/health</c> answers Healthy while PostgreSQL is unreachable, and the AppHost's
/// <c>WithHttpHealthCheck("/health")</c> would let the dashboard start anyway.
/// <para>
/// A <c>SELECT 1</c> rather than a package, so the sample needs nothing beyond the driver. It takes
/// its own connection instead of the request-scoped one: a health probe should not join, or open,
/// the caller's unit of work — and resolving the scoped connection here would begin a transaction
/// for every probe.
/// </para>
/// </summary>
/// <param name="connections">Creates a connection of its own for each probe.</param>
public sealed class NpgsqlHealthCheck(Func<IDbConnection> connections) : IHealthCheck
{
    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = connections();

            if (connection is DbConnection db)
            {
                await db.OpenAsync(cancellationToken);
            }
            else
            {
                connection.Open();
            }

            await connection.ExecuteScalarAsync<int>(
                new CommandDefinition("SELECT 1", cancellationToken: cancellationToken));

            return HealthCheckResult.Healthy();
        }
        catch (DbException ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message, ex);
        }
    }
}
