using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace SampleApp.MongoDb.Data;

/// <summary>
/// Readiness, as <c>AddDbContextCheck&lt;AppDbContext&gt;()</c> is for the EF Core sample: without
/// it <c>/health</c> answers Healthy while MongoDB is unreachable, and the AppHost's
/// <c>WithHttpHealthCheck("/health")</c> would let the dashboard start anyway.
/// <para>
/// A ping rather than a package, so the sample needs nothing beyond the driver.
/// </para>
/// </summary>
/// <param name="database">The database to ping.</param>
public sealed class MongoHealthCheck(IMongoDatabase database) : IHealthCheck
{
    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await database.RunCommandAsync<BsonDocument>("{ ping: 1 }", cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (MongoException ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message, ex);
        }
    }
}
