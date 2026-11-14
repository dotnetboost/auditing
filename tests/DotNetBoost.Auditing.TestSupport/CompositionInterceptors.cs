using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace DotNetBoost.Auditing.TestSupport;

// Stand-ins for another library's SaveChanges interceptor on the same context, without referencing
// one. Each mimics a behaviour DotNetBoost.MultiLingual has, because that is the composition these
// tests exist for; none of them is a copy of it.

/// <summary>When <see cref="PostSaveWriterInterceptor"/> commits the transaction it opened.</summary>
public enum WriterCommit
{
    /// <summary>In its own <c>SavedChanges</c>, before any interceptor registered after it has run.</summary>
    InSavedChanges,

    /// <summary>From EF's <c>DbContext.SavedChanges</c> event, once every interceptor has finished.</summary>
    OnSavedChangesEvent
}

/// <summary>
/// Writes a row of its own after the save, through a nested <c>SaveChanges</c> issued from its
/// <c>SavedChanges</c>, the way a library storing data keyed on the entity's generated id must.
/// Opens a transaction in <c>SavingChanges</c> when there is none, and counts nested saves. Like
/// such a library, it has work only when the save changes an application entity: a save that only
/// writes audit entries gives it none.
/// </summary>
/// <param name="newRow">The row it writes; give it a store-generated key so the capture defers it.</param>
public sealed class PostSaveWriterInterceptor(Func<object> newRow) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, State> _states = new();

    /// <summary>When it commits the transaction it opened.</summary>
    public WriterCommit Commit { get; set; } = WriterCommit.OnSavedChangesEvent;

    /// <summary>Throw after writing its row and before committing, as a failed write would.</summary>
    public bool FailWrite { get; set; }

    /// <summary>How many rows it has written, committed or not.</summary>
    public int RowsWritten { get; private set; }

    /// <inheritdoc/>
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (!result.HasResult && eventData.Context is { } context)
            Saving(context, s => s.Owned = context.Database.BeginTransaction());

        return result;
    }

    /// <inheritdoc/>
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (!result.HasResult && eventData.Context is { } context && Saving(context, null))
            _states.GetOrCreateValue(context).Owned = await context.Database.BeginTransactionAsync(cancellationToken);

        return result;
    }

    /// <inheritdoc/>
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is { } context && Outermost(context) is { } state)
        {
            Write(context, state);
            Complete(context, state);
        }

        return result;
    }

    /// <inheritdoc/>
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && Outermost(context) is { } state)
        {
            await WriteAsync(context, state, cancellationToken);
            Complete(context, state);
        }

        return result;
    }

    /// <inheritdoc/>
    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Failed(eventData.Context);

    /// <inheritdoc/>
    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Failed(eventData.Context);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override void SaveChangesCanceled(DbContextEventData eventData) => Failed(eventData.Context);

    /// <inheritdoc/>
    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        Failed(eventData.Context);
        return Task.CompletedTask;
    }

    /// <summary>True when this starts an outermost save that should open a transaction.</summary>
    private bool Saving(DbContext context, Action<State>? open)
    {
        var state = _states.GetOrCreateValue(context);
        if (state.Writing)
            return false;

        if (state.Depth > 0 || state.Pending)
        {
            state.Depth++;
            return false;
        }

        state.Depth = 1;
        state.HasWork = context.ChangeTracker.Entries().Any(e =>
            e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
            && e.Entity is not (Core.Models.AuditEntry or Core.Models.AuditPropertyChange));

        var needs = state.HasWork
            && context.Database.CurrentTransaction is null
            && System.Transactions.Transaction.Current is null;
        if (needs)
            open?.Invoke(state);

        return needs;
    }

    private State? Outermost(DbContext context)
    {
        if (!_states.TryGetValue(context, out var state) || state.Writing || state.Depth == 0)
            return null;

        if (--state.Depth > 0)
        {
            if (state.Pending)
                state.NestedAwaitingEvent++;

            return null;
        }

        return state;
    }

    private void Write(DbContext context, State state)
    {
        if (!state.HasWork)
            return;

        state.Writing = true;
        try
        {
            context.Add(newRow());
            context.SaveChanges();
            RowsWritten++;
        }
        finally
        {
            state.Writing = false;
        }

        ThrowIfFailing();
    }

    private async Task WriteAsync(DbContext context, State state, CancellationToken ct)
    {
        if (!state.HasWork)
            return;

        state.Writing = true;
        try
        {
            context.Add(newRow());
            await context.SaveChangesAsync(ct);
            RowsWritten++;
        }
        finally
        {
            state.Writing = false;
        }

        ThrowIfFailing();
    }

    private void ThrowIfFailing()
    {
        if (FailWrite)
            throw new InvalidOperationException("The other library's write failed.");
    }

    private void Complete(DbContext context, State state)
    {
        if (state.Owned is null || Commit == WriterCommit.InSavedChanges)
        {
            state.Owned?.Commit();
            Finish(context, state);
            return;
        }

        state.Pending = true;
        state.Depth = 1;
        if (!state.Listening)
        {
            context.SavedChanges += OnSavedChanges;
            state.Listening = true;
        }
    }

    private void OnSavedChanges(object? sender, SavedChangesEventArgs e)
    {
        if (sender is not DbContext context || !_states.TryGetValue(context, out var state) || state.Writing || !state.Pending)
            return;

        if (state.NestedAwaitingEvent > 0)
        {
            state.NestedAwaitingEvent--;
            return;
        }

        state.Owned?.Commit();
        Finish(context, state);
    }

    private void Failed(DbContext? context)
    {
        if (context is null || !_states.TryGetValue(context, out var state) || state.Writing)
            return;

        if (state.Depth > 1)
        {
            state.Depth--;
            return;
        }

        state.Owned?.Rollback();
        Finish(context, state);
    }

    private void Finish(DbContext context, State state)
    {
        state.Owned?.Dispose();
        if (state.Listening)
            context.SavedChanges -= OnSavedChanges;

        _states.Remove(context);
    }

    private sealed class State
    {
        public int Depth { get; set; }
        public IDbContextTransaction? Owned { get; set; }
        public bool Writing { get; set; }
        public bool HasWork { get; set; }
        public bool Pending { get; set; }
        public bool Listening { get; set; }
        public int NestedAwaitingEvent { get; set; }
    }
}

