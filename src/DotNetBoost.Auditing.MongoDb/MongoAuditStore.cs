using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using MongoDB.Driver;

namespace DotNetBoost.Auditing.MongoDb;

/// <summary>
/// Keeps entries in one MongoDB collection, one document each, with the property changes embedded.
/// </summary>
/// <remarks>
/// <b>This store writes when it is called</b>, like the Dapper one and unlike the EF Core one: there
/// is no change tracker to join, so recording is explicit and the insert happens there and then.
/// <para>
/// <b>An entry is always complete.</b> Because the changes are embedded, one insert writes the whole
/// entry — there is no window in which a trail holds an entry whose property rows have not arrived,
/// which the relational stores need a transaction to avoid.
/// </para>
/// <para>
/// <b>Atomicity with your own work is a different question.</b> It needs a multi-document
/// transaction, which MongoDB offers only on a replica set and through a session: start one, hand it
/// to <see cref="WithSession"/>, and the entry commits with the rest of your work. Without a
/// session the entry is committed by itself and a failure immediately afterwards leaves a trail of
/// a change that was rolled back.
/// </para>
/// <para>
/// <b>Redaction needs your entities declared.</b> Everything here is addressed by name and key, so
/// the engine has no type to read <c>[AuditRedact]</c> from unless the application calls
/// <c>DeclareEntitiesFrom(assembly)</c> at <c>Build()</c> — exactly as in a Dapper application.
/// </para>
/// </remarks>
/// <param name="database">The database holding the collection.</param>
/// <param name="options">Collection naming.</param>
/// <param name="session">The session to run inside, if there is one.</param>
public sealed class MongoAuditStore(
    IMongoDatabase database,
    MongoDbOptions options,
    IClientSessionHandle? session = null) : IAuditStore
{
    private readonly IMongoDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly MongoDbOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    private IMongoCollection<AuditEntryDocument> Entries =>
        _database.GetCollection<AuditEntryDocument>(_options.EntriesCollection);

    /// <summary>
    /// The same store, running inside <paramref name="ambient"/>, so an entry commits with the rest
    /// of your work:
    /// <code>
    /// using var session = await client.StartSessionAsync(ct);
    /// session.StartTransaction();
    /// var store = new MongoAuditStore(database, options).WithSession(session);
    /// // … write your document and record the change …
    /// await session.CommitTransactionAsync(ct);
    /// </code>
    /// <para>
    /// Transactions need a replica set. A standalone <c>mongod</c> refuses to start one, which is
    /// why this is opt-in rather than something the store arranges for you.
    /// </para>
    /// </summary>
    /// <param name="ambient">The session to run inside.</param>
    /// <returns>A store that issues its operations in that session.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ambient"/> is <c>null</c>.</exception>
    public MongoAuditStore WithSession(IClientSessionHandle ambient)
    {
        ArgumentNullException.ThrowIfNull(ambient);
        return new MongoAuditStore(_database, _options, ambient);
    }

    /// <inheritdoc/>
    public Task AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var document = AuditEntryDocument.From(entry);

        return session is null
            ? Entries.InsertOneAsync(document, cancellationToken: ct)
            : Entries.InsertOneAsync(session, document, cancellationToken: ct);
    }

    /// <inheritdoc/>
    public Task AppendRangeAsync(IReadOnlyCollection<AuditEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
            return Task.CompletedTask;

        var documents = entries.Select(AuditEntryDocument.From).ToList();

        return session is null
            ? Entries.InsertManyAsync(documents, cancellationToken: ct)
            : Entries.InsertManyAsync(session, documents, cancellationToken: ct);
    }

    /// <inheritdoc/>
    public async Task<AuditEntry?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var filter = Builders<AuditEntryDocument>.Filter.Eq(d => d.Id, id);

        var found = session is null
            ? await Entries.Find(filter).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            : await Entries.Find(session, filter).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return found?.ToEntry();
    }

    /// <inheritdoc/>
    public async Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var filter = Filter(query);

        long? total = null;

        if (query.IncludeTotal)
        {
            // CountDocuments rather than EstimatedDocumentCount: the estimate ignores the filter,
            // and a total that does not match the filter it was asked about is worse than none.
            total = session is null
                ? await Entries.CountDocumentsAsync(filter, cancellationToken: ct).ConfigureAwait(false)
                : await Entries.CountDocumentsAsync(session, filter, cancellationToken: ct).ConfigureAwait(false);
        }

        var find = session is null ? Entries.Find(filter) : Entries.Find(session, filter);

        find = find
            // Newest first, with the id as the tiebreaker — the same ordering the other two stores
            // use, so paging is stable when several entries share a timestamp. It matters more here:
            // a BSON date holds milliseconds, so entries share one more often than in SQL.
            .Sort(Builders<AuditEntryDocument>.Sort
                .Descending(d => d.OccurredAt)
                .Descending(d => d.Id))
            .Skip(query.Skip < 0 ? 0 : query.Skip)
            .Limit(query.Take);

        if (!query.IncludeChanges)
        {
            // Excluded server-side rather than dropped after reading: the property changes are most
            // of the bytes in an entry, and the point of asking for a page without them is not to
            // transfer them.
            find = find.Project<AuditEntryDocument>(
                Builders<AuditEntryDocument>.Projection.Exclude(d => d.Changes));
        }

        var documents = await find.ToListAsync(ct).ConfigureAwait(false);

        return new AuditPage([.. documents.Select(d => d.ToEntry())], total);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteOlderThanAsync(
        DateTime cutoff,
        AuditPruneScope scope,
        int batchSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var builder = Builders<AuditEntryDocument>.Filter;
        var filter  = builder.Lt(d => d.OccurredAt, Utc(cutoff));

        if (scope.EntityName is { } only)
            filter &= builder.Eq(d => d.EntityName, only);

        if (scope.ExcludeEntityNames.Count > 0)
        {
            // The entities with a retention of their own, pruned by their own pass. Without this a
            // longer per-entity retention would be deleted at the shorter global cutoff.
            filter &= builder.Nin(d => d.EntityName, scope.ExcludeEntityNames);
        }

        // The ids first, oldest first, one batch at a time. A single unbounded delete over a year of
        // entries is a long-running write against the collection that recording needs.
        var find = (session is null ? Entries.Find(filter) : Entries.Find(session, filter))
            .Sort(Builders<AuditEntryDocument>.Sort.Ascending(d => d.OccurredAt).Ascending(d => d.Id))
            .Limit(batchSize)
            .Project<AuditEntryDocument>(Builders<AuditEntryDocument>.Projection.Include(d => d.Id));

        var ids = (await find.ToListAsync(ct).ConfigureAwait(false)).Select(d => d.Id).ToList();

        if (ids.Count == 0)
            return 0;

        // No second delete for the property changes: they are inside the documents being removed.
        return await DeleteAsync(builder.In(d => d.Id, ids), ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<int> DeleteForEntityAsync(string entityName, string entityId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        var builder = Builders<AuditEntryDocument>.Filter;

        return DeleteAsync(
            builder.Eq(d => d.EntityName, entityName) & builder.Eq(d => d.EntityId, entityId),
            ct);
    }

    /// <inheritdoc/>
    public Task<int> DeleteForActorAsync(string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        return DeleteAsync(Builders<AuditEntryDocument>.Filter.Eq(d => d.Actor, actor), ct);
    }

    private async Task<int> DeleteAsync(FilterDefinition<AuditEntryDocument> filter, CancellationToken ct)
    {
        var result = session is null
            ? await Entries.DeleteManyAsync(filter, ct).ConfigureAwait(false)
            : await Entries.DeleteManyAsync(session, filter, cancellationToken: ct).ConfigureAwait(false);

        // Clamped rather than cast: the contract returns an int, and a prune of more than two
        // billion entries in one batch should come back as "a great many" and not as a negative
        // number. Unreachable with any sane batch size; a silent sign flip would not be.
        return (int)Math.Min(result.DeletedCount, int.MaxValue);
    }

    /// <summary>The filter for a query. Internal so its rendering can be asserted without a server.</summary>
    internal static FilterDefinition<AuditEntryDocument> Filter(AuditQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var builder = Builders<AuditEntryDocument>.Filter;
        var filter  = builder.Empty;

        if (query.EntityName is { } entityName)
            filter &= builder.Eq(d => d.EntityName, entityName);

        if (query.EntityId is { } entityId)
            filter &= builder.Eq(d => d.EntityId, entityId);

        if (query.Action is { } action)
            filter &= builder.Eq(d => d.Action, action);

        if (query.Actor is { } actor)
            filter &= builder.Eq(d => d.Actor, actor);

        // Deliberately separate from Actor: "any actor" and "no actor was captured" are different
        // questions. The field is left out of the document when it was not captured, and an equality
        // against null matches a missing field as well as an explicit null — so this asks the right
        // thing either way.
        if (query.ActorMissing)
            filter &= builder.Eq(d => d.Actor, null);

        if (query.CorrelationId is { } correlationId)
            filter &= builder.Eq(d => d.CorrelationId, correlationId);

        if (query.From is { } from)
            filter &= builder.Gte(d => d.OccurredAt, Utc(from));

        // Exclusive, so consecutive windows do not double-count.
        if (query.To is { } to)
            filter &= builder.Lt(d => d.OccurredAt, Utc(to));

        if (query.After is { } cursor)
        {
            // Everything strictly after the cursor in the sort this store issues: newest first, so
            // "after" is older, and at the same instant a smaller id.
            var at = Utc(cursor.OccurredAt);

            filter &= builder.Or(
                builder.Lt(d => d.OccurredAt, at),
                builder.And(builder.Eq(d => d.OccurredAt, at), builder.Lt(d => d.Id, cursor.Id)));
        }

        return filter;
    }

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc   => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _                  => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
