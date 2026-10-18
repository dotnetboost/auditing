using Dapper;
using DotNetBoost.Auditing.Core.Models;
using Microsoft.Data.Sqlite;
using DotNetBoost.Auditing.Dapper;

namespace DotNetBoost.Auditing.ProviderTests;

/// <summary>
/// Atomicity with Dapper. The EF Core store gets it for free by joining the caller's
/// <c>SaveChanges</c>; here it costs one transaction, and this is what that buys.
/// </summary>
public class DapperTransactionTests
{
    private static AuditEntry Entry(string entityId = "42") => new()
    {
        Id            = Guid.NewGuid(),
        EntityName    = "Product",
        EntityId      = entityId,
        Action        = AuditAction.Updated,
        CorrelationId = "tx",
        OccurredAt    = DateTime.UtcNow,
        Changes       = { new AuditPropertyChange { PropertyName = "Price", OldValue = "1", NewValue = "2" } }
    };

    private static async Task<int> CountAsync(DapperHarness harness, string table)
    {
        using var connection = harness.Connect();
        return await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM \"{table}\"");
    }

    [Fact]
    public async Task Rolling_back_the_transaction_takes_the_entry_with_it()
    {
        using var harness = new DapperHarness();

        using var connection = harness.Connect();
        using (var transaction = connection.BeginTransaction())
        {
            var store = new DapperAuditStore(connection, new DapperOptions()).WithTransaction(transaction);

            // The application's own work would go here, on the same connection and transaction.
            await connection.ExecuteAsync(
                "CREATE TABLE IF NOT EXISTS \"Products\" (\"Id\" INTEGER PRIMARY KEY, \"Price\" TEXT)",
                transaction: transaction);

            await connection.ExecuteAsync(
                "INSERT INTO \"Products\" (\"Id\", \"Price\") VALUES (42, '2')",
                transaction: transaction);

            await store.AppendAsync(Entry());

            transaction.Rollback();
        }

        // Neither the row nor its trail. Without the transaction the trail would now claim a price
        // change that never happened.
        Assert.Equal(0, await CountAsync(harness, "AuditEntries"));
        Assert.Equal(0, await CountAsync(harness, "AuditPropertyChanges"));
    }

    [Fact]
    public async Task Committing_the_transaction_keeps_the_entry()
    {
        using var harness = new DapperHarness();

        using var connection = harness.Connect();
        using (var transaction = connection.BeginTransaction())
        {
            var store = new DapperAuditStore(connection, new DapperOptions()).WithTransaction(transaction);
            await store.AppendAsync(Entry());
            transaction.Commit();
        }

        Assert.Equal(1, await CountAsync(harness, "AuditEntries"));
        Assert.Equal(1, await CountAsync(harness, "AuditPropertyChanges"));
    }

    [Fact]
    public async Task Without_a_transaction_the_entry_is_committed_as_it_is_written()
    {
        using var harness = new DapperHarness();

        using var connection = harness.Connect();
        var store = new DapperAuditStore(connection, new DapperOptions());

        await store.AppendAsync(Entry());

        // The documented difference from the EF Core store, which only tracks until the caller
        // saves. Here the INSERT has already happened and a failure immediately afterwards leaves a
        // trail of a change that was rolled back. That is why WithTransaction exists.
        Assert.Equal(1, await CountAsync(harness, "AuditEntries"));
    }

    [Fact]
    public async Task WithTransaction_leaves_the_original_store_alone()
    {
        using var harness = new DapperHarness();

        using var connection = harness.Connect();
        var store = new DapperAuditStore(connection, new DapperOptions());

        using (var transaction = connection.BeginTransaction())
        {
            var enlisted = store.WithTransaction(transaction);
            Assert.NotSame(store, enlisted);

            await enlisted.AppendAsync(Entry("1"));
            transaction.Rollback();
        }

        Assert.Equal(0, await CountAsync(harness, "AuditEntries"));

        // The original never joined that transaction, so it still works afterwards.
        await store.AppendAsync(Entry("2"));
        Assert.Equal(1, await CountAsync(harness, "AuditEntries"));
    }

    [Fact]
    public async Task A_closed_connection_is_opened_and_left_as_it_was_found()
    {
        using var harness = new DapperHarness();

        using var connection = new SqliteConnection(harness.ConnectionString);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);

        var store = new DapperAuditStore(connection, new DapperOptions());
        await store.AppendAsync(Entry());

        // Borrowed, never owned: the application may be about to open it, or to begin a
        // transaction on it.
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
        Assert.Equal(1, await CountAsync(harness, "AuditEntries"));
    }

    [Fact]
    public async Task An_already_open_connection_is_left_open()
    {
        using var harness = new DapperHarness();

        using var connection = harness.Connect();
        var store = new DapperAuditStore(connection, new DapperOptions());

        await store.AppendAsync(Entry());

        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }
}
