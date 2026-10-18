using Dapper;
using DotNetBoost.Auditing.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MongoDb;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace DotNetBoost.Auditing.IntegrationTests;

/// <summary>
/// An entity for the tests that need one — the interceptor cannot be exercised without something to
/// change.
/// </summary>
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}

/// <summary>A context with the audit tables mapped, for whichever engine the test points it at.</summary>
public class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyAuditing();
        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>
/// Starts a container once for every test class in its collection.
/// </summary>
/// <remarks>
/// <b>This suite does not skip when Docker is missing; it fails.</b> A container-gated suite that
/// skips silently reports green, which is the one outcome worse than not having it — the three
/// stores it exists to prove would go on looking tested. The failure message says what is needed.
/// </remarks>
public abstract class ContainerFixture : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        try
        {
            await StartAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start the {GetType().Name} container. This suite needs a running Docker " +
                "daemon: it exercises the stores against real engines, which is the only way the SQL " +
                "and the drivers' behaviour get verified. Run the unit suites instead if Docker is " +
                "unavailable — they cover everything that does not need a server.",
                ex);
        }
    }

    public abstract Task DisposeAsync();

    protected abstract Task StartAsync();
}

/// <summary>PostgreSQL, the engine both relational stores are proven against here.</summary>
public sealed class PostgresFixture : ContainerFixture
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    protected override Task StartAsync() => _container.StartAsync();

    public override Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>
    /// A connection string to a database of its own, created if it is absent.
    /// </summary>
    /// <remarks>
    /// A database per store, not one shared. Both relational stores create the same tables, so in a
    /// shared database whichever ran first would satisfy the other's <c>CREATE TABLE IF NOT
    /// EXISTS</c> — and the Dapper DDL, the thing these tests exist to execute for the first time,
    /// would never run at all.
    /// </remarks>
    public async Task<string> DatabaseAsync(string name)
    {
        await using var admin = new Npgsql.NpgsqlConnection(ConnectionString);
        await admin.OpenAsync();

        var exists = await admin.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname = @name)",
            new { name });

        if (!exists)
        {
            // The name is a test constant, never user input; quoted anyway, because a helper that
            // interpolates an identifier should do it the safe way wherever it lives.
            await admin.ExecuteAsync(
                $"CREATE DATABASE \"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"");
        }

        return new Npgsql.NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }
}

/// <summary>
/// MongoDB, as a single-node <b>replica set</b> rather than a standalone server.
/// </summary>
/// <remarks>
/// A standalone <c>mongod</c> refuses to start a transaction, so a standalone container could not
/// test <c>WithSession</c> at all — and that is the store's only route to atomicity with an
/// application's own write. Testcontainers' replica-set mode costs a few seconds of startup and buys
/// the one guarantee that would otherwise stay unproven.
/// </remarks>
public sealed class MongoFixture : ContainerFixture
{
    private readonly MongoDbContainer _container =
        new MongoDbBuilder("mongo:7.0").WithReplicaSet().Build();

    public string ConnectionString => _container.GetConnectionString();

    protected override Task StartAsync() => _container.StartAsync();

    public override Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>
/// SQL Server.
/// </summary>
/// <remarks>
/// <b>amd64 only.</b> Microsoft publishes no arm64 image, so on Apple Silicon this runs under
/// emulation. It does work — it was developed that way, ready in about twenty seconds — but it is
/// the slowest fixture here and the likeliest to fail on a machine without amd64 emulation
/// available.
/// <para>
/// Its tests are in a collection of their own, so excluding them costs nothing else:
/// <c>dotnet test --filter "FullyQualifiedName!~SqlServer"</c>.
/// </para>
/// </remarks>
public sealed class SqlServerFixture : ContainerFixture
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string ConnectionString => _container.GetConnectionString();

    protected override Task StartAsync() => _container.StartAsync();

    public override Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>A connection string to a database of its own, created if it is absent.</summary>
    /// <remarks>
    /// A database per store, for the reason <see cref="PostgresFixture.DatabaseAsync"/> gives: in a
    /// shared one, whichever store created the tables first would satisfy the other's conditional
    /// DDL and the Dapper script would never run.
    /// </remarks>
    public async Task<string> DatabaseAsync(string name)
    {
        await using var admin = new Microsoft.Data.SqlClient.SqlConnection(ConnectionString);
        await admin.OpenAsync();

        await admin.ExecuteAsync(
            $"IF DB_ID(N'{name.Replace("'", "''", StringComparison.Ordinal)}') IS NULL " +
            $"CREATE DATABASE [{name.Replace("]", "]]", StringComparison.Ordinal)}]");

        return new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = name
        }.ConnectionString;
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

[CollectionDefinition("mongodb")]
public sealed class MongoCollection : ICollectionFixture<MongoFixture>;

[CollectionDefinition("sqlserver")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>;
