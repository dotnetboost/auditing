using DotNetBoost.Auditing.Core;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Metadata;
using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace DotNetBoost.Auditing.EntityFrameworkCore;

/// <summary>
/// The capture. Nothing in a repository or a handler needs to know about auditing: the entity's
/// attributes and one line on the context are the whole of the opt-in.
///
/// <para><b>Most entries are written in the caller's own save.</b> A <c>Modified</c> or
/// <c>Deleted</c> entity already has its key while <c>SavingChanges</c> runs, so its entry is
/// tracked there and goes to the database in the same <c>SaveChanges</c>, in the same batch and the
/// same transaction. The change and its trail land together or neither does, and no transaction had
/// to be opened to make that true.</para>
///
/// <para><b>A creation with a store-generated key cannot be.</b> Its id does not exist until the
/// INSERT has run, and <see cref="AuditEntry.EntityId"/> needs it. Those entries are deferred to
/// <c>SavedChanges</c>, where every key is populated, and written by a second save. To keep them
/// atomic with the change they describe, this interceptor opens a transaction before the primary
/// save when the caller has none, and commits it only once the deferred entries are in.</para>
///
/// <para><b>The two cases it cannot make atomic</b>, both of which it still records rather than
/// refusing — stated here because the library's headline guarantee has exactly these holes:</para>
/// <list type="bullet">
/// <item><description>
/// A <b>retrying execution strategy</b> (<c>EnableRetryOnFailure</c>) with no caller transaction.
/// Such a strategy refuses a transaction it did not start, because it could not replay it. The
/// deferred entries are then written in their own transaction, so a failure between the two leaves
/// the creation committed and its <c>Created</c> entry missing. Wrap the unit of work in
/// <c>Database.CreateExecutionStrategy().ExecuteAsync(...)</c> with your own transaction and the
/// hole closes.
/// </description></item>
/// <item><description>
/// A <b>non-relational provider</b>, which has no transactions to open at all.
/// </description></item>
/// </list>
///
/// <para>Both disappear if the entity's key is assigned in code — a <c>Guid</c> set by the
/// application, HiLo, a sequence — because then nothing is deferred.</para>
///
/// <para><b>State is per <see cref="DbContext"/> instance</b>, so several contexts can share one
/// scope. Context pooling (<c>AddDbContextPool</c>) is not supported: pooled options are built once,
/// from the root provider, and cannot carry per-request services.</para>
/// </summary>
/// <param name="services">The scope's provider; the engine is resolved lazily, on first save.</param>
internal sealed class AuditingSaveChangesInterceptor(IServiceProvider services) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, SaveState> _states = new();

    // Resolved on first use, not injected: the recorder depends on the store, the store on the
    // DbContext, and this interceptor is created while that DbContext is still being built.
    private IAuditManager? _audit;
    private IAuditManager Audit => _audit ??= services.GetRequiredService<IAuditManager>();

    private AuditingOptions? _options;
    private AuditingOptions Options => _options ??= services.GetRequiredService<AuditingOptions>();

    // The engine itself, not the public interface: this capture decides to skip an entity before
    // the engine sees it, and the reason for that decision is the engine's to give. Resolved from
    // the same scope as IAuditManager, so it is the same instance behind it.
    private AuditRecorder? _recorder;
    private AuditRecorder Recorder => _recorder ??= services.GetRequiredService<AuditRecorder>();

    /// <inheritdoc/>
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is { } context && Begin(context) is { } state)
        {
            Capture(context, state);

            if (state.Deferred.Count > 0 && NeedsOwnTransaction(context))
                state.OwnedTransaction = context.Database.BeginTransaction();
        }

        return result;
    }

    /// <inheritdoc/>
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is { } context && Begin(context) is { } state)
        {
            Capture(context, state);

            if (state.Deferred.Count > 0 && NeedsOwnTransaction(context))
            {
                state.OwnedTransaction = await context.Database
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        // IsRecording means this completion belongs to the interceptor's OWN deferred save. Without
        // this check that save's SavedChanges would record the deferred entries again, save again,
        // and recurse until the stack ran out.
        if (eventData.Context is not { } context
            || !_states.TryGetValue(context, out var state)
            || state.IsRecording)
        {
            return result;
        }

        try
        {
            if (state.Deferred.Count > 0)
            {
                state.IsRecording = true;
                RecordDeferred(state);

                // Cleared before the save, not after: nothing may re-run these, whatever happens
                // next.
                state.Deferred.Clear();
                context.SaveChanges();
            }

            state.OwnedTransaction?.Commit();
        }
        finally
        {
            Finish(context, state);
        }

        return result;
    }

    /// <inheritdoc/>
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        // See SavedChanges: IsRecording means this is the interceptor's own deferred save completing,
        // and running the deferred pass again would recurse without end.
        if (eventData.Context is not { } context
            || !_states.TryGetValue(context, out var state)
            || state.IsRecording)
        {
            return result;
        }

        try
        {
            if (state.Deferred.Count > 0)
            {
                // Guards the re-entry: this save raises SavingChanges and SavedChanges again, and
                // the only thing pending is the trail itself.
                state.IsRecording = true;
                RecordDeferred(state);
                state.Deferred.Clear();
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            if (state.OwnedTransaction is { } transaction)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Finish(context, state);
        }

        return result;
    }

    /// <inheritdoc/>
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is not { } context
            || !_states.TryGetValue(context, out var state)
            || state.IsRecording)
        {
            // A failure in the deferred save itself unwinds through SavedChanges, whose finally
            // disposes the uncommitted transaction — rolling the change back with it.
            return;
        }

        try
        {
            // Rolls back the change as well as its trail, which is the point of having opened it.
            state.OwnedTransaction?.Rollback();
        }
        finally
        {
            Finish(context, state);
        }
    }

    /// <inheritdoc/>
    public override async Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is not { } context
            || !_states.TryGetValue(context, out var state)
            || state.IsRecording)
        {
            return;
        }

        try
        {
            if (state.OwnedTransaction is { } transaction)
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Finish(context, state);
        }
    }

    /// <summary>
    /// The state for this save, or <c>null</c> when this save is the interceptor's own deferred
    /// write and must not be captured again.
    /// </summary>
    private SaveState? Begin(DbContext context)
    {
        if (_states.TryGetValue(context, out var existing))
            return existing.IsRecording ? null : existing;

        var state = new SaveState();
        _states.Add(context, state);
        return state;
    }

    private void Finish(DbContext context, SaveState state)
    {
        state.OwnedTransaction?.Dispose();
        state.OwnedTransaction = null;
        state.IsRecording      = false;
        state.Deferred.Clear();

        _states.Remove(context);
    }

    /// <summary>
    /// Reads the change tracker: entries whose key is already known are recorded now, into this
    /// save; the rest are held for <c>SavedChanges</c>.
    /// </summary>
    /// <remarks>
    /// The tracker is materialised to a list first. Recording adds entities to it, and enumerating
    /// a collection while adding to it is how a capture becomes a capture of itself.
    /// </remarks>
    private void Capture(DbContext context, SaveState state)
    {
        var entries = context.ChangeTracker.Entries().ToList();

        foreach (var entry in entries)
        {
            if (EntityDiff.ActionFor(entry.State) is not { } action)
                continue;

            if (!EntityDiff.IsAddressable(entry))
                continue;

            // Reported here rather than left to the engine, because this capture skips the entity
            // before the engine ever sees it — that is the point of asking, since the alternative
            // is diffing an entity whose changes will be thrown away. Without this, every
            // suppression in an EF Core application would be invisible: the interceptor writes
            // entries for changes nobody asked it about, so there is no return value for it to
            // hand back. The reason comes from the engine so that the rule stays in one place.
            if (Recorder.SuppressionFor(entry.Metadata.ClrType) is { } suppression)
            {
                Recorder.ReportSuppressed(
                    suppression,
                    AuditEntityNames.For(entry.Metadata.ClrType),
                    // Empty when the key arrives with the INSERT: this entity was skipped before it
                    // was ever addressed, and inventing a placeholder would read as a real key.
                    TrackedKeys.TryGetKey(entry) ?? string.Empty,
                    action);

                continue;
            }

            var changes = EntityDiff.Build(entry, action, Options.RecordUpdatesWithoutBeforeImage);

            // Nothing moved. Left to the engine this would return null anyway, and with the same
            // report — but caught here it also saves deferring an entry that will turn out to be
            // empty, so the report is made here too rather than lost to the shortcut.
            if (action == AuditAction.Updated && changes.Count == 0)
            {
                Recorder.ReportSuppressed(
                    AuditSuppression.NothingChanged,
                    AuditEntityNames.For(entry.Metadata.ClrType),
                    TrackedKeys.TryGetKey(entry) ?? string.Empty,
                    action);

                continue;
            }

            var write = new AuditWrite
            {
                EntityName = AuditEntityNames.For(entry.Metadata.ClrType),
                // A placeholder only for the deferred ones, replaced below once the key exists. It
                // is never recorded: RecordDeferred builds the final write from the entry.
                EntityId   = TrackedKeys.TryGetKey(entry) ?? string.Empty,
                EntityType = entry.Metadata.ClrType,
                Action     = action,
                Changes    = changes,
                // One value for the whole save, so every entry it produces is identifiable as one
                // edit — including the deferred ones written afterwards, and including saves made
                // outside a request, where there is no ambient activity to borrow an id from.
                CorrelationId = state.CorrelationId
            };

            if (write.EntityId.Length == 0)
            {
                // An Added entity with a store-generated key. Its id arrives with the INSERT.
                state.Deferred.Add(new Deferred(entry, write));
                continue;
            }

            Record(write);
        }
    }

    private void RecordDeferred(SaveState state)
    {
        foreach (var (entry, write) in state.Deferred)
        {
            var entityId = TrackedKeys.TryGetKey(entry)
                ?? throw new InvalidOperationException(
                    $"{entry.Metadata.DisplayName()} still has no key after the save, so its creation " +
                    "cannot be recorded. Assign the key in code, or exclude the entity with [AuditIgnore].");

            Record(write with { EntityId = entityId });
        }
    }

    /// <summary>
    /// Records through the engine, which applies the model's rules, formats the values and stamps
    /// the actor, correlation and time.
    /// </summary>
    /// <remarks>
    /// Awaited by completion rather than with <c>await</c>, so the synchronous
    /// <c>SaveChanges</c> path shares this code. With the EF Core store that is exact rather than
    /// hopeful: appending only tracks the entity, so the task is already complete when it comes
    /// back. A store that genuinely went to the database here would be refused rather than blocked
    /// on.
    /// </remarks>
    private void Record(AuditWrite write)
    {
        var recording = Audit.RecordAsync(write);

        if (!recording.IsCompleted)
        {
            throw new InvalidOperationException(
                "The audit store did not complete synchronously. The EF Core capture tracks entries on " +
                "the context rather than writing them, so it cannot block on a store that goes to the " +
                "database during Append. Register the EF Core store, or record explicitly through " +
                "IAuditManager instead of through the interceptor.");
        }

        // Observes the result, so a failure surfaces here rather than as an unobserved exception.
        recording.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Whether this interceptor should own a transaction for the deferred pass.
    /// </summary>
    private static bool NeedsOwnTransaction(DbContext context) =>
        context.Database.CurrentTransaction is null
        // A non-relational provider has none to open.
        && context.Database.IsRelational()
        // A retrying strategy refuses a transaction it did not start, because it could not replay
        // it. Documented on the class rather than worked around here.
        && !context.Database.CreateExecutionStrategy().RetriesOnFailure;

    /// <summary>What one save is holding. Lives for the length of that save and no longer.</summary>
    private sealed class SaveState
    {
        /// <summary>Creations whose key arrives with the INSERT.</summary>
        public List<Deferred> Deferred { get; } = [];

        /// <summary>The transaction this interceptor opened, or <c>null</c> when the caller had one.</summary>
        public IDbContextTransaction? OwnedTransaction { get; set; }

        /// <summary>Whether the save now in flight is the interceptor's own deferred write.</summary>
        public bool IsRecording { get; set; }

        /// <summary>
        /// The id tying this save's entries together. Taken from the ambient activity when there is
        /// one, so the trail joins the traces of the request that caused it, and otherwise minted
        /// here — per save, not per entry.
        /// </summary>
        public string CorrelationId { get; } =
            Activity.Current?.Id ?? Guid.NewGuid().ToString("n", CultureInfo.InvariantCulture);
    }

    private readonly record struct Deferred(EntityEntry Entry, AuditWrite Write);
}
