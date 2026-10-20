using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Metadata;
using DotNetBoost.Auditing.Core.Models;

namespace DotNetBoost.Auditing.Core.Services;

/// <summary>
/// The retention half of <see cref="Interfaces.IAuditManager"/>: it works out the cutoffs from the
/// configured and
/// declared retentions and drains the store in batches until there is nothing left to delete.
/// </summary>
internal sealed class AuditPruner(
    IAuditStore store,
    IEntityKeyAccessor keys,
    AuditModelRegistry registry,
    AuditingOptions options,
    TimeProvider timeProvider)
{
    private readonly IAuditStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IEntityKeyAccessor _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly AuditModelRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly AuditingOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    internal async Task<int> PruneAsync(int batchSize = 1000, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        // Nothing configured anywhere means nothing to do. Deleting on a default would be deleting
        // history nobody asked to lose.
        if (_options.Retention is null && _registry.Retentions.Count == 0)
            return 0;

        var now   = _time.GetUtcNow().UtcDateTime;
        var total = 0;

        // The declared entities first, each to its own cutoff.
        foreach (var (entityName, retention) in _registry.Retentions)
            total += await DrainAsync(now - retention, AuditPruneScope.For(entityName), batchSize, ct).ConfigureAwait(false);

        // Then everything else, to the configured default — EXCLUDING the entities just handled.
        // Without the exclusion an entity with a longer retention than the default would be deleted
        // at the default, making [AuditRetention] silently the opposite of what it says.
        if (_options.Retention is { } fallback)
        {
            var scope = _registry.Retentions.Count == 0
                ? AuditPruneScope.All
                : AuditPruneScope.Excluding([.. _registry.Retentions.Keys]);

            total += await DrainAsync(now - fallback, scope, batchSize, ct).ConfigureAwait(false);
        }

        return total;
    }

    internal Task<int> PruneOlderThanAsync(
        TimeSpan olderThan,
        string? entityName = null,
        int batchSize = 1000,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(olderThan, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var cutoff = _time.GetUtcNow().UtcDateTime - olderThan;
        var scope  = entityName is null ? AuditPruneScope.All : AuditPruneScope.For(entityName);

        return DrainAsync(cutoff, scope, batchSize, ct);
    }

    internal Task<int> EraseForAsync(object entity, CancellationToken ct = default)
    {
        var (name, id) = AuditAddress.Of(entity, _keys);
        return _store.DeleteForEntityAsync(name, id, ct);
    }

    internal Task<int> EraseForAsync(string entityName, string entityId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        return _store.DeleteForEntityAsync(entityName, entityId, ct);
    }

    internal Task<int> EraseForActorAsync(string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        return _store.DeleteForActorAsync(actor, ct);
    }

    /// <summary>
    /// Deletes in batches until a batch comes back short, which is the store saying the backlog is
    /// clear.
    /// </summary>
    /// <remarks>
    /// A cancelled prune keeps what it has already deleted and returns the count. Rolling it back
    /// would mean holding a transaction across the whole sweep — the thing batching exists to
    /// avoid — and the work is idempotent, so the next run simply carries on.
    /// </remarks>
    private async Task<int> DrainAsync(DateTime cutoff, AuditPruneScope scope, int batchSize, CancellationToken ct)
    {
        var total = 0;

        while (!ct.IsCancellationRequested)
        {
            var deleted = await _store.DeleteOlderThanAsync(cutoff, scope, batchSize, ct).ConfigureAwait(false);
            total += deleted;

            if (deleted < batchSize)
                break;
        }

        return total;
    }
}
