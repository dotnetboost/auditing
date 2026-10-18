using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace DotNetBoost.Auditing.MongoDb;

/// <summary>
/// Creates the indexes the collection needs — the counterpart of
/// <c>DapperSchema.EnsureCreatedAsync</c> and EF Core's <c>ApplyAuditing</c>. Call it from a
/// deployment step or a test that builds the store directly rather than through
/// <c>UseMongoDb(...)</c>.
/// <para>
/// They mirror the relational stores' indexes, so the same questions are served the same way
/// whichever backend the application chose. Creating an index that is already there is a no-op, so
/// this is idempotent.
/// </para>
/// </summary>
public static class MongoAuditIndexes
{
    /// <summary>The index definitions, so they can be reviewed — or asserted — without a server.</summary>
    /// <param name="options">Collection naming. Unused today; taken for symmetry with the other stores and for when an option changes an index.</param>
    /// <returns>Every index this store needs.</returns>
    public static IReadOnlyList<CreateIndexModel<AuditEntryDocumentShape>> Definitions(MongoDbOptions? options = null)
    {
        _ = options;

        var keys = Builders<AuditEntryDocumentShape>.IndexKeys;

        return
        [
            // The main read: one entity's history, newest first. Leading with the address and ending
            // on the sort lets one index serve the filter and the ordering together.
            new CreateIndexModel<AuditEntryDocumentShape>(
                keys.Ascending(d => d.EntityName).Ascending(d => d.EntityId).Descending(d => d.OccurredAt),
                new CreateIndexOptions { Name = "entity_occurred" }),

            // "What did this person change, and when" — and the actor half of the erasure path.
            // Deliberately NOT sparse: the field is left out of the document when no actor was
            // captured, and a sparse index could not then serve the query for exactly those.
            new CreateIndexModel<AuditEntryDocumentShape>(
                keys.Ascending(d => d.Actor).Descending(d => d.OccurredAt),
                new CreateIndexOptions { Name = "actor_occurred" }),

            // Backs the prune, which scans by age across every entity.
            new CreateIndexModel<AuditEntryDocumentShape>(
                keys.Descending(d => d.OccurredAt),
                new CreateIndexOptions { Name = "occurred" }),

            // "The other halves of this edit", and the join to the traces of the request.
            new CreateIndexModel<AuditEntryDocumentShape>(
                keys.Ascending(d => d.CorrelationId),
                new CreateIndexOptions { Name = "correlation" }),

            // The multikey index over the embedded array, and the whole reason embedding costs
            // nothing here: "every change anyone ever made to Price" is an index seek rather than a
            // collection scan, which is the question the relational stores give a table of their own
            // to answer.
            new CreateIndexModel<AuditEntryDocumentShape>(
                keys.Ascending("Changes.PropertyName"),
                new CreateIndexOptions { Name = "change_property" })
        ];
    }

    /// <summary>Creates every index this store needs, if it is absent. Idempotent.</summary>
    /// <param name="database">The database holding the collection.</param>
    /// <param name="options">Collection naming. Defaults to <see cref="MongoDbOptions"/>'s own defaults.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>A task that completes when the indexes are present.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is <c>null</c>.</exception>
    public static async Task EnsureCreatedAsync(
        IMongoDatabase database,
        MongoDbOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        options ??= new MongoDbOptions();

        var collection = database.GetCollection<AuditEntryDocumentShape>(options.EntriesCollection);

        await collection.Indexes.CreateManyAsync(Definitions(options), ct).ConfigureAwait(false);
    }
}

/// <summary>
/// The stored document's field names, as a type the index definitions can be written against.
/// </summary>
/// <remarks>
/// Public only because <see cref="MongoAuditIndexes.Definitions"/> is: an index definition is
/// generic in the document it indexes, and the document type itself is internal — it exists so this
/// package can serialize without registering a global class map, and making it public would put a
/// storage shape in the package's API. This carries the field names and nothing else.
/// </remarks>
public sealed class AuditEntryDocumentShape
{
    /// <summary>The entity's stored name.</summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>The entity's stored key.</summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>Who made the change, or absent when it was not captured.</summary>
    public string? Actor { get; set; }

    /// <summary>Ties this entry to the others of the same edit.</summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>When it happened.</summary>
    public DateTime OccurredAt { get; set; }
}

/// <summary>
/// Creates the indexes as the host starts, when <see cref="MongoDbOptions.CreateIndexes"/> is on.
/// Runs once, before the application begins serving.
/// </summary>
internal sealed partial class MongoIndexHostedService(
    IServiceProvider services,
    MongoDbOptions options,
    ILogger logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Its own scope: the host has no scope of its own, and the database may be registered scoped
        // — one per tenant, say.
        using var scope = services.CreateScope();
        var database    = scope.ServiceProvider.GetRequiredService<IMongoDatabase>();

        try
        {
            await MongoAuditIndexes.EnsureCreatedAsync(database, options, cancellationToken).ConfigureAwait(false);
            LogCreated(logger, options.EntriesCollection);
        }
        catch (Exception ex)
        {
            // Logged and rethrown: a trail collected without its indexes works until it does not,
            // and the host's own startup message would not say which component failed.
            LogFailed(logger, options.EntriesCollection, ex);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 7400, Level = LogLevel.Debug, Message = "Audit indexes on '{Collection}' are present.")]
    private static partial void LogCreated(ILogger logger, string collection);

    [LoggerMessage(EventId = 7401, Level = LogLevel.Error,
        Message = "Could not create the audit indexes on '{Collection}'. Reading a trail would be a collection scan.")]
    private static partial void LogFailed(ILogger logger, string collection, Exception exception);
}
