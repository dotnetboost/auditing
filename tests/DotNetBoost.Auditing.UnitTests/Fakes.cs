using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.TestSupport;

namespace DotNetBoost.Auditing.UnitTests;

/// <summary>
/// <see cref="InMemoryAuditStore"/> with a notebook: it delegates every call and records what it
/// was asked, so the engine's tests can assert on what reached the store as well as on what came
/// back.
/// </summary>
/// <remarks>
/// <b>A decorator, not a second double.</b> The behaviour comes from
/// <see cref="InMemoryAuditStore"/>, which passes the same contract suite as the real stores in
/// <c>ProviderTests</c> — so an engine test built on this is built on something known to behave
/// like a database. The previous version of this type was a hand-written fake that nothing held to
/// that standard: it could have drifted from every real store and every test here would still have
/// passed.
/// </remarks>
internal sealed class RecordingAuditStore : IAuditStore
{
    private readonly InMemoryAuditStore _inner = new();

    /// <summary>Every stored entry, newest first.</summary>
    public IReadOnlyList<AuditEntry> Entries => _inner.All;

    /// <summary>Each <see cref="DeleteOlderThanAsync"/> call, in order.</summary>
    public List<(DateTime Cutoff, AuditPruneScope Scope, int BatchSize)> PruneCalls { get; } = [];

    /// <summary>The last query the reader sent, after the engine's clamp.</summary>
    public AuditQuery? LastQuery { get; private set; }

    /// <summary>Puts entries in without going through the engine, for a test that needs a trail to act on.</summary>
    public async Task SeedAsync(params AuditEntry[] entries) => await _inner.AppendRangeAsync(entries);

    public Task AppendAsync(AuditEntry entry, CancellationToken ct = default) =>
        _inner.AppendAsync(entry, ct);

    public Task AppendRangeAsync(IReadOnlyCollection<AuditEntry> entries, CancellationToken ct = default) =>
        _inner.AppendRangeAsync(entries, ct);

    public Task<AuditEntry?> GetAsync(Guid id, CancellationToken ct = default) =>
        _inner.GetAsync(id, ct);

    public Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken ct = default)
    {
        LastQuery = query;
        return _inner.QueryAsync(query, ct);
    }

    public Task<int> DeleteOlderThanAsync(DateTime cutoff, AuditPruneScope scope, int batchSize, CancellationToken ct = default)
    {
        PruneCalls.Add((cutoff, scope, batchSize));
        return _inner.DeleteOlderThanAsync(cutoff, scope, batchSize, ct);
    }

    public Task<int> DeleteForEntityAsync(string entityName, string entityId, CancellationToken ct = default) =>
        _inner.DeleteForEntityAsync(entityName, entityId, ct);

    public Task<int> DeleteForActorAsync(string actor, CancellationToken ct = default) =>
        _inner.DeleteForActorAsync(actor, ct);
}

internal sealed class FixedActorAccessor(string? actor) : IAuditActorAccessor
{
    public string? GetActor() => actor;
}
