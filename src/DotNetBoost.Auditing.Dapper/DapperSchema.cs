using Dapper;
using DotNetBoost.Auditing.Core.Metadata;
using System.Data;
using System.Data.Common;

namespace DotNetBoost.Auditing.Dapper;

/// <summary>
/// Creates the two tables this store reads and writes, for applications that have no migrations of
/// their own to hang them off. Enabled with <c>UseDapper(..., migrateSchema: true)</c>, which runs
/// it once when the host starts; call <see cref="EnsureCreatedAsync"/> directly to run it from a
/// deployment step or a test instead.
/// <para>
/// The shape is the one the EF Core store maps — same table and column names, same lengths, same
/// index names — so a database created here is the database that store expects, and an application
/// can move between them. Column widths come from the same Core constants that store's
/// configuration uses, so the two cannot drift.
/// </para>
/// <para>
/// Every statement is conditional, so running it against an existing database is a no-op. It only
/// ever CREATEs: it will not alter or drop a table that is already there, which means it can bring
/// a database into existence but cannot migrate one whose shape has changed. That is the job of
/// whatever owns the rest of your schema.
/// </para>
/// </summary>
public static class DapperSchema
{
    /// <summary>
    /// Creates the entries and property-changes tables and their indexes if they are absent.
    /// Idempotent.
    /// </summary>
    /// <param name="connection">
    /// The connection to run the DDL on. Opened for the duration if it is closed, and left as it
    /// was found. Its runtime type selects the dialect unless <see cref="DapperOptions.Dialect"/>
    /// says otherwise.
    /// </param>
    /// <param name="options">Table naming. Defaults to <see cref="DapperOptions"/>'s own defaults.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>A task that completes when the tables are present.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <c>null</c>.</exception>
    /// <exception cref="NotSupportedException">The connection type matches no supported dialect.</exception>
    /// <exception cref="InvalidOperationException">The DDL failed — typically a role without CREATE rights.</exception>
    public static async Task EnsureCreatedAsync(
        IDbConnection connection,
        DapperOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        options ??= new DapperOptions();

        var dialect = options.Dialect ?? AuditStatements.DialectOf(connection);
        var script  = Script(dialect, options);

        var opened = await UnitOfWork.OpenIfClosedAsync(connection, ct).ConfigureAwait(false);

        try
        {
            await connection
                .ExecuteAsync(new CommandDefinition(script, cancellationToken: ct))
                .ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            throw new InvalidOperationException(
                $"Could not create the audit tables ('{options.EntriesTable}', '{options.ChangesTable}'). " +
                "The database role needs CREATE TABLE and CREATE INDEX rights. Pass " +
                "migrateSchema: false to UseDapper(...) if the schema is managed elsewhere.",
                ex);
        }
        finally
        {
            if (opened)
                connection.Close();
        }
    }

