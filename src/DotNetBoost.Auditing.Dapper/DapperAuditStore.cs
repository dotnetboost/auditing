using Dapper;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using System.Data;
using System.Globalization;

namespace DotNetBoost.Auditing.Dapper;

/// <summary>
/// Keeps entries in two tables through Dapper, for applications that do not use EF Core.
/// </summary>
/// <remarks>
/// <b>This store writes when it is called.</b> There is no change tracker to join, so
/// <see cref="AppendAsync"/> issues its INSERTs there and then — which is the opposite of the EF
/// Core store, where appending only tracks and the caller's <c>SaveChanges</c> commits.
/// <para>
/// So <b>atomicity is yours to arrange</b>: begin a transaction on the connection, hand it to the
/// store with <see cref="WithTransaction"/> (or register it in the scope), do your work, commit.
/// Without one, the entry commits by itself and a failure immediately afterwards leaves a trail of
/// a change that was rolled back. The EF Core store gets the guarantee for free; here it costs one
/// transaction.
/// </para>
/// <para>
/// <b>Redaction needs your entities declared.</b> Everything here is addressed by name and key, so
/// the engine has no type to read <c>[AuditRedact]</c> from unless the application calls
/// <c>DeclareEntitiesFrom(assembly)</c> at <c>Build()</c>. Undeclared, a redacted property is
/// recorded in full — which is why that call is not optional in a Dapper application.
/// </para>
/// </remarks>
/// <param name="connection">The application's connection. Borrowed, never owned.</param>
/// <param name="options">Table naming and the dialect.</param>
/// <param name="transaction">The ambient transaction to enlist in, if there is one.</param>
public sealed class DapperAuditStore(
    IDbConnection connection,
    DapperOptions options,
    IDbTransaction? transaction = null) : IAuditStore
{
    /// <summary>
    /// How many entries an erasure deletes per round trip.
    /// <para>
    /// Not configurable, and not taken from the prune's <c>batchSize</c>: the erasure verbs on
    /// <see cref="IAuditStore"/> deliberately have no batch parameter, because how a store keeps
    /// within its engine's limits is the store's business rather than the caller's. A thousand is
    /// the prune's default and is comfortably under the lowest ceiling the <c>IN</c> list has to
    /// clear — SQL Server's 2,100 parameters.
    /// </para>
    /// </summary>
    private const int EraseBatchSize = 1000;

    private readonly IDbConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    private readonly DapperOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    private AuditStatements Sql => AuditStatements.For(
        _options.Dialect ?? AuditStatements.DialectOf(_connection), _options);

    /// <summary>
    /// The same store, enlisted in <paramref name="ambient"/>, so a change and its trail commit
    /// together:
    /// <code>
    /// using var tx = connection.BeginTransaction();
    /// var store    = new DapperAuditStore(connection, options).WithTransaction(tx);
    /// // … write your row and record the change …
    /// tx.Commit();
    /// </code>
    /// </summary>
    /// <param name="ambient">The transaction to enlist in.</param>
    /// <returns>A store that issues its statements inside that transaction.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ambient"/> is <c>null</c>.</exception>
    public DapperAuditStore WithTransaction(IDbTransaction ambient)
    {
        ArgumentNullException.ThrowIfNull(ambient);
        return new DapperAuditStore(_connection, _options, ambient);
    }

    /// <inheritdoc/>
    public Task AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return AppendRangeAsync([entry], ct);
    }

    /// <inheritdoc/>
    public async Task AppendRangeAsync(IReadOnlyCollection<AuditEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
            return;

        var sql = Sql;

        var entryRows = entries
            .Select(e => new
            {
                e.Id,
                e.EntityName,
                e.EntityId,
                Action = (int)e.Action,
                e.Actor,
                e.CorrelationId,
                // Normalised here as well as in the engine. PostgreSQL's `timestamp with time zone`
                // refuses a DateTime that is not Kind.Utc, and a store that trusted its caller
                // would fail at the database rather than at the mistake.
                OccurredAt = AuditRows.Utc(e.OccurredAt),
                e.Metadata
            })
            .ToArray();

        var changeRows = entries
            .SelectMany(e => e.Changes.Select(c => new
            {
                AuditEntryId = e.Id,
                c.PropertyName,
                c.OldValue,
                c.NewValue,
                c.IsRedacted,
                c.IsTruncated
            }))
            .ToArray();

        await UnitOfWork.RunAsync(_connection, async () =>
        {
            // Dapper sends one statement per item in a single batch, so a save that changed a dozen
            // entities is two round trips rather than twenty-five.
            await _connection.ExecuteAsync(
                new CommandDefinition(sql.InsertEntry, entryRows, transaction, cancellationToken: ct))
                .ConfigureAwait(false);

            if (changeRows.Length > 0)
            {
                await _connection.ExecuteAsync(
                    new CommandDefinition(sql.InsertChange, changeRows, transaction, cancellationToken: ct))
                    .ConfigureAwait(false);
            }

            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<AuditEntry?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var sql = Sql;

        return await UnitOfWork.RunAsync(_connection, async () =>
        {
            var row = await _connection.QuerySingleOrDefaultAsync<AuditRows.EntryRow>(
                new CommandDefinition(sql.SelectEntryById, new { Id = id }, transaction, cancellationToken: ct))
                .ConfigureAwait(false);

            if (row is null)
                return null;

            var entry = AuditRows.ToEntry(row);
            await FillChangesAsync(sql, [entry], ct).ConfigureAwait(false);

            return entry;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var sql = Sql;
        var (where, parameters) = Filter(sql, query);

        return await UnitOfWork.RunAsync(_connection, async () =>
        {
            long? total = null;

            if (query.IncludeTotal)
            {
                total = await _connection.ExecuteScalarAsync<long>(
                    new CommandDefinition(
                        $"SELECT COUNT(*) FROM {sql.EntriesTable} {where}",
                        parameters,
                        transaction,
                        cancellationToken: ct))
                    .ConfigureAwait(false);
            }

            var take = query.Take;
            var skip = query.Skip < 0 ? 0 : query.Skip;

            var page = $"{sql.SelectEntries}{where} {sql.OrderBy}{AuditStatements.Limit(sql.Dialect, take, skip)}";

            var entries = (await _connection.QueryAsync<AuditRows.EntryRow>(
                new CommandDefinition(page, parameters, transaction, cancellationToken: ct))
                .ConfigureAwait(false))
                .Select(AuditRows.ToEntry)
                .ToList();

            if (query.IncludeChanges)
                await FillChangesAsync(sql, entries, ct).ConfigureAwait(false);

            return new AuditPage(entries, total);
        }, ct).ConfigureAwait(false);
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

        var sql        = Sql;
        var parameters = new DynamicParameters();
        parameters.Add("Cutoff", AuditRows.Utc(cutoff));

        var where = new System.Text.StringBuilder();

        if (scope.EntityName is { } only)
        {
            where.Append(CultureInfo.InvariantCulture, $"AND {sql.Column("EntityName")} = @EntityName ");
            parameters.Add("EntityName", only);
        }

        if (scope.ExcludeEntityNames.Count > 0)
        {
            // The entities with a retention of their own, pruned by their own pass. Without this a
            // longer per-entity retention would be deleted at the shorter global cutoff.
            where.Append(
                CultureInfo.InvariantCulture,
                $"AND {sql.NoneOf(sql.Column("EntityName"), "@Excluded")} ");
            parameters.Add("Excluded", scope.ExcludeEntityNames.ToArray());
        }

        var select =
            $"{sql.SelectIdsOlderThan}{where}{sql.OldestFirst}{AuditStatements.Limit(sql.Dialect, batchSize)}";

        return await UnitOfWork.RunAsync(_connection, async () =>
        {
            var ids = (await _connection.QueryAsync<AuditRows.IdRow>(
                new CommandDefinition(select, parameters, transaction, cancellationToken: ct))
                .ConfigureAwait(false))
                .Select(r => AuditRows.ToGuid(r.Id))
                .ToArray();

            return ids.Length == 0 ? 0 : await DeleteByIdAsync(sql, ids, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<int> DeleteForEntityAsync(string entityName, string entityId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        var sql = Sql;

        return EraseAsync(
            sql,
            sql.SelectIdsForEntity,
            new { EntityName = entityName, EntityId = entityId },
            ct);
    }

    /// <inheritdoc/>
    public Task<int> DeleteForActorAsync(string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var sql = Sql;

        return EraseAsync(sql, sql.SelectIdsForActor, new { Actor = actor }, ct);
    }

    /// <summary>
    /// Deletes every entry <paramref name="selectIds"/> matches, a batch at a time, and reports the
    /// total.
    /// </summary>
    /// <remarks>
    /// Batched for the reason the prune is — a trail that has been collecting for a year is a
    /// delete of millions of rows, and one statement would hold locks across the table that
    /// recording needs — and for one more that is fatal rather than merely slow: the ids go into
    /// the DELETE as an <c>IN</c> list, which Dapper expands to one parameter per id on SQL Server
    /// and SQLite. Unbatched, erasing a trail of more than a couple of thousand entries does not
    /// run slowly, it throws.
    /// <para>
    /// The whole drain runs inside one <see cref="UnitOfWork.RunAsync"/>, so a connection the store
    /// had to open is opened once rather than per batch.
    /// </para>
    /// <para>
    /// A cancelled erasure throws, having deleted whole batches. It is not rolled back — that would
    /// mean holding a transaction across the sweep, which is what batching exists to avoid — and
    /// the work is idempotent, so running it again erases what is left.
    /// </para>
    /// </remarks>
    private async Task<int> EraseAsync(
        AuditStatements sql,
        string selectIds,
        object parameters,
        CancellationToken ct)
    {
        // Oldest first and bounded, exactly as the prune selects its batch. The ORDER BY is not
        // decoration: SQL Server's OFFSET/FETCH is only legal after one, and both erasure filters
        // lead an index that ends on OccurredAt, so it is served by the index rather than sorted.
        var select = $"{selectIds}{sql.OldestFirst}{AuditStatements.Limit(sql.Dialect, EraseBatchSize)}";

        return await UnitOfWork.RunAsync(_connection, async () =>
        {
            var total = 0;

            while (true)
            {
                var ids = (await _connection.QueryAsync<AuditRows.IdRow>(
                    new CommandDefinition(select, parameters, transaction, cancellationToken: ct))
                    .ConfigureAwait(false))
                    .Select(r => AuditRows.ToGuid(r.Id))
                    .ToArray();

                if (ids.Length == 0)
                    return total;

                total += await DeleteByIdAsync(sql, ids, ct).ConfigureAwait(false);

                // Short of a full batch is the store saying that was the last of them, so the next
                // SELECT would come back empty. Saves a round trip on every erasure small enough to
                // fit in one batch, which is nearly all of them.
                if (ids.Length < EraseBatchSize)
                    return total;
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the property rows and then the entries, <see cref="AuditStatements.MaxIdsPerStatement"/>
    /// at a time, and reports how many entries went.
    /// </summary>
    /// <remarks>
    /// The children are deleted explicitly rather than left to the cascade the DDL declares: a
    /// cascade only fires if the database is enforcing foreign keys, which SQLite does not do unless
    /// it was asked to, and an orphaned property row is invisible — nothing reads it and nothing
    /// ever cleans it up. Each pass deletes one group's children before its entries, so a group is
    /// self-contained and no pass can leave an orphan behind.
    /// <para>
    /// <b>The grouping is the statement's limit, not the caller's.</b> Every id handed here is
    /// deleted and counted, however many there are — a prune's <c>batchSize</c> is the caller's to
    /// choose and the drain loop reads a short return as "the backlog is clear". All that changes
    /// is how many statements it takes, which is what keeps an <c>IN</c> list inside the dialect's
    /// parameter ceiling.
    /// </para>
    /// </remarks>
    private async Task<int> DeleteByIdAsync(AuditStatements sql, Guid[] ids, CancellationToken ct)
    {
        var deleted = 0;

        foreach (var group in Grouped(ids, sql.MaxIdsPerStatement))
        {
            await _connection.ExecuteAsync(
                new CommandDefinition(sql.DeleteChangesByEntryIds, new { Ids = group }, transaction, cancellationToken: ct))
                .ConfigureAwait(false);

            deleted += await _connection.ExecuteAsync(
                new CommandDefinition(sql.DeleteEntriesByIds, new { Ids = group }, transaction, cancellationToken: ct))
                .ConfigureAwait(false);
        }

        return deleted;
    }

    /// <summary>
    /// <paramref name="ids"/> in groups of at most <paramref name="size"/>, and as itself when it
    /// already fits — which is every prune and erasure of an ordinary size, and the case worth not
    /// copying an array for.
    /// </summary>
    /// <remarks>
    /// The early return is also what makes the loop safe for a <paramref name="size"/> of
    /// <see cref="int.MaxValue"/>, which is what PostgreSQL asks for: the loop is only ever entered
    /// when <paramref name="size"/> is smaller than an array's length, so its arithmetic cannot
    /// overflow.
    /// </remarks>
    private static IEnumerable<Guid[]> Grouped(Guid[] ids, int size)
    {
        if (ids.Length <= size)
        {
            yield return ids;
            yield break;
        }

        for (var start = 0; start < ids.Length; start += size)
            yield return ids[start..Math.Min(start + size, ids.Length)];
    }

    /// <summary>
    /// Loads the property rows for a page of entries in one round trip.
    /// </summary>
    /// <remarks>
    /// A second query rather than a join. A join multiplies every entry by its changes and the
    /// mapper then has to fold them back, and both tables have columns of the same name. The page
    /// is already bounded by the engine, so the <c>IN</c> list is bounded with it — which matters on
    /// SQL Server, where it becomes one parameter per id against a limit of 2100.
    /// </remarks>
    private async Task FillChangesAsync(AuditStatements sql, List<AuditEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0)
            return;

        var ids = entries.Select(e => e.Id).ToArray();

        var changes = (await _connection.QueryAsync<AuditRows.ChangeRow>(
            new CommandDefinition(sql.SelectChangesForEntries, new { Ids = ids }, transaction, cancellationToken: ct))
            .ConfigureAwait(false))
            .Select(AuditRows.ToChange);

        var byEntry = changes.GroupBy(c => c.AuditEntryId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var entry in entries)
        {
            if (!byEntry.TryGetValue(entry.Id, out var rows))
                continue;

            foreach (var row in rows)
                entry.Changes.Add(row);
        }
    }

    /// <summary>
    /// The WHERE clause and its parameters. Always starts with a true predicate so that every
    /// optional filter can append <c>AND …</c> without caring whether it is the first.
    /// </summary>
    private static (string Where, DynamicParameters Parameters) Filter(AuditStatements sql, AuditQuery query)
    {
        var where      = new System.Text.StringBuilder("WHERE 1 = 1 ");
        var parameters = new DynamicParameters();

        void And(string column, string name, object? value)
        {
            where.Append(CultureInfo.InvariantCulture, $"AND {sql.Column(column)} = @{name} ");
            parameters.Add(name, value);
        }

        if (query.EntityName is { } entityName)
            And("EntityName", "EntityName", entityName);

        if (query.EntityId is { } entityId)
            And("EntityId", "EntityId", entityId);

        if (query.Action is { } action)
            And("Action", "Action", (int)action);

        if (query.Actor is { } actor)
            And("Actor", "Actor", actor);

        // Deliberately separate from Actor: "any actor" and "no actor was captured" are different
        // questions, and one nullable string cannot ask both.
        if (query.ActorMissing)
            where.Append(CultureInfo.InvariantCulture, $"AND {sql.Column("Actor")} IS NULL ");

        if (query.CorrelationId is { } correlationId)
            And("CorrelationId", "CorrelationId", correlationId);

        if (query.From is { } from)
        {
            where.Append(CultureInfo.InvariantCulture, $"AND {sql.Column("OccurredAt")} >= @From ");
            parameters.Add("From", AuditRows.Utc(from));
        }

        // Exclusive, so consecutive windows do not double-count.
        if (query.To is { } to)
        {
            where.Append(CultureInfo.InvariantCulture, $"AND {sql.Column("OccurredAt")} < @To ");
            parameters.Add("To", AuditRows.Utc(to));
        }

        if (query.After is { } cursor)
        {
            // Everything strictly after the cursor in the ORDER BY this store issues: newest first,
            // so "after" is older, and at the same instant a smaller id. Which way that is spelled
            // decides whether the engine seeks or scans — see AuditStatements.AfterCursor, where
            // the two forms differ by 120-fold at depth.
            where.Append(
                CultureInfo.InvariantCulture,
                $"AND {sql.AfterCursor(sql.Column("OccurredAt"), sql.Column("Id"), "@AfterAt", "@AfterId")} ");

            parameters.Add("AfterAt", AuditRows.Utc(cursor.OccurredAt));
            parameters.Add("AfterId", cursor.Id);
        }

        return (where.ToString(), parameters);
    }

}
