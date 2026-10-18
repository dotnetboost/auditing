using System.Data;
using System.Data.Common;

namespace DotNetBoost.Auditing.Dapper;

/// <summary>
/// Opens a connection when it is closed, and leaves it as it was found. The store is scoped and the
/// connection belongs to the application, which may have opened it, may be about to, and may be in
/// a transaction on it — so every statement borrows it rather than owning it.
/// </summary>
internal static class UnitOfWork
{
    /// <summary>Opens <paramref name="connection"/> if it is closed. Returns whether it did.</summary>
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

    /// <summary>Runs <paramref name="body"/> with the connection open, closing it again if it opened it.</summary>
    internal static async Task<T> RunAsync<T>(IDbConnection connection, Func<Task<T>> body, CancellationToken ct)
    {
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
}
