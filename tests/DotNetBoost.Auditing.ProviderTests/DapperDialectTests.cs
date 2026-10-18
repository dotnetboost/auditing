using Microsoft.Data.Sqlite;
using DotNetBoost.Auditing.Dapper;

namespace DotNetBoost.Auditing.ProviderTests;

/// <summary>
/// The dialect helpers, and the DDL for the two engines a unit test cannot start.
/// </summary>
/// <remarks>
/// The SQL Server and PostgreSQL scripts are asserted as <b>text</b> — the object names, the widths
/// and the constraints they must declare — not executed. That is weaker than running them and the
/// difference matters: these assertions catch a renamed index or a missing cascade, and would not
/// catch invalid syntax. Running them needs a container, which belongs in an integration suite
/// rather than here.
/// </remarks>
public class DapperDialectTests
{
    [Fact]
    public void A_connection_type_selects_its_dialect()
    {
        using var sqlite = new SqliteConnection("DataSource=:memory:");
        Assert.Equal(SqlDialect.Sqlite, AuditStatements.DialectOf(sqlite));
    }

    [Fact]
    public void An_unknown_connection_type_says_what_to_do_about_it()
    {
        using var connection = new UnknownConnection();

        var error = Assert.Throws<NotSupportedException>(() => AuditStatements.DialectOf(connection));

        Assert.Contains("DapperOptions.Dialect", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SqlDialect.SqlServer, "[AuditEntries]")]
    [InlineData(SqlDialect.PostgreSql, "\"AuditEntries\"")]
    [InlineData(SqlDialect.Sqlite, "\"AuditEntries\"")]
    public void Identifiers_are_quoted_the_way_their_engine_quotes_them(SqlDialect dialect, string expected) =>
        Assert.Equal(expected, AuditStatements.Quote(dialect, "AuditEntries"));

    [Fact]
    public void A_closing_delimiter_in_an_identifier_is_doubled()
    {
        // Not expected in a table name. Not doing it would be an injection point in the method
        // whose whole job is to make one safe.
        Assert.Equal("[odd]]name]", AuditStatements.Quote(SqlDialect.SqlServer, "odd]name"));
        Assert.Equal("\"odd\"\"name\"", AuditStatements.Quote(SqlDialect.PostgreSql, "odd\"name"));
    }

    [Fact]
    public void A_schema_qualifies_the_table_when_there_is_one()
    {
        Assert.Equal("\"audit\".\"AuditEntries\"",
            AuditStatements.Qualify(SqlDialect.PostgreSql, "audit", "AuditEntries"));

        Assert.Equal("\"AuditEntries\"",
            AuditStatements.Qualify(SqlDialect.PostgreSql, null, "AuditEntries"));
    }

    [Theory]
    [InlineData(SqlDialect.SqlServer, "OFFSET 10 ROWS FETCH NEXT 25 ROWS ONLY")]
    [InlineData(SqlDialect.PostgreSql, "LIMIT 25 OFFSET 10")]
    [InlineData(SqlDialect.Sqlite, "LIMIT 25 OFFSET 10")]
    public void A_page_is_limited_the_way_its_engine_limits_one(SqlDialect dialect, string expected) =>
        Assert.Equal(expected, AuditStatements.Limit(dialect, take: 25, skip: 10));

    [Theory]
    [InlineData(SqlDialect.SqlServer)]
    [InlineData(SqlDialect.PostgreSql)]
    [InlineData(SqlDialect.Sqlite)]
    public void Every_dialects_script_declares_the_same_objects(SqlDialect dialect)
    {
        var script = DapperSchema.Script(dialect);

        // The names the EF Core model generates, so the two stores' databases are comparable object
        // for object and one serves either.
        Assert.Contains("AuditEntries", script, StringComparison.Ordinal);
        Assert.Contains("AuditPropertyChanges", script, StringComparison.Ordinal);
        Assert.Contains("PK_AuditEntries", script, StringComparison.Ordinal);
        Assert.Contains("PK_AuditPropertyChanges", script, StringComparison.Ordinal);
        Assert.Contains("IX_AuditEntries_Entity_OccurredAt", script, StringComparison.Ordinal);
        Assert.Contains("IX_AuditEntries_Actor_OccurredAt", script, StringComparison.Ordinal);
        Assert.Contains("IX_AuditEntries_OccurredAt", script, StringComparison.Ordinal);
        Assert.Contains("IX_AuditEntries_CorrelationId", script, StringComparison.Ordinal);
        Assert.Contains("IX_AuditPropertyChanges_PropertyName", script, StringComparison.Ordinal);

        // The one cascade in the schema, so a deleted entry cannot leave unreadable property rows.
        Assert.Contains("ON DELETE CASCADE", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SqlDialect.SqlServer)]
    [InlineData(SqlDialect.PostgreSql)]
    public void The_relational_scripts_declare_the_widths_Core_fixes(SqlDialect dialect)
    {
        var script = DapperSchema.Script(dialect);

        // From the same Core constants the EF Core configuration uses, so the two cannot drift.
        Assert.Contains("128", script, StringComparison.Ordinal);   // entity name, correlation id
        Assert.Contains("(64)", script, StringComparison.Ordinal);  // entity key
        Assert.Contains("(256)", script, StringComparison.Ordinal); // actor, property name
    }

    [Fact]
    public void A_schema_name_is_created_for_the_engines_that_have_schemas()
    {
        Assert.Contains("CREATE SCHEMA", DapperSchema.Script(SqlDialect.SqlServer, new DapperOptions { Schema = "audit" }),
            StringComparison.Ordinal);

        Assert.Contains("CREATE SCHEMA IF NOT EXISTS", DapperSchema.Script(SqlDialect.PostgreSql, new DapperOptions { Schema = "audit" }),
            StringComparison.Ordinal);

        // SQLite has none, and asking for one must not put a CREATE SCHEMA in its script.
        Assert.DoesNotContain("CREATE SCHEMA", DapperSchema.Script(SqlDialect.Sqlite, new DapperOptions { Schema = "audit" }),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Table_names_can_be_changed()
    {
        var script = DapperSchema.Script(SqlDialect.Sqlite, new DapperOptions
        {
            EntriesTable = "Trail",
            ChangesTable = "TrailValues"
        });

        Assert.Contains("\"Trail\"", script, StringComparison.Ordinal);
        Assert.Contains("PK_Trail", script, StringComparison.Ordinal);
        Assert.Contains("IX_Trail_OccurredAt", script, StringComparison.Ordinal);
        Assert.DoesNotContain("AuditEntries", script, StringComparison.Ordinal);
    }

    private sealed class UnknownConnection : System.Data.Common.DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = "";
        public override string Database => "";
        public override string DataSource => "";
        public override string ServerVersion => "";
        public override System.Data.ConnectionState State => System.Data.ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { }
        public override void Open() => throw new NotSupportedException();

        protected override System.Data.Common.DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override System.Data.Common.DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