/// <summary>
/// Under a retrying execution strategy with no caller transaction, suppresses the save and replays
/// it as one unit inside the strategy and a transaction of its own, putting the change tracker back
/// before each retry.
/// </summary>
public sealed class ReplayingInterceptor : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, object> _replaying = new();

    /// <summary>How many attempts fail after the save has run and before the commit.</summary>
    public int FailAttempts { get; set; }

    /// <summary>How many attempts ran.</summary>
    public int Attempts { get; private set; }

    /// <inheritdoc/>
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (!ShouldReplay(eventData.Context, result, out var context))
            return result;

        var snapshot = TrackerSnapshot.Take(context);
        _replaying.Add(context, new object());
        try
        {
            var saved = context.Database.CreateExecutionStrategy().Execute(
                context,
                (_, c) =>
                {
                    Attempts++;
                    using var transaction = c.Database.BeginTransaction();
                    try
                    {
                        var n = c.SaveChanges();
                        if (FailAttempts > 0)
                        {
                            FailAttempts--;
                            throw new TransientTestException();
                        }

                        transaction.Commit();
                        return n;
                    }
                    catch
                    {
                        transaction.Rollback();
                        snapshot.Restore(c);
                        throw;
                    }
                },
                verifySucceeded: null);

            return InterceptionResult<int>.SuppressWithResult(saved);
        }
        finally
        {
            _replaying.Remove(context);
        }
    }

    private bool ShouldReplay(DbContext? candidate, InterceptionResult<int> result, out DbContext context)
    {
        context = candidate!;
        return !result.HasResult
            && candidate is not null
            && !_replaying.TryGetValue(candidate, out _)
            && candidate.Database.CurrentTransaction is null
            && candidate.Database.CreateExecutionStrategy().RetriesOnFailure;
    }

    /// <inheritdoc/>
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (!ShouldReplay(eventData.Context, result, out var context))
            return result;

        var snapshot = TrackerSnapshot.Take(context);
        _replaying.Add(context, new object());
        try
        {
            var saved = await context.Database.CreateExecutionStrategy().ExecuteAsync(
                context,
                async (_, c, ct) =>
                {
                    Attempts++;
                    await using var transaction = await c.Database.BeginTransactionAsync(ct);
                    try
                    {
                        var n = await c.SaveChangesAsync(ct);
                        if (FailAttempts > 0)
                        {
                            FailAttempts--;
                            throw new TransientTestException();
                        }

                        await transaction.CommitAsync(ct);
                        return n;
                    }
                    catch
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                        snapshot.Restore(c);
                        throw;
                    }
                },
                verifySucceeded: null,
                cancellationToken);

            return InterceptionResult<int>.SuppressWithResult(saved);
        }
        finally
        {
            _replaying.Remove(context);
        }
    }

    /// <summary>The change tracker as it stood before the save.</summary>
    private sealed class TrackerSnapshot
    {
        private readonly HashSet<object> _tracked;
        private readonly List<(EntityEntry Entry, EntityState State, PropertyValues Current, PropertyValues? Original, HashSet<string> Modified, string[] Temporary)> _changed;

        private TrackerSnapshot(
            HashSet<object> tracked,
            List<(EntityEntry, EntityState, PropertyValues, PropertyValues?, HashSet<string>, string[])> changed)
        {
            _tracked = tracked;
            _changed = changed;
        }

        public static TrackerSnapshot Take(DbContext context)
        {
            var tracked = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var changed = new List<(EntityEntry, EntityState, PropertyValues, PropertyValues?, HashSet<string>, string[])>();

            foreach (var entry in context.ChangeTracker.Entries())
            {
                tracked.Add(entry.Entity);
                if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                {
                    changed.Add((
                        entry,
                        entry.State,
                        entry.CurrentValues.Clone(),
                        entry.State == EntityState.Added ? null : entry.OriginalValues.Clone(),
                        entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).ToHashSet(StringComparer.Ordinal),
                        entry.Properties.Where(p => p.IsTemporary).Select(p => p.Metadata.Name).ToArray()));
                }
            }

            return new TrackerSnapshot(tracked, changed);
        }

        public void Restore(DbContext context)
        {
            // Rows the failed attempt started tracking were rolled back with it.
            foreach (var entry in context.ChangeTracker.Entries().Where(e => !_tracked.Contains(e.Entity)).ToList())
                entry.State = EntityState.Detached;

            foreach (var (entry, state, current, original, modified, temporary) in _changed)
            {
                entry.State = state;

                // An added row's store-generated key goes back to the temporary value it had, so the
                // retry inserts without an explicit key: SQL Server refuses one for an identity.
                foreach (var name in temporary)
                {
                    var key = entry.Property(name);
                    key.CurrentValue = current[name];
                    key.IsTemporary = true;
                }

                foreach (var property in entry.Properties)
                {
                    if (!property.Metadata.IsPrimaryKey())
                        property.CurrentValue = current[property.Metadata];

                    if (original is not null)
                        property.OriginalValue = original[property.Metadata];

                    if (state == EntityState.Modified)
                        property.IsModified = modified.Contains(property.Metadata.Name);
                }
            }
        }
    }
}

