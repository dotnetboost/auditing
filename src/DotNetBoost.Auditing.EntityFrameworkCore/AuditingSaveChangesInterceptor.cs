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
/// <para>Inside an ambient <c>TransactionScope</c> it opens nothing: the deferred save enlists in
/// the scope like the primary one, and commits or rolls back with it.</para>
///
/// <para><b>Beside another library's <c>SaveChanges</c> interceptor</b>, in either order: a save the
/// other issues from inside this one is counted as nested, and when this interceptor owns the
/// transaction and another follows it, the commit waits for EF's <c>DbContext.SavedChanges</c>
/// event. See <c>AuditingSaveChangesInterceptor.Lifecycle.cs</c>.</para>
///
/// <para><b>State is per <see cref="DbContext"/> instance</b>, and every entry is added to the
/// context being saved, so several contexts can share one scope. Each must map the audit tables
/// (<c>ApplyAuditing()</c>). Context pooling (<c>AddDbContextPool</c>) is not supported: pooled options are built once,
/// from the root provider, and cannot carry per-request services.</para>
/// </summary>
/// <param name="services">The scope's provider; the engine is resolved lazily, on first save.</param>
internal sealed partial class AuditingSaveChangesInterceptor(IServiceProvider services) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, SaveState> _states = new();

    private AuditingOptions? _options;
    private AuditingOptions Options => _options ??= services.GetRequiredService<AuditingOptions>();

    // The engine itself, not the public interface: this capture decides to skip an entity before
    // the engine sees it, and the reason for that decision is the engine's to give; and it stores
    // entries in the context being saved, not through the store. Resolved on first use, not
    // injected: the recorder depends on the store, the store on the DbContext, and this
    // interceptor is created while that DbContext is still being built.
    private AuditRecorder? _recorder;
    private AuditRecorder Recorder => _recorder ??= services.GetRequiredService<AuditRecorder>();

    /// <inheritdoc/>
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is { } context && Saving(context, result) is { } state)
        {
            if (NeedsTransactionNow(context, state))
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

        if (eventData.Context is { } context && Saving(context, result) is { } state)
        {
            if (NeedsTransactionNow(context, state))
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

        if (eventData.Context is not { } context || Saved(context) is not { } state)
            return result;

        try
        {
            WriteDeferred(context, state);

            if (!DeferCommit(context, state))
            {
                state.OwnedTransaction?.Commit();
                Finish(context, state);
            }
        }
        catch
        {
            RollbackQuietly(state);
            Finish(context, state);
            throw;
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

        if (eventData.Context is not { } context || Saved(context) is not { } state)
            return result;

        try
        {
            await WriteDeferredAsync(context, state, cancellationToken).ConfigureAwait(false);

            if (!DeferCommit(context, state))
            {
                if (state.OwnedTransaction is { } transaction)
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                Finish(context, state);
            }
        }
        catch
        {
            await RollbackQuietlyAsync(state).ConfigureAwait(false);
            Finish(context, state);
            throw;
        }

        return result;
    }

    /// <inheritdoc/>
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Abort(eventData.Context);
    }

    /// <inheritdoc/>
    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        return AbortAsync(eventData.Context);
    }

    /// <inheritdoc/>
    /// <remarks>EF reports a cancelled save here and <b>not</b> through <c>SaveChangesFailed</c>.</remarks>
    public override void SaveChangesCanceled(DbContextEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Abort(eventData.Context);
    }

    /// <inheritdoc/>
    /// <remarks>See <see cref="SaveChangesCanceled"/>.</remarks>
    public override Task SaveChangesCanceledAsync(
        DbContextEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        return AbortAsync(eventData.Context);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// EF reports a concurrency conflict here, just before it throws, and <b>not</b> through
    /// <c>SaveChangesFailed</c> — unless an interceptor suppresses the exception, and then the save
    /// goes on. Rolled back later, not here: see <see cref="RollBackAfterConflict"/>.
    /// </remarks>
    public override InterceptionResult ThrowingConcurrencyException(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (!result.IsSuppressed)
            RollBackAfterConflict(eventData.Context);

        return result;
    }

    /// <inheritdoc/>
    /// <remarks>See <see cref="ThrowingConcurrencyException"/>.</remarks>
    public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (!result.IsSuppressed)
            RollBackAfterConflict(eventData.Context);

        return ValueTask.FromResult(result);
    }

    /// <summary>
    /// Whether to open a transaction now: this save has deferred creations, there is none of ours
    /// yet, and none to rely on. When there is one to rely on, it is remembered, so that
    /// <c>SavedChanges</c> can tell whether someone committed it early.
    /// </summary>
    private static bool NeedsTransactionNow(DbContext context, SaveState state)
    {
        if (state.Deferred.Count == 0 || state.OwnedTransaction is not null)
            return false;

        if (NeedsOwnTransaction(context))
            return true;

        state.ReliedOn ??= context.Database.CurrentTransaction;
        return false;
    }

    /// <summary>
    /// Reads the change tracker: entries whose key is already known are recorded now, into this
    /// save; the rest are held for <c>SavedChanges</c>.
    /// </summary>
    /// <remarks>
    /// The tracker is materialised to a list first. Recording adds entities to it, and enumerating
    /// a collection while adding to it is how a capture becomes a capture of itself.
    /// </remarks>
    private void Capture(DbContext context, SaveState state, List<EntityEntry> entries)
    {
        EnsureMapsAuditTables(context);

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
                    // Empty when the key arrives with the INSERT, or spans several columns: this
                    // entity was skipped before it was ever addressed, and inventing a placeholder
                    // would read as a real key. Never a throw — a multi-column key is exactly what
                    // an application is told to exclude, so the exclusion cannot trip over it.
                    TrackedKeys.KeyForReport(entry),
                    action);

                continue;
            }

            var changes = EntityDiff.Build(entry, action, Options.RecordUpdatesWithoutBeforeImage);

            // Nothing moved. Left to the engine this would return null anyway, and with the same
            // report — but caught here it also saves deferring an entry that will turn out to be
            // empty, so the report is made here too rather than lost to the shortcut. TryGetKey and
            // not KeyForReport: only an entity that was NOT excluded gets this far, and for one of
            // those a multi-column key is still refused, saved with a change or without one.
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

            Record(context, write);
        }
    }

    private void RecordDeferred(DbContext context, SaveState state)
    {
        foreach (var (entry, write) in state.Deferred)
        {
            var entityId = TrackedKeys.TryGetKey(entry)
                ?? throw new InvalidOperationException(
                    $"{entry.Metadata.DisplayName()} still has no key after the save, so its creation " +
                    "cannot be recorded. Assign the key in code, or exclude the entity with [AuditIgnore] " +
                    "or IgnoreEntity<T>().");

            Record(context, write with { EntityId = entityId });
        }
    }

    /// <summary>
    /// Records through the engine, which applies the model's rules, formats the values and stamps
    /// the actor, correlation and time — and adds the entry to <paramref name="context"/>, the
    /// context being saved.
    /// </summary>
    /// <remarks>
    /// Not through the store: the store holds the scope's registered context, and a second context
    /// in the same scope (another instance, or another type) is a different one. Its entries would
    /// then be tracked where this save cannot write them.
    /// </remarks>
    private void Record(DbContext context, AuditWrite write)
    {
        if (Recorder.Prepare(write) is not { } entry)
            return;

        context.Set<AuditEntry>().Add(entry);
        Created(entry);
        Recorder.Counted(entry);
    }

    /// <summary>
    /// Refuses a context that captures changes but has nowhere to keep them, before it writes
    /// anything.
    /// </summary>
    private static void EnsureMapsAuditTables(DbContext context)
    {
        if (context.Model.FindEntityType(typeof(AuditEntry)) is not null)
            return;

        throw new InvalidOperationException(
            $"{context.GetType().Name} captures its changes with UseAuditing(sp), but its model has no " +
            "audit tables, and entries are kept in the context being saved. Call " +
            "modelBuilder.ApplyAuditing() in its OnModelCreating, or remove UseAuditing(sp) from its options.");
    }

    /// <summary>
    /// Whether this interceptor should own a transaction for the deferred pass.
    /// </summary>
    private static bool NeedsOwnTransaction(DbContext context) =>
        context.Database.CurrentTransaction is null
        // An ambient TransactionScope already makes the two saves atomic, and EF refuses to begin
        // a transaction inside one.
        && System.Transactions.Transaction.Current is null
        // A non-relational provider has none to open.
        && context.Database.IsRelational()
        // A retrying strategy refuses a transaction it did not start, because it could not replay
        // it. Documented on the class rather than worked around here.
        && !context.Database.CreateExecutionStrategy().RetriesOnFailure;

    private readonly record struct Deferred(EntityEntry Entry, AuditWrite Write);
}
