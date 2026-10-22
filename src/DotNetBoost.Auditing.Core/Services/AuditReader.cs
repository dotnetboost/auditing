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

    internal async Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Two answers to "where does this page start". Refused rather than letting one win: a read
        // that silently ignored the other would page wrongly, and page wrongly quietly.
        if (query.After is not null && query.Skip > 0)
        {
            throw new ArgumentException(
                "A query sets both Skip and After, which are two ways of saying where the page starts. " +
                "Use After to walk a trail — an entry arriving mid-walk cannot shift a row across a " +
                "page boundary, and the read does not get dearer with depth — or Skip to jump to a " +
                "page number.",
                nameof(query));
        }

        var page = await _store.QueryAsync(Clamp(query), ct).ConfigureAwait(false);

        // Derived here rather than in each store, so what a cursor points at is defined once. A
        // store assembling its own page leaves NextCursor null and this fills it in.
        return page.Entries.Count == 0
            ? page
            : page with { NextCursor = AuditCursor.Of(page.Entries[^1]) };
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
