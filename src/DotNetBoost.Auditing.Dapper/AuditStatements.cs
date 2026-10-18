using System.Collections.Concurrent;
using System.Data;
using System.Globalization;

namespace DotNetBoost.Auditing.Dapper;

/// <summary>
/// Every statement the store issues, spelled for one dialect and one pair of table names. Built
/// once per (dialect, schema, tables) and cached: the store is scoped, so composing a dozen strings
/// in each constructor would be per-request work for a result that never varies.
/// </summary>
internal sealed class AuditStatements
{
    private static readonly ConcurrentDictionary<(SqlDialect, string?, string), AuditStatements> Cache = new();

    /// <summary>Every entry column, in a fixed order, so INSERT and SELECT cannot disagree.</summary>
    internal const string EntryColumnList =
        "Id, EntityName, EntityId, Action, Actor, CorrelationId, OccurredAt, Metadata";

    /// <summary>Every property-change column, in a fixed order, for the same reason.</summary>
    internal const string ChangeColumnList =
        "AuditEntryId, PropertyName, OldValue, NewValue, IsRedacted, IsTruncated";

    private AuditStatements(SqlDialect dialect, DapperOptions options)
    {
        Dialect      = dialect;
        EntriesTable = Qualify(dialect, options.Schema, options.EntriesTable);
        ChangesTable = Qualify(dialect, options.Schema, options.ChangesTable);

        var entryColumns = Columns(dialect, EntryColumnList);
        var entryValues  = Parameters(EntryColumnList);

        var changeColumns = Columns(dialect, ChangeColumnList);
        var changeValues  = Parameters(ChangeColumnList);

        string C(string column) => Quote(dialect, column);

        InsertEntry  = $"INSERT INTO {EntriesTable} ({entryColumns}) VALUES ({entryValues})";
        InsertChange = $"INSERT INTO {ChangesTable} ({changeColumns}) VALUES ({changeValues})";

        SelectEntries = $"SELECT {entryColumns} FROM {EntriesTable} ";

        SelectEntryById = $"{SelectEntries}WHERE {C("Id")} = @Id";

        // Newest first, with the id as the tiebreaker: two entries recorded in the same tick must
        // not swap places between one page and the next, or paging a busy trail shows a row twice
        // and skips another. The same ordering the EF Core store uses.
        OrderBy = $"ORDER BY {C("OccurredAt")} DESC, {C("Id")} DESC ";

        SelectChangesForEntries =
            $"SELECT {changeColumns} FROM {ChangesTable} WHERE {AnyOf(dialect, C("AuditEntryId"), "@Ids")} " +
            $"ORDER BY {C("PropertyName")} ASC";

        SelectIdsOlderThan = $"SELECT {C("Id")} FROM {EntriesTable} WHERE {C("OccurredAt")} < @Cutoff ";

        SelectIdsForEntity =
            $"SELECT {C("Id")} FROM {EntriesTable} " +
            $"WHERE {C("EntityName")} = @EntityName AND {C("EntityId")} = @EntityId";

        SelectIdsForActor = $"SELECT {C("Id")} FROM {EntriesTable} WHERE {C("Actor")} = @Actor";

        DeleteChangesByEntryIds = $"DELETE FROM {ChangesTable} WHERE {AnyOf(dialect, C("AuditEntryId"), "@Ids")}";
        DeleteEntriesByIds      = $"DELETE FROM {EntriesTable} WHERE {AnyOf(dialect, C("Id"), "@Ids")}";

        OldestFirst = $"ORDER BY {C("OccurredAt")} ASC, {C("Id")} ASC ";
    }

    internal SqlDialect Dialect { get; }
    internal string EntriesTable { get; }
    internal string ChangesTable { get; }

    internal string InsertEntry { get; }
    internal string InsertChange { get; }
    internal string SelectEntries { get; }
    internal string SelectEntryById { get; }
    internal string OrderBy { get; }
    internal string OldestFirst { get; }
    internal string SelectChangesForEntries { get; }
    internal string SelectIdsOlderThan { get; }
    internal string SelectIdsForEntity { get; }
    internal string SelectIdsForActor { get; }
    internal string DeleteChangesByEntryIds { get; }
    internal string DeleteEntriesByIds { get; }

    internal static AuditStatements For(SqlDialect dialect, DapperOptions options) =>
        Cache.GetOrAdd(
            (dialect, options.Schema, options.EntriesTable + "|" + options.ChangesTable),
            static (_, state) => new AuditStatements(state.Dialect, state.Options),
            (Dialect: dialect, Options: options));

