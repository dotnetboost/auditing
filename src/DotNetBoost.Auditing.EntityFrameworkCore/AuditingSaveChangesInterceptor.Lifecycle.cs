using DotNetBoost.Auditing.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace DotNetBoost.Auditing.EntityFrameworkCore;

// When a save starts, nests, ends and fails — what lets the capture share a context with another
// library's SaveChanges interceptor in either registration order. Ported from the same design in
// DotNetBoost.MultiLingual's save interceptor, not shared with it.
//
// The phases of one save:
//
//   Saving          outermost SavingChanges: start over from anything left behind, capture, open a
//                   transaction if creations are deferred and none is open.
//   Nested          a SavingChanges while a save of ours is in flight and EF has already accepted its
//                   changes (or while it awaits the event): depth++, capture into the same state.
//   Saved           outermost SavedChanges: write the deferred entries, then commit — or, when this
//                   interceptor owns the transaction and another SaveChanges interceptor follows it,
//                   leave it open for EF's DbContext.SavedChanges event.
//   Awaiting event  a save starting now is nested, not a new outermost one.
//   Done            the outer save's DbContext.SavedChanges event: write what nested saves deferred,
//                   commit, finish.
//   Failed          SaveChangesFailed, SaveChangesCanceled or ThrowingConcurrencyException: a nested
//                   save only unwinds its depth; the outermost rolls back and finishes.
internal sealed partial class AuditingSaveChangesInterceptor
{
    // Every entry this capture created, held weakly. An entry still Added when an outermost save
    // starts belongs to a save that never completed — refused in SavingChanges, failed, or rolled
    // back and put back by another interceptor's tracker snapshot — and would otherwise be written
    // a second time beside the fresh capture's.
    private readonly ConditionalWeakTable<AuditEntry, object> _created = new();