/// <summary>The failure <see cref="TestRetryingStrategy"/> retries.</summary>
public sealed class TransientTestException() : Exception("A transient failure, forced by the test.");

/// <summary>A retrying strategy for providers that have none of their own, such as SQLite.</summary>
/// <param name="dependencies">EF's dependencies for the strategy.</param>
public sealed class TestRetryingStrategy(ExecutionStrategyDependencies dependencies)
    : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(1))
{
    /// <inheritdoc/>
    protected override bool ShouldRetryOn(Exception exception) => exception is TransientTestException;

    /// <inheritdoc/>
    protected override TimeSpan? GetNextDelay(Exception lastException) =>
        base.GetNextDelay(lastException) is null ? null : TimeSpan.Zero;
}

/// <summary>
/// Throws from its <c>SavedChanges</c> for the outermost save only, after every interceptor before
/// it has finished. Nested saves — the capture's own deferred write among them — pass.
/// </summary>
public sealed class ThrowAfterSaveInterceptor : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, StrongBox<int>> _depth = new();

    /// <inheritdoc/>
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Enter(eventData.Context);
        return result;
    }

    /// <inheritdoc/>
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Enter(eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc/>
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result) =>
        Leave(eventData.Context) ? throw new InvalidOperationException("Refused after the save.") : result;

    /// <inheritdoc/>
    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default) =>
        Leave(eventData.Context) ? throw new InvalidOperationException("Refused after the save.") : ValueTask.FromResult(result);

    /// <inheritdoc/>
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is { } context)
            _depth.Remove(context);
    }

    /// <inheritdoc/>
    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        SaveChangesFailed(eventData);
        return Task.CompletedTask;
    }

    private void Enter(DbContext? context)
    {
        if (context is not null)
            _depth.GetOrCreateValue(context).Value++;
    }

    private bool Leave(DbContext? context) =>
        context is not null && _depth.TryGetValue(context, out var depth) && depth.Value > 0 && --depth.Value == 0;
}