    /// <summary>A column name, quoted for the dialect.</summary>
    internal string Column(string column) => Quote(Dialect, column);

    /// <summary>
    /// A predicate matching <paramref name="column"/> against a set of values, spelled for the
    /// dialect.
    /// </summary>
    /// <remarks>
    /// <b>Not one spelling for all three.</b> Dapper expands <c>IN @p</c> into
    /// <c>IN (@p1, @p2, …)</c> on SQL Server and SQLite, and <b>does not do it at all on
    /// Npgsql</b> — the parameter goes through whole, PostgreSQL is handed <c>IN $1</c>, and the
    /// statement fails with a syntax error. It happens for a list as well as an array, and nothing
    /// about the C# says which engine is underneath, so the unit suite on SQLite passed it
    /// cheerfully; the integration suite found it on the first run.
    /// <para>
    /// <c>= ANY(@p)</c> is PostgreSQL's own form and the better one anyway: one parameter instead of
    /// one per id, so there is no 2100-parameter ceiling to run into and the plan is reused across
    /// batch sizes.
    /// </para>
    /// </remarks>
    internal string AnyOf(string column, string parameter) => AnyOf(Dialect, column, parameter);

    /// <inheritdoc cref="AnyOf(string, string)"/>
    internal static string AnyOf(SqlDialect dialect, string column, string parameter) => dialect switch
    {
        SqlDialect.PostgreSql => $"{column} = ANY({parameter})",
        _                     => $"{column} IN {parameter}"
    };

    /// <summary>
    /// The negation of <see cref="AnyOf(string, string)"/>, for the same reason and with the same
    /// per-dialect spelling.
    /// </summary>
    /// <remarks>
    /// On an empty set, <c>&lt;&gt; ALL(…)</c> is true for every row — "none of nothing" excludes
    /// nothing — which is the right answer. Callers guard anyway, so it is never reached.
    /// </remarks>
    internal string NoneOf(string column, string parameter) => Dialect switch
    {
        SqlDialect.PostgreSql => $"{column} <> ALL({parameter})",
        _                     => $"{column} NOT IN {parameter}"
    };

    /// <summary>
    /// The dialect a connection speaks, from its runtime type name. Deliberately a name match
    /// rather than a reference to each provider's package: this library would otherwise have to
    /// depend on all three to support any one of them.
    /// </summary>
    /// <exception cref="NotSupportedException">The connection matches none of the supported engines.</exception>
    internal static SqlDialect DialectOf(IDbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var name = connection.GetType().Name;

        return name switch
        {
            "SqlConnection"    => SqlDialect.SqlServer,
            "NpgsqlConnection" => SqlDialect.PostgreSql,
            "SqliteConnection" or "SQLiteConnection" => SqlDialect.Sqlite,
            _ => throw new NotSupportedException(
                $"Cannot infer a SQL dialect from connection type '{name}'. Supported: SQL Server, " +
                "PostgreSQL and SQLite. Set DapperOptions.Dialect explicitly — which is also what to do " +
                "when the connection is a wrapper around one of them.")
        };
    }

    /// <summary>An identifier, quoted the way its engine quotes one.</summary>
    internal static string Quote(SqlDialect dialect, string identifier) => dialect switch
    {
        // Doubling the closing delimiter is what makes an identifier containing one safe. Neither
        // is expected in a table name; not doing it would be an injection point in a method whose
        // whole job is to make one safe.
        SqlDialect.SqlServer => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]",
        _                    => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
    };

    /// <summary>A table name, with its schema when there is one.</summary>
    internal static string Qualify(SqlDialect dialect, string? schema, string table) =>
        string.IsNullOrWhiteSpace(schema)
            ? Quote(dialect, table)
            : $"{Quote(dialect, schema)}.{Quote(dialect, table)}";

    /// <summary>A row limit, spelled for the dialect. Must follow an ORDER BY on SQL Server.</summary>
    internal static string Limit(SqlDialect dialect, int take, int skip = 0) => dialect switch
    {
        SqlDialect.SqlServer => string.Create(
            CultureInfo.InvariantCulture,
            $"OFFSET {skip} ROWS FETCH NEXT {take} ROWS ONLY"),
        _ => string.Create(CultureInfo.InvariantCulture, $"LIMIT {take} OFFSET {skip}")
    };

    private static string Columns(SqlDialect dialect, string list) =>
        string.Join(", ", Split(list).Select(c => Quote(dialect, c)));

    private static string Parameters(string list) =>
        string.Join(", ", Split(list).Select(c => "@" + c));

    private static string[] Split(string list) => list.Split(',', StringSplitOptions.TrimEntries);
}
