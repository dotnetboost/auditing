using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Metadata;
using DotNetBoost.Auditing.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.Auditing.EntityFrameworkCore;

/// <summary>
/// Keeps entries in the application's own <see cref="DbContext"/>, which is what makes the
/// transaction guarantee possible: the audit rows are tracked alongside the change they describe
/// and committed by the same <c>SaveChanges</c>.
/// </summary>
/// <param name="context">The context holding the two tables.</param>
public sealed class EfCoreAuditStore(DbContext context) : IAuditStore
{
    private readonly DbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    private DbSet<AuditEntry> Entries => _context.Set<AuditEntry>();

    private DbSet<AuditPropertyChange> Changes => _context.Set<AuditPropertyChange>();

    /// <summary>
    /// Tracks the entry for insertion. <b>It does not save.</b>
    /// </summary>
    /// <param name="entry">The entry to append.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A completed task; nothing is awaited because nothing goes to the database here.</returns>
    /// <remarks>
    /// An entry describes a change the caller is making, so it has to commit <i>with</i> that
    /// change — saving it here would commit the record of a change before the change itself, and a
    /// failure in between would leave a trail of something that never happened.
    /// <para>
    /// For the interceptor this is exactly right: the rows join the save already in flight. For an
    /// explicit <c>IAuditManager</c> call it means the entry is durable when <b>you</b> call
    /// <c>SaveChangesAsync</c> on your context, which is the next thing an EF Core application does
    /// anyway. If you record against a context with nothing else pending, save it.
    /// </para>
    /// </remarks>
    public Task AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Entries.Add(entry);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task AppendRangeAsync(IReadOnlyCollection<AuditEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Entries.AddRange(entries);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<AuditEntry?> GetAsync(Guid id, CancellationToken ct = default) =>
        await Entries
            .AsNoTracking()
            .Include(e => e.Changes)
            .FirstOrDefaultAsync(e => e.Id == id, ct)
            .ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var filtered = Filter(Entries.AsNoTracking(), query);

        // Counted before paging, and only when asked: it is a second pass over the index, and most
        // callers paging a history never show a total.
        long? total = query.IncludeTotal
            ? await filtered.LongCountAsync(ct).ConfigureAwait(false)
            : null;

        var page = filtered
            // Id as the tiebreaker, so two entries recorded in the same tick do not swap places
            // between one page and the next. Without it, paging a busy trail can show a row twice
            // and skip another.
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Skip(query.Skip)
            .Take(query.Take);

        if (query.IncludeChanges)
            page = page.Include(e => e.Changes);

        var entries = await page.ToListAsync(ct).ConfigureAwait(false);
        return new AuditPage(entries, total);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteOlderThanAsync(
        DateTime cutoff,
        AuditPruneScope scope,
        int batchSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var eligible = Entries.AsNoTracking().Where(e => e.OccurredAt < cutoff);

        if (scope.EntityName is { } only)
            eligible = eligible.Where(e => e.EntityName == only);

        if (scope.ExcludeEntityNames.Count > 0)
        {
            // The entities with a retention of their own, pruned by their own pass. Without this a
            // longer per-entity retention would be deleted at the shorter global cutoff.
            var excluded = scope.ExcludeEntityNames.ToArray();
            eligible = eligible.Where(e => !excluded.Contains(e.EntityName));
        }

        // The ids first, oldest first, one batch at a time. ExecuteDelete over a Take() is not
        // translatable on every provider, and a single unbounded delete over a year of entries
        // would hold locks across the table that recording needs.
        var ids = await eligible
            .OrderBy(e => e.OccurredAt)
            .Select(e => e.Id)
            .Take(batchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return ids.Count == 0 ? 0 : await DeleteByIdAsync(ids, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteForEntityAsync(string entityName, string entityId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        var ids = await Entries.AsNoTracking()
            .Where(e => e.EntityName == entityName && e.EntityId == entityId)
            .Select(e => e.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return ids.Count == 0 ? 0 : await DeleteByIdAsync(ids, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteForActorAsync(string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var ids = await Entries.AsNoTracking()
            .Where(e => e.Actor == actor)
            .Select(e => e.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return ids.Count == 0 ? 0 : await DeleteByIdAsync(ids, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the property rows and then the entries, both by set-based delete.
    /// </summary>
    /// <remarks>
    /// The children are deleted explicitly rather than left to the cascade. The model declares one,
    /// but a cascade is only enforced if the database is enforcing foreign keys — which SQLite does
    /// not do unless it was asked to — and an orphaned property row is invisible: nothing reads it
    /// and nothing ever cleans it up.
    /// </remarks>
    private async Task<int> DeleteByIdAsync(List<Guid> ids, CancellationToken ct)
    {
        await Changes.Where(c => ids.Contains(c.AuditEntryId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return await Entries.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    private static IQueryable<AuditEntry> Filter(IQueryable<AuditEntry> entries, AuditQuery query)
    {
        if (query.EntityName is { } entityName)
            entries = entries.Where(e => e.EntityName == entityName);

        if (query.EntityId is { } entityId)
            entries = entries.Where(e => e.EntityId == entityId);

        if (query.Action is { } action)
            entries = entries.Where(e => e.Action == action);

        if (query.Actor is { } actor)
            entries = entries.Where(e => e.Actor == actor);

        // Deliberately separate from Actor: "any actor" and "no actor was captured" are different
        // questions, and one nullable string cannot ask both.
        if (query.ActorMissing)
            entries = entries.Where(e => e.Actor == null);

        if (query.CorrelationId is { } correlationId)
            entries = entries.Where(e => e.CorrelationId == correlationId);

        if (query.From is { } from)
            entries = entries.Where(e => e.OccurredAt >= from);

        // Exclusive, so consecutive windows do not double-count.
        if (query.To is { } to)
            entries = entries.Where(e => e.OccurredAt < to);

        return entries;
    }
}

/// <summary>
/// Reads keys from the EF model rather than by naming convention, and is therefore right about keys
/// the convention cannot see — a shadow key, or one configured fluently. Replaces
/// <c>ConventionEntityKeyAccessor</c> when the EF Core store is registered.
/// </summary>
/// <param name="context">The context whose model describes the entities.</param>
public sealed class EfCoreEntityKeyAccessor(DbContext context) : IEntityKeyAccessor
{
    private readonly DbContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly Core.Services.ConventionEntityKeyAccessor _fallback = new();

    /// <inheritdoc/>
    public string? GetKey(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var entityType = _context.Model.FindEntityType(entity.GetType());
        var key        = entityType?.FindPrimaryKey();

        // Not in this context's model — a DTO, or an entity from another context. The convention
        // accessor may still know what to do with it, and saying "no key" here would refuse a
        // recording that would otherwise work.
        if (key is null)
            return _fallback.GetKey(entity);

        if (key.Properties.Count > 1)
        {
            throw new InvalidOperationException(
                $"{entity.GetType().Name} has a composite primary key " +
                $"({string.Join(", ", key.Properties.Select(p => p.Name))}). An audit entry records a " +
                "single key, so record against it with audit.For(name, id) and a key you compose " +
                "yourself, or register your own IEntityKeyAccessor.");
        }

        var property = key.Properties[0];

        // Public metadata first, the change tracker last: a shadow key exists only on a tracked
        // entity, and asking the tracker about an untracked one would attach it as a side effect.
        var value = property.PropertyInfo is { } info ? info.GetValue(entity)
            : property.FieldInfo is { } field ? field.GetValue(entity)
            : _context.Entry(entity).Property(property.Name).CurrentValue;

        return EntityKey.IsUnset(value) ? null : EntityKey.ToKeyString(value!);
    }
}
