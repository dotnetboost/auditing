using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;

namespace DotNetBoost.Auditing.TestSupport;

/// <summary>
/// An <see cref="IAuditStore"/> in a list. Runs the same contract suite as the real stores (see
/// <c>ProviderTests</c>), so a test built on it is testing against something known to behave like a
/// database — which is the only reason a double is worth having.
/// </summary>
/// <remarks>
/// It <b>copies on the way in and on the way out</b>, because a database does. A double that handed
/// back the caller's own object would let a test mutate stored state by accident, and would hide the
/// one bug a store can have that nothing else catches: forgetting to persist a field.
/// </remarks>
public sealed class InMemoryAuditStore : IAuditStore
{
    private readonly List<AuditEntry> _entries = [];

    /// <summary>Every stored entry, newest first, for a test that wants to look directly.</summary>
    public IReadOnlyList<AuditEntry> All
    {
        get
        {
            lock (_entries)
                return [.. Ordered().Select(e => Copy(e))];
        }
    }

    /// <summary>How many entries are stored.</summary>
    public int Count
    {
        get
        {
            lock (_entries)
                return _entries.Count;
        }
    }

    /// <summary>How many property changes are stored, across every entry.</summary>
    public int ChangeCount
    {
        get
        {
            lock (_entries)
                return _entries.Sum(e => e.Changes.Count);
        }
    }

    /// <inheritdoc/>
    public Task AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_entries)
            _entries.Add(Copy(entry));

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task AppendRangeAsync(IReadOnlyCollection<AuditEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        lock (_entries)
            _entries.AddRange(entries.Select(e => Copy(e)));

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<AuditEntry?> GetAsync(Guid id, CancellationToken ct = default)
    {
        lock (_entries)
        {
            var found = _entries.Find(e => e.Id == id);
            return Task.FromResult(found is null ? null : Copy(found));
        }
    }

    /// <inheritdoc/>
    public Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        lock (_entries)
        {
            var matched = Ordered().Where(e => Matches(e, query)).ToList();

            long? total = query.IncludeTotal ? matched.Count : null;

            var page = matched
                .Skip(query.Skip < 0 ? 0 : query.Skip)
                .Take(query.Take)
                .Select(e => Copy(e, query.IncludeChanges))
                .ToList();

            return Task.FromResult(new AuditPage(page, total));
        }
    }

    /// <inheritdoc/>
    public Task<int> DeleteOlderThanAsync(
        DateTime cutoff,
        AuditPruneScope scope,
        int batchSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        lock (_entries)
        {
            // Oldest first and capped at the batch, exactly as the real stores do: a prune that
            // cannot finish in one pass must still have made progress from the right end.
            var doomed = _entries
                .Where(e => e.OccurredAt < cutoff && InScope(e, scope))
                .OrderBy(e => e.OccurredAt)
                .ThenBy(e => e.Id)
                .Take(batchSize)
                .ToList();

            foreach (var entry in doomed)
                _entries.Remove(entry);

            return Task.FromResult(doomed.Count);
        }
    }

    /// <inheritdoc/>
    public Task<int> DeleteForEntityAsync(string entityName, string entityId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        lock (_entries)
            return Task.FromResult(_entries.RemoveAll(e => e.EntityName == entityName && e.EntityId == entityId));
    }

    /// <inheritdoc/>
    public Task<int> DeleteForActorAsync(string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        lock (_entries)
            return Task.FromResult(_entries.RemoveAll(e => e.Actor == actor));
    }

    /// <summary>Empties the store, for a test that reuses one across cases.</summary>
    public void Clear()
    {
        lock (_entries)
            _entries.Clear();
    }

    /// <summary>
    /// Newest first with the id as the tiebreaker — the ordering every real store issues, so paging
    /// is stable when several entries share a timestamp.
    /// </summary>
    private IEnumerable<AuditEntry> Ordered() =>
        _entries.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id);

    private static bool Matches(AuditEntry entry, AuditQuery query) =>
        (query.EntityName is null || entry.EntityName == query.EntityName)
        && (query.EntityId is null || entry.EntityId == query.EntityId)
        && (query.Action is null || entry.Action == query.Action)
        && (query.Actor is null || entry.Actor == query.Actor)
        // Separate from Actor: "any actor" and "no actor was captured" are different questions.
        && (!query.ActorMissing || entry.Actor is null)
        && (query.CorrelationId is null || entry.CorrelationId == query.CorrelationId)
        && (query.From is null || entry.OccurredAt >= query.From)
        // Exclusive, so consecutive windows do not double-count.
        && (query.To is null || entry.OccurredAt < query.To);

    private static bool InScope(AuditEntry entry, AuditPruneScope scope) =>
        (scope.EntityName is null || entry.EntityName == scope.EntityName)
        && !scope.ExcludeEntityNames.Contains(entry.EntityName);

    private static AuditEntry Copy(AuditEntry entry, bool withChanges = true)
    {
        var copy = new AuditEntry
        {
            Id            = entry.Id,
            EntityName    = entry.EntityName,
            EntityId      = entry.EntityId,
            Action        = entry.Action,
            Actor         = entry.Actor,
            CorrelationId = entry.CorrelationId,
            OccurredAt    = entry.OccurredAt,
            Metadata      = entry.Metadata
        };

        if (!withChanges)
            return copy;

        foreach (var change in entry.Changes)
        {
            copy.Changes.Add(new AuditPropertyChange
            {
                AuditEntryId = change.AuditEntryId == Guid.Empty ? entry.Id : change.AuditEntryId,
                PropertyName = change.PropertyName,
                OldValue     = change.OldValue,
                NewValue     = change.NewValue,
                IsRedacted   = change.IsRedacted,
                IsTruncated  = change.IsTruncated
            });
        }

        return copy;
    }
}