    /// <summary>
    /// The DDL <see cref="EnsureCreatedAsync"/> would run, for review or for checking into a
    /// migrations folder instead of running it at startup.
    /// </summary>
    /// <param name="dialect">The engine to spell the DDL for.</param>
    /// <param name="options">Table naming. Defaults to <see cref="DapperOptions"/>'s own defaults.</param>
    /// <returns>The script.</returns>
    public static string Script(SqlDialect dialect, DapperOptions? options = null)
    {
        options ??= new DapperOptions();

        var entries = AuditStatements.Qualify(dialect, options.Schema, options.EntriesTable);
        var changes = AuditStatements.Qualify(dialect, options.Schema, options.ChangesTable);

        string C(string column) => AuditStatements.Quote(dialect, column);

        // Widths come from Core so the two stores cannot describe the same column differently.
        var entityName    = AuditEntityNames.MaxLength;             // 128
        var entityId      = EntityKey.MaxLength;                    // 64
        var actor         = AuditColumns.ActorMaxLength;            // 256
        var correlationId = AuditColumns.CorrelationIdMaxLength;    // 128
        var propertyName  = AuditColumns.PropertyNameMaxLength;     // 256

        // Index names match what EF Core generates for the same model, so the two stores' databases
        // are comparable object for object.
        var entriesPk    = "PK_" + options.EntriesTable;
        var changesPk    = "PK_" + options.ChangesTable;
        var entityIndex  = $"IX_{options.EntriesTable}_Entity_OccurredAt";
        var actorIndex   = $"IX_{options.EntriesTable}_Actor_OccurredAt";
        var ageIndex     = $"IX_{options.EntriesTable}_OccurredAt";
        var correlIndex  = $"IX_{options.EntriesTable}_CorrelationId";
        var propIndex    = $"IX_{options.ChangesTable}_PropertyName";
        var changesFk    = $"FK_{options.ChangesTable}_{options.EntriesTable}";

        return dialect switch
        {
            SqlDialect.SqlServer => $"""
                {SqlServerSchema(options.Schema)}
                IF OBJECT_ID(N'{entries}', N'U') IS NULL
                BEGIN
                    CREATE TABLE {entries} (
                        {C("Id")}            UNIQUEIDENTIFIER          NOT NULL,
                        {C("EntityName")}    NVARCHAR({entityName})    NOT NULL,
                        {C("EntityId")}      NVARCHAR({entityId})      NOT NULL,
                        {C("Action")}        INT                       NOT NULL,
                        {C("Actor")}         NVARCHAR({actor})         NULL,
                        {C("CorrelationId")} NVARCHAR({correlationId}) NOT NULL,
                        {C("OccurredAt")}    DATETIME2                 NOT NULL,
                        {C("Metadata")}      NVARCHAR(MAX)             NULL,
                        CONSTRAINT {C(entriesPk)} PRIMARY KEY ({C("Id")})
                    );
                    CREATE INDEX {C(entityIndex)} ON {entries} ({C("EntityName")}, {C("EntityId")}, {C("OccurredAt")});
                    CREATE INDEX {C(actorIndex)}  ON {entries} ({C("Actor")}, {C("OccurredAt")});
                    CREATE INDEX {C(ageIndex)}    ON {entries} ({C("OccurredAt")});
                    CREATE INDEX {C(correlIndex)} ON {entries} ({C("CorrelationId")});
                END;

                IF OBJECT_ID(N'{changes}', N'U') IS NULL
                BEGIN
                    CREATE TABLE {changes} (
                        {C("AuditEntryId")} UNIQUEIDENTIFIER         NOT NULL,
                        {C("PropertyName")} NVARCHAR({propertyName}) NOT NULL,
                        {C("OldValue")}     NVARCHAR(MAX)            NULL,
                        {C("NewValue")}     NVARCHAR(MAX)            NULL,
                        {C("IsRedacted")}   BIT                      NOT NULL,
                        {C("IsTruncated")}  BIT                      NOT NULL,
                        CONSTRAINT {C(changesPk)} PRIMARY KEY ({C("AuditEntryId")}, {C("PropertyName")}),
                        CONSTRAINT {C(changesFk)} FOREIGN KEY ({C("AuditEntryId")})
                            REFERENCES {entries} ({C("Id")}) ON DELETE CASCADE
                    );
                    CREATE INDEX {C(propIndex)} ON {changes} ({C("PropertyName")});
                END;
                """,

            SqlDialect.PostgreSql => $"""
                {PostgresSchema(options.Schema)}
                CREATE TABLE IF NOT EXISTS {entries} (
                    {C("Id")}            UUID                             NOT NULL,
                    {C("EntityName")}    CHARACTER VARYING({entityName})  NOT NULL,
                    {C("EntityId")}      CHARACTER VARYING({entityId})    NOT NULL,
                    {C("Action")}        INTEGER                          NOT NULL,
                    {C("Actor")}         CHARACTER VARYING({actor})       NULL,
                    {C("CorrelationId")} CHARACTER VARYING({correlationId}) NOT NULL,
                    {C("OccurredAt")}    TIMESTAMP WITH TIME ZONE         NOT NULL,
                    {C("Metadata")}      TEXT                             NULL,
                    CONSTRAINT {C(entriesPk)} PRIMARY KEY ({C("Id")})
                );

                CREATE INDEX IF NOT EXISTS {C(entityIndex)} ON {entries} ({C("EntityName")}, {C("EntityId")}, {C("OccurredAt")});
                CREATE INDEX IF NOT EXISTS {C(actorIndex)}  ON {entries} ({C("Actor")}, {C("OccurredAt")});
                CREATE INDEX IF NOT EXISTS {C(ageIndex)}    ON {entries} ({C("OccurredAt")});
                CREATE INDEX IF NOT EXISTS {C(correlIndex)} ON {entries} ({C("CorrelationId")});

                CREATE TABLE IF NOT EXISTS {changes} (
                    {C("AuditEntryId")} UUID                              NOT NULL,
                    {C("PropertyName")} CHARACTER VARYING({propertyName}) NOT NULL,
                    {C("OldValue")}     TEXT                              NULL,
                    {C("NewValue")}     TEXT                              NULL,
                    {C("IsRedacted")}   BOOLEAN                           NOT NULL,
                    {C("IsTruncated")}  BOOLEAN                           NOT NULL,
                    CONSTRAINT {C(changesPk)} PRIMARY KEY ({C("AuditEntryId")}, {C("PropertyName")}),
                    CONSTRAINT {C(changesFk)} FOREIGN KEY ({C("AuditEntryId")})
                        REFERENCES {entries} ({C("Id")}) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS {C(propIndex)} ON {changes} ({C("PropertyName")});
                """,

            // SQLite has no schemas, no length enforcement and no native GUID, boolean or date
            // type: GUIDs and timestamps are TEXT and booleans are INTEGER, which is exactly how
            // EF Core's SQLite provider stores them — so one database still serves both stores.
            // The foreign key is declared for parity; SQLite only enforces it when the connection
            // has asked it to, which is why the store deletes the child rows itself.
            SqlDialect.Sqlite => $"""
                CREATE TABLE IF NOT EXISTS {entries} (
                    {C("Id")}            TEXT    NOT NULL,
                    {C("EntityName")}    TEXT    NOT NULL,
                    {C("EntityId")}      TEXT    NOT NULL,
                    {C("Action")}        INTEGER NOT NULL,
                    {C("Actor")}         TEXT    NULL,
                    {C("CorrelationId")} TEXT    NOT NULL,
                    {C("OccurredAt")}    TEXT    NOT NULL,
                    {C("Metadata")}      TEXT    NULL,
                    CONSTRAINT {C(entriesPk)} PRIMARY KEY ({C("Id")})
                );

                CREATE INDEX IF NOT EXISTS {C(entityIndex)} ON {entries} ({C("EntityName")}, {C("EntityId")}, {C("OccurredAt")});
                CREATE INDEX IF NOT EXISTS {C(actorIndex)}  ON {entries} ({C("Actor")}, {C("OccurredAt")});
                CREATE INDEX IF NOT EXISTS {C(ageIndex)}    ON {entries} ({C("OccurredAt")});
                CREATE INDEX IF NOT EXISTS {C(correlIndex)} ON {entries} ({C("CorrelationId")});

                CREATE TABLE IF NOT EXISTS {changes} (
                    {C("AuditEntryId")} TEXT    NOT NULL,
                    {C("PropertyName")} TEXT    NOT NULL,
                    {C("OldValue")}     TEXT    NULL,
                    {C("NewValue")}     TEXT    NULL,
                    {C("IsRedacted")}   INTEGER NOT NULL,
                    {C("IsTruncated")}  INTEGER NOT NULL,
                    CONSTRAINT {C(changesPk)} PRIMARY KEY ({C("AuditEntryId")}, {C("PropertyName")}),
                    CONSTRAINT {C(changesFk)} FOREIGN KEY ({C("AuditEntryId")})
                        REFERENCES {entries} ({C("Id")}) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS {C(propIndex)} ON {changes} ({C("PropertyName")});
                """,

            _ => throw new NotSupportedException($"Unknown SQL dialect '{dialect}'.")
        };
    }

    private static string SqlServerSchema(string? schema) =>
        string.IsNullOrWhiteSpace(schema)
            ? string.Empty
            : $"""
               IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'{schema.Replace("'", "''", StringComparison.Ordinal)}')
                   EXEC(N'CREATE SCHEMA {AuditStatements.Quote(SqlDialect.SqlServer, schema)}');
               """;

    private static string PostgresSchema(string? schema) =>
        string.IsNullOrWhiteSpace(schema)
            ? string.Empty
            : $"CREATE SCHEMA IF NOT EXISTS {AuditStatements.Quote(SqlDialect.PostgreSql, schema)};";
}
