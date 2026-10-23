using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;

namespace DotNetBoost.Auditing.Dapper;

/// <summary>
/// Borrows the application's connection: opens it when it is closed, leaves it as it was found, and
/// lets only one of this store's operations use it at a time.
/// </summary>
/// <remarks>
/// The store is scoped and the connection belongs to the application, which may have opened it, may
/// be about to, and may be in a transaction on it — so every statement borrows it rather than
/// owning it.
/// <para>
/// <b>One at a time, and that is not a performance decision.</b> An ADO.NET connection cannot run
/// two commands at once, so two operations on one connection have to take turns whatever this type
/// does; the choice is only whether they take turns or corrupt each other. They used to corrupt
/// each other: "open if closed, close if I opened it" is a check-then-act on shared state, so two
/// concurrent calls could both find the connection closed, both open it, and then the first to
/// finish would close it while the second was still issuing statements. The window is as wide as a
/// connection takes to open, which for anything but SQLite is a network round trip — the slowest
/// thing in ADO.NET, and therefore the easiest race in it to lose.
/// </para>
/// <para>
/// What that cost: against SQLite, two concurrent appends produced
/// <c>ArgumentNullException: SafeHandle cannot be null</c> from inside the native driver, and 40 of
/// 20,001 entries. A meaningless error and a half-written trail, which for an auditing library is
/// the worst pair of outcomes available. <c>ProviderTests</c> has that scenario as a test.
/// </para>
/// <para>
/// <b>What this cannot fix.</b> The gate only knows about operations that go through it. If the
/// application runs its own commands on the same connection at the same time — a
/// <c>Task.WhenAll</c> over two repositories that share the scope's <see cref="IDbConnection"/> —
/// that is two concurrent commands on one connection and ADO.NET does not allow it, whoever issued
/// them. Give parallel work its own connection.
/// </para>
/// </remarks>
internal static class UnitOfWork
{
    /// <summary>
    /// One gate per connection, for as long as that connection lives.
    /// </summary>
    /// <remarks>
    /// Keyed on the connection rather than held by the store, because the two are not one to one:
    /// <c>WithTransaction</c> returns a second store over the same connection, and both must queue
    /// behind the same gate. A <see cref="ConditionalWeakTable{TKey, TValue}"/> so a connection the
    /// application disposes takes its gate with it; the semaphores are never disposed, which costs
    /// nothing because <see cref="SemaphoreSlim"/> allocates no wait handle unless asked for one.
    /// </remarks>
    private static readonly ConditionalWeakTable<IDbConnection, SemaphoreSlim> Gates = new();

    /// <summary>Opens <paramref name="connection"/> if it is closed. Returns whether it did.</summary>
    /// <remarks>
    /// Call it through <see cref="RunAsync"/> unless there is a reason not to: on its own it is the
    /// check-then-act half of the race described on this type, with nothing serialising it.
    /// </remarks>
    internal static async Task<bool> OpenIfClosedAsync(IDbConnection connection, CancellationToken ct)
    {
        if (connection.State != ConnectionState.Closed)
            return false;

        if (connection is DbConnection db)
            await db.OpenAsync(ct).ConfigureAwait(false);
        else
            connection.Open();

        return true;
    }

    /// <summary>
    /// Runs <paramref name="body"/> with the connection open and to itself, closing it again if it
    /// opened it.
    /// </summary>
    /// <remarks>
    /// Not re-entrant: <paramref name="body"/> must use the connection directly rather than calling
    /// back through here, which would wait on a gate it is already holding. Every caller in this
    /// package does.
    /// </remarks>
    internal static async Task<T> RunAsync<T>(IDbConnection connection, Func<Task<T>> body, CancellationToken ct)
    {
        var gate = Gates.GetValue(connection, static _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // Inside the gate, so the state this reads cannot change under it and the close below
            // cannot land while another operation is mid-statement.
            var opened = await OpenIfClosedAsync(connection, ct).ConfigureAwait(false);

            try
            {
                return await body().ConfigureAwait(false);
            }
            finally
            {
                if (opened)
                    connection.Close();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// As <see cref="RunAsync{T}"/>, for work with nothing to return.
    /// </summary>
    internal static async Task RunAsync(IDbConnection connection, Func<Task> body, CancellationToken ct) =>
        await RunAsync<bool>(
            connection,
            async () =>
            {
                await body().ConfigureAwait(false);
                return true;
            },
            ct)
            .ConfigureAwait(false);
}
