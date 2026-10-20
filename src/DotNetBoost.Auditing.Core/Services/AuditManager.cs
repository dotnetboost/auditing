using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;

namespace DotNetBoost.Auditing.Core.Services;

/// <summary>
/// The engine behind <see cref="IAuditManager"/>. The work still lives in three classes —
/// <see cref="AuditRecorder"/> for the rules, <see cref="AuditReader"/> for the clamp,
/// <see cref="AuditPruner"/> for the cutoffs — because they need different dependencies and have
/// nothing to say to each other. This type is what presents them as one service.
/// </summary>
/// <remarks>
/// It holds no logic of its own on purpose. Every method here is a forward, so there is exactly one
/// implementation of redaction, one of the page clamp, and one of the retention arithmetic, and no
/// second place to forget them.
/// </remarks>
internal sealed class AuditManager(
    AuditRecorder recorder,
    AuditReader reader,
    AuditPruner pruner) : IAuditManager
{
    private readonly AuditRecorder _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
    private readonly AuditReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    private readonly AuditPruner _pruner = pruner ?? throw new ArgumentNullException(nameof(pruner));

    /// <inheritdoc/>
    public AuditSubject For(object entity)
    {
        var (name, id) = _recorder.Address(entity);

        // The type travels with the subject. Resolving the address and then dropping the type would
        // cost every write through this subject its [AuditIgnore] and [AuditRedact] rules, silently.
        return new AuditSubject(this, name, id, entity.GetType());
    }

    /// <inheritdoc/>
    public AuditSubject For(string entityName, string entityId) => new(this, entityName, entityId);

    /// <inheritdoc/>
    public Task<AuditEntry?> RecordAsync(AuditWrite write, CancellationToken ct = default) =>
        _recorder.RecordAsync(write, ct);

    /// <inheritdoc/>
    public bool IsAudited(Type entityType) => _recorder.IsAudited(entityType);

    /// <inheritdoc/>
    public Task<AuditEntry?> GetAsync(Guid id, CancellationToken ct = default) =>
        _reader.GetAsync(id, ct);

    /// <inheritdoc/>
    public Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken ct = default) =>
        _reader.QueryAsync(query, ct);

    /// <inheritdoc/>
    public Task<AuditPage> HistoryAsync(object entity, int skip = 0, int take = 0, CancellationToken ct = default) =>
        _reader.HistoryAsync(entity, skip, take, ct);

    /// <inheritdoc/>
    public Task<int> PruneAsync(int batchSize = 1000, CancellationToken ct = default) =>
        _pruner.PruneAsync(batchSize, ct);

    /// <inheritdoc/>
    public Task<int> PruneOlderThanAsync(
        TimeSpan olderThan,
        string? entityName = null,
        int batchSize = 1000,
        CancellationToken ct = default) =>
        _pruner.PruneOlderThanAsync(olderThan, entityName, batchSize, ct);

    /// <inheritdoc/>
    public Task<int> EraseForAsync(object entity, CancellationToken ct = default) =>
        _pruner.EraseForAsync(entity, ct);

    /// <inheritdoc/>
    public Task<int> EraseForAsync(string entityName, string entityId, CancellationToken ct = default) =>
        _pruner.EraseForAsync(entityName, entityId, ct);

    /// <inheritdoc/>
    public Task<int> EraseForActorAsync(string actor, CancellationToken ct = default) =>
        _pruner.EraseForActorAsync(actor, ct);
}