/// <summary>
/// Cancels the caller's token once the save has started, the way a request abort reaches EF:
/// after every <c>SavingChanges</c> hook, while the batch runs. Asynchronous only: a synchronous
/// save takes no token.
/// </summary>
public sealed class CancellingInterceptor : SaveChangesInterceptor
{
    /// <summary>The source to cancel on the next save, or <c>null</c> to let it run.</summary>
    public CancellationTokenSource? Cancel { get; set; }

    /// <inheritdoc/>
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Cancel?.Cancel();
        return ValueTask.FromResult(result);
    }
}

/// <summary>Refuses every save from <c>SavingChanges</c>, as a validating interceptor would.</summary>
public sealed class RefusingInterceptor : SaveChangesInterceptor
{
    /// <summary>Whether it refuses.</summary>
    public bool Active { get; set; } = true;

    /// <inheritdoc/>
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
        Active ? throw new InvalidOperationException("Refused by validation.") : result;

    /// <inheritdoc/>
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default) =>
        Active ? throw new InvalidOperationException("Refused by validation.") : ValueTask.FromResult(result);
}

/// <summary>Suppresses the save, as an interceptor that writes the data some other way would.</summary>
public sealed class SuppressingInterceptor : SaveChangesInterceptor
{
    /// <summary>Whether it suppresses.</summary>
    public bool Active { get; set; }

    /// <inheritdoc/>
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
        Active ? InterceptionResult<int>.SuppressWithResult(0) : result;

    /// <inheritdoc/>
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Active ? InterceptionResult<int>.SuppressWithResult(0) : result);
}

/// <summary>Saves through either of EF's paths, so one test covers both.</summary>
public static class SavePath
{
    /// <summary>
    /// <c>SaveChanges()</c> when <paramref name="sync"/>, else <c>SaveChangesAsync()</c>. The
    /// synchronous path has its own interceptor hooks, and the capture implements each twice.
    /// </summary>
    public static Task<int> SaveAsync(this DbContext context, bool sync)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!sync)
            return context.SaveChangesAsync();

        try
        {
            return Task.FromResult(context.SaveChanges());
        }
        catch (Exception exception)
        {
            return Task.FromException<int>(exception);
        }
    }
}
