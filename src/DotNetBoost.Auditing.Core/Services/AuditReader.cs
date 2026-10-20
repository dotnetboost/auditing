using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;

namespace DotNetBoost.Auditing.Core.Services;

/// <summary>
/// The reading half of <see cref="Interfaces.IAuditManager"/>. Its whole job is the clamp: no query reaches a
/// store with an unbounded page, whatever a caller or an HTTP query string asked for.
/// </summary>
internal sealed class AuditReader(
    IAuditStore store,
    IEntityKeyAccessor keys,
    AuditingOptions options)
{
    private readonly IAuditStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IEntityKeyAccessor _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly AuditingOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    internal Task<AuditEntry?> GetAsync(Guid id, CancellationToken ct = default) =>
        _store.GetAsync(id, ct);

    internal Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _store.QueryAsync(Clamp(query), ct);
    }

    internal Task<AuditPage> HistoryAsync(object entity, int skip = 0, int take = 0, CancellationToken ct = default)
    {
        var (name, id) = AuditAddress.Of(entity, _keys);

        return QueryAsync(new AuditQuery
        {
            EntityName = name,
            EntityId   = id,
            Skip       = skip,
            Take       = take
        }, ct);
    }

    /// <summary>
    /// The query a store may trust: a page size inside the configured limits and a non-negative
    /// offset.
    /// </summary>
    private AuditQuery Clamp(AuditQuery query) => query with
    {
        Skip = Math.Max(0, query.Skip),

        // Zero means "the default", never "all of them". An audit table is the one place in an
        // application where the difference is the gap between a page and an outage.
        Take = query.Take <= 0
            ? Math.Min(_options.DefaultPageSize, _options.MaxPageSize)
            : Math.Min(query.Take, _options.MaxPageSize)
    };
}
