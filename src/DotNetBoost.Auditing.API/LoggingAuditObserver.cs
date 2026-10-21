using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using Microsoft.Extensions.Logging;

namespace DotNetBoost.Auditing.API;

/// <summary>
/// Writes what the engine decided not to record to <see cref="ILogger"/>. Register with
/// <c>.UseLoggingObserver()</c>.
/// </summary>
/// <remarks>
/// <b>The level is chosen by whether the suppression is usually a decision or usually a
/// mistake</b>, which is the only thing that makes this readable in a real log:
/// <list type="bullet">
/// <item><description>
/// <b>Debug</b> for the three that are configured intent — the kill switch, <c>[AuditIgnore]</c>,
/// and a write with nothing left to say. A high-churn ignored entity reports one of these on every
/// save, so at any higher level this would be the noisiest thing in the application and the first
/// thing an operator filtered out.
/// </description></item>
/// <item><description>
/// <b>Warning</b> for the two that are nearly always a misconfiguration: opt-in mode with nothing
/// declared under the entity's name, and a write recorded with no model rules to apply. In both,
/// the source says the entity is audited and the table disagrees.
/// </description></item>
/// </list>
/// <para>
/// <see cref="AuditSuppression.NotOptedIn"/> sits on the line and is logged at Debug. It is what
/// opt-in mode is for, so warning on it would warn about the configuration working — but it is
/// also how an operator discovers opt-in is on at all, which is why it says so in as many words.
/// </para>
/// <para>
/// The messages name the entity and the key, which are already in every entry the same reader may
/// read, and never a recorded value: a log line about a suppressed write must not become the copy
/// of the data the suppression existed to keep out of the trail.
/// </para>
/// </remarks>
/// <param name="logger">Where the lines go.</param>
public sealed partial class LoggingAuditObserver(ILogger<LoggingAuditObserver> logger) : IAuditObserver
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc/>
    public void WriteSuppressed(AuditSuppression reason, string entityName, string entityId, AuditAction action)
    {
        switch (reason)
        {
            case AuditSuppression.RecordingDisabled:
                LogRecordingDisabled(_logger, action, entityName, entityId);
                break;

            case AuditSuppression.EntityIgnored:
                LogEntityIgnored(_logger, action, entityName, entityId);
                break;

            case AuditSuppression.NotOptedIn:
                LogNotOptedIn(_logger, action, entityName, entityId);
                break;

            case AuditSuppression.EntityNotDeclared:
                LogEntityNotDeclared(_logger, action, entityName, entityId);
                break;

            case AuditSuppression.NothingChanged:
                LogNothingChanged(_logger, action, entityName, entityId);
                break;

            default:
                // A reason this package has not been taught yet. Logged rather than ignored: a new
                // suppression arriving as silence is the failure this observer exists to prevent,
                // and an unknown one is worth more attention than a known one, not less.
                LogUnknownSuppression(_logger, reason, action, entityName, entityId);
                break;
        }
    }

    /// <inheritdoc/>
    public void RecordedWithoutModelRules(string entityName, string entityId, AuditAction action) =>
        LogNoModelRules(_logger, action, entityName, entityId);

    [LoggerMessage(EventId = 7100, Level = LogLevel.Debug,
        Message = "Not recording a {Action} of '{EntityName}' ({EntityId}): recording is disabled.")]
    private static partial void LogRecordingDisabled(ILogger logger, AuditAction action, string entityName, string entityId);

    [LoggerMessage(EventId = 7101, Level = LogLevel.Debug,
        Message = "Not recording a {Action} of '{EntityName}' ({EntityId}): the entity is [AuditIgnore].")]
    private static partial void LogEntityIgnored(ILogger logger, AuditAction action, string entityName, string entityId);

    [LoggerMessage(EventId = 7102, Level = LogLevel.Debug,
        Message = "Not recording a {Action} of '{EntityName}' ({EntityId}): opt-in is required and the " +
                  "entity is not [Audited].")]
    private static partial void LogNotOptedIn(ILogger logger, AuditAction action, string entityName, string entityId);

    [LoggerMessage(EventId = 7103, Level = LogLevel.Warning,
        Message = "Not recording a {Action} of '{EntityName}' ({EntityId}): opt-in is required and nothing " +
                  "is declared under that name, so nothing proves it opted in. Call " +
                  "DeclareEntitiesFrom(assembly) at Build(), or address the write with the entity itself.")]
    private static partial void LogEntityNotDeclared(ILogger logger, AuditAction action, string entityName, string entityId);

    [LoggerMessage(EventId = 7104, Level = LogLevel.Debug,
        Message = "Not recording a {Action} of '{EntityName}' ({EntityId}): no recordable property changed.")]
    private static partial void LogNothingChanged(ILogger logger, AuditAction action, string entityName, string entityId);

    [LoggerMessage(EventId = 7105, Level = LogLevel.Warning,
        Message = "Recorded a {Action} of '{EntityName}' ({EntityId}) with no model rules applied: nothing " +
                  "is declared under that name, so [AuditIgnore] and [AuditRedact] were not applied. If " +
                  "this entity has a redacted property, its value is now in the table in full. Call " +
                  "DeclareEntitiesFrom(assembly) at Build().")]
    private static partial void LogNoModelRules(ILogger logger, AuditAction action, string entityName, string entityId);

    [LoggerMessage(EventId = 7106, Level = LogLevel.Warning,
        Message = "Not recording a {Action} of '{EntityName}' ({EntityId}): {Reason}.")]
    private static partial void LogUnknownSuppression(
        ILogger logger,
        AuditSuppression reason,
        AuditAction action,
        string entityName,
        string entityId);
}
