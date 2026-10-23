using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;

namespace DotNetBoost.Auditing.TestSupport;

/// <summary>
/// An <see cref="IAuditObserver"/> that keeps a notebook, so a test can assert on what the engine
/// decided not to record — which is the whole point of the interface and otherwise unobservable.
/// </summary>
/// <remarks>
/// Here rather than beside one suite's fakes because both levels need it: the engine's own tests in
/// <c>UnitTests</c> assert that every suppression reports its reason, and <c>ProviderTests</c>
/// asserts that the EF Core interceptor reports the ones it decides for itself, before the engine
/// is reached.
/// </remarks>
public sealed class RecordingAuditObserver : IAuditObserver
{
    /// <summary>Every suppression reported, in order.</summary>
    public List<(AuditSuppression Reason, string EntityName, string EntityId, AuditAction Action)> Suppressed { get; } = [];

    /// <summary>Every write reported as recorded without the model's rules, in order.</summary>
    public List<(string EntityName, string EntityId, AuditAction Action)> WithoutModelRules { get; } = [];

    /// <summary>The reasons reported, for the common assertion.</summary>
    public IEnumerable<AuditSuppression> Reasons => Suppressed.Select(s => s.Reason);

    /// <inheritdoc/>
    public void WriteSuppressed(AuditSuppression reason, string entityName, string entityId, AuditAction action) =>
        Suppressed.Add((reason, entityName, entityId, action));

    /// <inheritdoc/>
    public void RecordedWithoutModelRules(string entityName, string entityId, AuditAction action) =>
        WithoutModelRules.Add((entityName, entityId, action));
}
