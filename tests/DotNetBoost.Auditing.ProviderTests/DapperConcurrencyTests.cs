using Dapper;
using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.Dapper;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace DotNetBoost.Auditing.ProviderTests;

/// <summary>
/// Opens slowly, so two callers can be inside the open window at once — the only way to create the
/// interleaving this file is about without waiting on a clock.
/// </summary>
/// <remarks>
/// The barrier waits with a timeout rather than for ever, because the fix means the second caller
/// never arrives: it is queued behind the first. So pre-fix the two meet instantly and the race
/// happens; post-fix the first waits out the timeout alone and carries on. A test that needed the
/// second participant would deadlock on the fixed code, which is the wrong way round.
/// </remarks>
internal sealed class GatedConnection(string connectionString, Barrier gate, TimeSpan wait)
    : SqliteConnection(connectionString)
{
    public override void Open()
    {
        gate.SignalAndWait(wait);
        base.Open();
    }
}

/// <summary>
/// The Dapper store borrows the application's connection, and the connection is scoped — so two
/// audited operations in one scope share it. These are the tests for what happens then.
/// </summary>
public class DapperConcurrencyTests
{
    [Fact]
    public async Task A_second_operation_waits_rather_than_sharing_the_connection()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");

        var log      = new List<string>();
        var released = new TaskCompletionSource();
        var started  = new TaskCompletionSource();

        async Task<bool> First()
        {
            lock (log) log.Add("first in");
            started.SetResult();
            await released.Task;
            lock (log) log.Add("first out");
            return true;
        }

        Task<bool> Second()
        {
            lock (log) log.Add("second in");
            lock (log) log.Add("second out");
            return Task.FromResult(true);
        }

        var first = UnitOfWork.RunAsync(connection, First, CancellationToken.None);
        await started.Task;

        // Starts while the first is still inside. An ADO.NET connection cannot run two commands at
        // once, so the only correct behaviour is to queue.
        var second = UnitOfWork.RunAsync(connection, Second, CancellationToken.None);

        released.SetResult();
        await Task.WhenAll(first, second);

        // No sleeps anywhere: the order is the assertion. "second in" between the first pair is the
        // bug — two operations on one connection at the same time.
        Assert.Equal(["first in", "first out", "second in", "second out"], log);

        // And the connection is as it was found.
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task Two_concurrent_appends_on_a_closed_connection_both_land()
    {
        var connectionString = $"DataSource=file:{Guid.NewGuid():N}?mode=memory&cache=shared";

        using var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();
        await DapperSchema.EnsureCreatedAsync(keepAlive);

        using var gate = new Barrier(2);
        using var connection = new GatedConnection(connectionString, gate, TimeSpan.FromMilliseconds(250));

        // Explicit because the dialect is inferred from the connection's type name, and this is a
        // subclass — which DialectOf's own message anticipates.
        var store = new DapperAuditStore(connection, new DapperOptions { Dialect = SqlDialect.Sqlite });

        const int Many = 500;

        // One long append and one short one, both starting with the connection closed. Before the
        // gate in UnitOfWork, both opened it, both believed they had to close it, and the short one
        // got there while the long one was still issuing statements: the native handle went out
        // from under it with "SafeHandle cannot be null", and 40 of 501 entries were written. A
        // meaningless error and a half-written trail.
        var slow = Task.Run(() => store.AppendRangeAsync(
            [.. Enumerable.Range(0, Many).Select(i => Entry(i.ToString(CultureInfo.InvariantCulture)))]));

        var fast = Task.Run(() => store.AppendAsync(Entry("single")));

        await Task.WhenAll(slow, fast);

        using var count = new SqliteConnection(connectionString);
        count.Open();

        Assert.Equal(Many + 1, await count.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM \"AuditEntries\""));

        // Borrowed, not taken: it was closed when the store found it and is closed again.
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task A_connection_the_application_opened_is_left_open()
    {
        var connectionString = $"DataSource=file:{Guid.NewGuid():N}?mode=memory&cache=shared";

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        await DapperSchema.EnsureCreatedAsync(connection);

        var store = new DapperAuditStore(connection, new DapperOptions());

        await Task.WhenAll(
            Task.Run(() => store.AppendAsync(Entry("1"))),
            Task.Run(() => store.AppendAsync(Entry("2"))));

        // The store never opened it, so it must never close it — the application is still using it.
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM \"AuditEntries\""));
    }

    private static AuditEntry Entry(string entityId) => new()
    {
        Id            = Guid.NewGuid(),
        EntityName    = "Product",
        EntityId      = entityId,
        Action        = AuditAction.Created,
        CorrelationId = "c1",
        OccurredAt    = DateTime.UtcNow
    };
}