    /// <summary>
    /// Enters a save. Returns the state to capture into and that the caller may open a transaction
    /// for, or <c>null</c> when there is nothing to do.
    /// </summary>
    private SaveState? Saving(DbContext context, InterceptionResult<int> result)
    {
        var state = _states.GetOrCreateValue(context);

        // The interceptor's own deferred write.
        if (state.IsRecording)
            return null;

        if (result.HasResult)
        {
            // Another interceptor suppressed this save and accounts for it: nothing reaches the
            // database through EF. Counted when it is nested, so its SavedChanges does not end the
            // outer save early.
            if (IsNested(state))
                state.Depth++;

            return null;
        }

        if (IsNested(state))
        {
            state.Depth++;
            try
            {
                Capture(context, state, context.ChangeTracker.Entries().ToList());
            }
            catch
            {
                state.Depth--;
                throw;
            }

            return state;
        }

        // An outermost save. Whatever is left from an earlier one did not complete.
        //
        // The tracker is read once, here, and shared: every read of it runs EF's change detection
        // over every tracked entity, and a save should pay for that once, not once per step.
        var entries = context.ChangeTracker.Entries().ToList();
        StartOver(context, state, entries);
        state.Depth = 1;

        try
        {
            foreach (var entry in entries)
            {
                if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                    && entry.Entity is not (AuditEntry or AuditPropertyChange))
                {
                    state.Changed.Add((entry, entry.State));
                }
            }

            Capture(context, state, entries);
            return state;
        }
        catch
        {
            StartOver(context, state, entries: null);
            throw;
        }
    }

    /// <summary>
    /// True when a save starting now is nested inside one of ours: that save is awaiting EF's
    /// event, or it is in flight and EF has already saved and accepted its changes — the only moment
    /// another interceptor can save from inside it. Anything else is a save that never completed
    /// (EF raises nothing when an interceptor's <c>SavingChanges</c> throws), or one that another
    /// interceptor is replaying from scratch, and the new save starts over.
    /// </summary>
    /// <remarks>
    /// An outer save that changed nothing EF tracks gives nothing to tell the two apart by, and is
    /// treated as completed; it captured nothing, so starting over loses nothing.
    /// </remarks>
    private static bool IsNested(SaveState state) =>
        state.CommitPending
        || state.Depth > 0
        && state.Changed.Count > 0
        && state.Changed.TrueForAll(c =>
            c.Entry.State == (c.State == EntityState.Deleted ? EntityState.Detached : EntityState.Unchanged));

    /// <summary>
    /// Leaves a save that completed. Returns the state when it was the outermost, which does the
    /// work, or <c>null</c>.
    /// </summary>
    private SaveState? Saved(DbContext context)
    {
        if (!_states.TryGetValue(context, out var state) || state.IsRecording)
            return null;

        // Depth 0: a save this interceptor never entered — suppressed by another one.
        if (state.Depth == 0 || --state.Depth > 0)
        {
            // EF raises the DbContext.SavedChanges event for a nested save too, before the outer
            // save's; that one must not commit.
            if (state.CommitPending && state.Depth > 0)
                state.NestedSavesAwaitingEvent++;

            return null;
        }

        return state;
    }

    /// <summary>
    /// Leaves the commit for EF's <c>DbContext.SavedChanges</c> event when this interceptor owns the
    /// transaction and another <c>SaveChanges</c> interceptor follows it. Returns whether it did.
    /// </summary>
    /// <remarks>
    /// EF runs interceptors in the order they were added, and raises the event once every
    /// <c>SavedChanges</c> has returned — or not at all when one throws, in which case every
    /// interceptor sees <c>SaveChangesFailed</c> and this one rolls back. Committing here would put
    /// the later interceptor's post-save work outside the transaction. The event is synchronous, so
    /// that commit is one blocking round trip, paid only when another interceptor follows.
    /// </remarks>
    private bool DeferCommit(DbContext context, SaveState state)
    {
        if (state.OwnedTransaction is null || !HasSaveInterceptorAfterThis(context))
            return false;

        state.CommitPending = true;
        state.Depth = 1;

        if (!state.ListensToSavedChanges)
        {
            context.SavedChanges += OnSavedChanges;
            state.ListensToSavedChanges = true;
        }

        return true;
    }

    /// <summary>Whether a <c>SaveChanges</c> interceptor was added to the context after this one.</summary>
    private bool HasSaveInterceptorAfterThis(DbContext context)
    {
        var interceptors = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors;
        if (interceptors is null)
            return false;

        var seenThis = false;
        foreach (var interceptor in interceptors)
        {
            if (ReferenceEquals(interceptor, this))
                seenThis = true;
            else if (seenThis && interceptor is ISaveChangesInterceptor)
                return true;
        }

        return false;
    }

    /// <summary>EF's <c>DbContext.SavedChanges</c> event: every interceptor is done, so commit.</summary>
    private void OnSavedChanges(object? sender, SavedChangesEventArgs e)
    {
        if (sender is not DbContext context
            || !_states.TryGetValue(context, out var state)
            || state.IsRecording
            || !state.CommitPending)
        {
            return;
        }

        if (state.NestedSavesAwaitingEvent > 0)
        {
            state.NestedSavesAwaitingEvent--;
            return;
        }

        try
        {
            // Creations a nested save added after the deferred pass ran. Synchronous, as the event is.
            WriteDeferred(context, state);
            state.OwnedTransaction?.Commit();
            Finish(context, state);
        }
        catch
        {
            RollbackQuietly(state);
            Finish(context, state);
            throw;
        }
    }

    /// <summary>Writes the deferred creations' entries, now that their keys exist.</summary>
    private void WriteDeferred(DbContext context, SaveState state)
    {
        if (!BeginDeferredWrite(context, state))
            return;

        try
        {
            context.SaveChanges();
        }
        finally
        {
            state.IsRecording = false;
        }
    }

    /// <summary>See <see cref="WriteDeferred"/>.</summary>
    private async Task WriteDeferredAsync(DbContext context, SaveState state, CancellationToken cancellationToken)
    {
        if (!BeginDeferredWrite(context, state))
            return;

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            state.IsRecording = false;
        }
    }

    /// <summary>
    /// Records the deferred entries into the context and marks the save that will write them as
    /// this interceptor's own. Returns <c>false</c> when there is nothing deferred.
    /// </summary>
    private bool BeginDeferredWrite(DbContext context, SaveState state)
    {
        if (state.Deferred.Count == 0)
            return false;

        // Relied on a transaction someone else opened, and it is gone or replaced: they committed
        // before this point, and these entries can no longer be atomic with the change.
        if (state.ReliedOn is { } reliedOn && !ReferenceEquals(reliedOn, context.Database.CurrentTransaction))
        {
            state.ReliedOn = null;
            Recorder.AtomicityLost(state.CorrelationId, state.Deferred.Count);
        }

        // This save raises SavingChanges and SavedChanges again, and the only thing pending is the
        // trail itself. Without the flag it would record the deferred entries again, save again,
        // and recurse until the stack ran out.
        state.IsRecording = true;
        try
        {
            RecordDeferred(context, state);
        }
        catch
        {
            state.IsRecording = false;
            throw;
        }

        // Cleared before the save, not after: nothing may re-run these, whatever happens next.
        state.Deferred.Clear();
        return true;
    }

    /// <summary>
    /// Arranges the rollback for a concurrency conflict, which cannot happen in the hook that
    /// reports it.
    /// </summary>
    /// <remarks>
    /// EF raises <c>ThrowingConcurrencyException</c> while it is still reading the batch's results.
    /// The connection is busy, so a <c>ROLLBACK</c> sent then fails on PostgreSQL ("a command is
    /// already in progress") and SQL Server ("an open DataReader"), and its error replaces the
    /// concurrency exception. SQLite does not mind, which is how this hid. EF raises its
    /// <c>DbContext.SaveChangesFailed</c> event once the reader is closed, just before it throws,
    /// and the rollback happens there.
    /// </remarks>
    private void RollBackAfterConflict(DbContext? context)
    {
        if (context is null
            || !_states.TryGetValue(context, out var state)
            || state.IsRecording
            || state.ListensToSaveChangesFailed)
        {
            return;
        }

        context.SaveChangesFailed += OnSaveChangesFailed;
        state.ListensToSaveChangesFailed = true;
    }

    /// <summary>EF's <c>DbContext.SaveChangesFailed</c> event, after a concurrency conflict.</summary>
    private void OnSaveChangesFailed(object? sender, SaveChangesFailedEventArgs e) => Abort(sender as DbContext);

    /// <summary>Ends the save after a failure, a cancellation or a concurrency conflict.</summary>
    private void Abort(DbContext? context)
    {
        if (LeaveFailed(context) is not { } state)
            return;

        try
        {
            // Rolls back the change as well as its trail, which is the point of having opened it.
            RollbackQuietly(state);
        }
        finally
        {
            Finish(context!, state);
        }
    }

    /// <summary>See <see cref="Abort"/>.</summary>
    private async Task AbortAsync(DbContext? context)
    {
        if (LeaveFailed(context) is not { } state)
            return;

        try
        {
            await RollbackQuietlyAsync(state).ConfigureAwait(false);
        }
        finally
        {
            Finish(context!, state);
        }
    }

    /// <summary>
    /// Leaves a save that failed. Returns the state when it was the outermost, which rolls back. A
    /// nested save's failure is reported to whoever issued it, and the outer save goes on as that
    /// caller decides.
    /// </summary>
    private SaveState? LeaveFailed(DbContext? context)
    {
        // A failure in the deferred save itself unwinds through SavedChanges, which rolls back.
        if (context is null || !_states.TryGetValue(context, out var state) || state.IsRecording)
            return null;

        if (state.Depth > 1)
        {
            state.Depth--;
            return null;
        }

        // Already finished: the failure is the outer save reporting what SavedChanges threw.
        if (state.Depth == 0 && !state.CommitPending)
            return null;

        return state;
    }

    /// <summary>
    /// Puts down whatever an earlier save left on this context: rolls back its transaction, and
    /// detaches the entries it captured but never wrote.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="state">Its state.</param>
    /// <param name="entries">
    /// The tracker as the caller has just read it, or <c>null</c> to read it here — after a failed
    /// capture, whose own entries the caller's list does not hold.
    /// </param>
    private void StartOver(DbContext context, SaveState state, List<EntityEntry>? entries)
    {
        RollbackQuietly(state);
        Finish(context, state);

        // A list, not the live tracker: detaching an entry changes what is being enumerated.
        foreach (var entry in entries ?? context.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is not AuditEntry audit
                || entry.State != EntityState.Added
                || !_created.TryGetValue(audit, out _))
            {
                continue;
            }

            // A copy: detaching a change takes it out of the entry's collection.
            foreach (var change in audit.Changes.ToList())
            {
                var changeEntry = context.Entry(change);
                if (changeEntry.State == EntityState.Added)
                    changeEntry.State = EntityState.Detached;
            }

            entry.State = EntityState.Detached;
        }
    }

    /// <summary>Remembers an entry this capture created, for <see cref="StartOver"/>.</summary>
    private void Created(AuditEntry entry) => _created.AddOrUpdate(entry, entry);

    /// <summary>
    /// Rolls back on a failure path, where the failure being reported is the one the caller needs.
    /// </summary>
    /// <remarks>
    /// Every caller is unwinding a save that already failed, inside EF's own failure handling or a
    /// <c>catch</c>: an exception from the rollback would replace that failure, and the caller
    /// would see "connection is broken" instead of why the save failed. Disposing the transaction,
    /// which <see cref="Finish"/> does next, rolls back whatever is left, and a server whose
    /// connection is gone has rolled back already.
    /// </remarks>
    private static void RollbackQuietly(SaveState state)
    {
        try
        {
            state.OwnedTransaction?.Rollback();
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            // See remarks.
        }
    }

    /// <summary>See <see cref="RollbackQuietly"/>.</summary>
    private static async Task RollbackQuietlyAsync(SaveState state)
    {
        if (state.OwnedTransaction is not { } transaction)
            return;

        try
        {
            // Without the save's token: when the save was cancelled that token is already
            // cancelled, and a rollback that refuses to run is how a transaction stays open.
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            // See RollbackQuietly.
        }
    }

    private void Finish(DbContext context, SaveState state)
    {
        state.OwnedTransaction?.Dispose();

        if (state.ListensToSavedChanges)
            context.SavedChanges -= OnSavedChanges;

        if (state.ListensToSaveChangesFailed)
            context.SaveChangesFailed -= OnSaveChangesFailed;

        state.Reset();
    }

    /// <summary>What one save is holding, from its outermost <c>SavingChanges</c> to its end.</summary>
    private sealed class SaveState
    {
        /// <summary>How many saves of this context are in flight, the outermost included.</summary>
        public int Depth { get; set; }

        /// <summary>The entries the outermost save changes, and their state before it.</summary>
        public List<(EntityEntry Entry, EntityState State)> Changed { get; } = [];

        /// <summary>Creations whose key arrives with the INSERT.</summary>
        public List<Deferred> Deferred { get; } = [];

        /// <summary>The transaction this interceptor opened, or <c>null</c> when the caller had one.</summary>
        public IDbContextTransaction? OwnedTransaction { get; set; }

        /// <summary>Someone else's transaction the deferred entries rely on.</summary>
        public IDbContextTransaction? ReliedOn { get; set; }

        /// <summary>Whether the save now in flight is the interceptor's own deferred write.</summary>
        public bool IsRecording { get; set; }

        /// <summary>Whether the commit waits for EF's <c>DbContext.SavedChanges</c> event.</summary>
        public bool CommitPending { get; set; }

        /// <summary>Nested saves that completed while the commit waits, whose events come first.</summary>
        public int NestedSavesAwaitingEvent { get; set; }

        /// <summary>Whether <see cref="OnSavedChanges"/> is subscribed to the context.</summary>
        public bool ListensToSavedChanges { get; set; }

        /// <summary>Whether <see cref="OnSaveChangesFailed"/> is subscribed, after a concurrency conflict.</summary>
        public bool ListensToSaveChangesFailed { get; set; }

        /// <summary>
        /// The id tying this save's entries together. Taken from the ambient activity when there is
        /// one, so the trail joins the traces of the request that caused it, and otherwise minted
        /// here — per save, not per entry.
        /// </summary>
        public string CorrelationId { get; private set; } = NewCorrelationId();

        public void Reset()
        {
            Depth = 0;
            Changed.Clear();
            Deferred.Clear();
            OwnedTransaction = null;
            ReliedOn = null;
            IsRecording = false;
            CommitPending = false;
            NestedSavesAwaitingEvent = 0;
            ListensToSavedChanges = false;
            ListensToSaveChangesFailed = false;
            CorrelationId = NewCorrelationId();
        }

        private static string NewCorrelationId() =>
            Activity.Current?.Id ?? Guid.NewGuid().ToString("n", CultureInfo.InvariantCulture);
    }
}
