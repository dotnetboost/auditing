namespace DotNetBoost.Auditing.Dapper;

/// <summary>
/// Table naming for the two tables this store reads and writes. The defaults match
/// <c>AuditingModelOptions</c> in the EF Core store, so the same database serves both — and a test
/// asserts that by writing with one store and reading with the other.
/// </summary>
public sealed class DapperOptions
{
    /// <summary>Table holding the entries. Default: <c>AuditEntries</c>.</summary>
    public string EntriesTable { get; set; } = "AuditEntries";

    /// <summary>Table holding one row per property that changed. Default: <c>AuditPropertyChanges</c>.</summary>
    public string ChangesTable { get; set; } = "AuditPropertyChanges";

    /// <summary>
    /// Schema for the tables, or <c>null</c> for the connection's default. SQLite has no schemas;
    /// leave this unset there.
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>
    /// Overrides the SQL dialect instead of inferring it from the connection type. Set this when
    /// the connection is a wrapper — a profiler, a resiliency decorator — whose type name no longer
    /// says which engine is behind it.
    /// </summary>
    public SqlDialect? Dialect { get; set; }
}

/// <summary>
/// The SQL dialects this store writes. They differ in identifier quoting, in how a boolean, a GUID
/// and a timestamp are spelled, and in how a page is limited; everything else is the same statement.
/// </summary>
public enum SqlDialect
{
    /// <summary>Microsoft SQL Server. Identifiers in <c>[brackets]</c>, paging via <c>OFFSET … FETCH</c>.</summary>
    SqlServer,

    /// <summary>PostgreSQL. Identifiers in <c>"quotes"</c>, paging via <c>LIMIT … OFFSET</c>.</summary>
    PostgreSql,

    /// <summary>SQLite. Identifiers in <c>"quotes"</c>, paging via <c>LIMIT … OFFSET</c>.</summary>
    Sqlite
}
