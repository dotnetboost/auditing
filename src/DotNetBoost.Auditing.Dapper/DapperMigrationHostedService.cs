using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Data;

namespace DotNetBoost.Auditing.Dapper;

/// <summary>
/// Creates the audit tables as the host starts, when <c>UseDapper(..., migrateSchema: true)</c>
/// asked for it. Runs once, before the application begins serving — a change recorded first would
/// fail on a table that does not exist yet.
/// </summary>
internal sealed partial class DapperMigrationHostedService(
    IServiceProvider services,
    DapperOptions options,
    ILogger logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Its own scope: the host has no scope of its own, and the connection is scoped.
        using var scope = services.CreateScope();
        var connection  = scope.ServiceProvider.GetRequiredService<IDbConnection>();

        try
        {
            await DapperSchema.EnsureCreatedAsync(connection, options, cancellationToken).ConfigureAwait(false);
            LogCreated(logger, options.EntriesTable);
        }
        catch (Exception ex)
        {
            // Logged and rethrown: the host's own startup failure message says little about which
            // component failed, and a missing audit table is worth naming.
            LogFailed(logger, options.EntriesTable, ex);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 7300, Level = LogLevel.Debug, Message = "Audit table '{Table}' is present.")]
    private static partial void LogCreated(ILogger logger, string table);

    [LoggerMessage(EventId = 7301, Level = LogLevel.Error,
        Message = "Could not create the audit table '{Table}'. The application cannot record changes.")]
    private static partial void LogFailed(ILogger logger, string table, Exception exception);
}
