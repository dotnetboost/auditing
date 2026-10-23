using DotNetBoost.Auditing.Core.Models;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DotNetBoost.Auditing.Core.Services;

/// <summary>
/// The engine's three counters. <see cref="AuditingTelemetry"/> is the public half — the names an
/// application subscribes to.
/// </summary>
/// <remarks>
/// <b>Not an <see cref="Interfaces.IAuditObserver"/> implementation, deliberately.</b> The observer
/// is registered with <c>Replace</c>, so metrics behind it would be switched off by an application
/// that registered an observer of its own — telemetry should not be something you can turn off by
/// accident. The observer is for what an application must react to; this is for what it watches.
/// <para>
/// Each method asks the instrument whether anything is listening before it builds a tag, so the
/// cost of an unsubscribed counter is a field read and a branch rather than a
/// <see cref="TagList"/>.
/// </para>
/// </remarks>
internal sealed class AuditMetrics : IDisposable
{
    // Null when the meter came from a factory, which owns it. Only a meter this type created is a
    // meter this type may dispose.
    private readonly Meter? _owned;

    private readonly Counter<long> _recorded;
    private readonly Counter<long> _suppressed;
    private readonly Counter<long> _deleted;

    /// <summary>
    /// Creates the instruments on <paramref name="factory"/>'s meter, or on one of its own.
    /// </summary>
    /// <param name="factory">
    /// The host's meter factory, or <c>null</c>. An ASP.NET Core application has one registered
    /// already; a console application that only called <c>AddAuditing()</c> does not, and a library
    /// that demanded one would refuse to start over telemetry nobody asked for.
    /// </param>
    internal AuditMetrics(IMeterFactory? factory)
    {
        Meter meter;

        if (factory is not null)
        {
            meter = factory.Create(new MeterOptions(AuditingTelemetry.MeterName)
            {
                Version = AuditingTelemetry.MeterVersion
            });
        }
        else
        {
            meter  = new Meter(AuditingTelemetry.MeterName, AuditingTelemetry.MeterVersion);
            _owned = meter;
        }

        _recorded = meter.CreateCounter<long>(
            AuditingTelemetry.EntriesRecorded, "{entry}", "Audit entries the engine recorded.");

        _suppressed = meter.CreateCounter<long>(
            AuditingTelemetry.WritesSuppressed, "{write}", "Writes the engine deliberately did not record.");

        _deleted = meter.CreateCounter<long>(
            AuditingTelemetry.EntriesDeleted, "{entry}", "Audit entries deleted by a prune or an erasure.");
    }

    /// <summary>One entry recorded.</summary>
    internal void Recorded(string entityName, AuditAction action)
    {
        if (!_recorded.Enabled)
            return;

        _recorded.Add(1, new TagList
        {
            { AuditingTelemetry.TagEntity, entityName },
            { AuditingTelemetry.TagAction, Name(action) }
        });
    }

    /// <summary>One write declined, and which rule declined it.</summary>
    internal void Suppressed(AuditSuppression reason, string entityName, AuditAction action)
    {
        if (!_suppressed.Enabled)
            return;

        _suppressed.Add(1, new TagList
        {
            { AuditingTelemetry.TagEntity, entityName },
            { AuditingTelemetry.TagReason, Name(reason) },
            { AuditingTelemetry.TagAction, Name(action) }
        });
    }

    /// <summary><paramref name="count"/> entries deleted by <paramref name="operation"/>.</summary>
    internal void Deleted(int count, string operation)
    {
        if (count <= 0 || !_deleted.Enabled)
            return;

        _deleted.Add(count, new TagList { { AuditingTelemetry.TagOperation, operation } });
    }

    /// <inheritdoc/>
    public void Dispose() => _owned?.Dispose();

    // Literals rather than ToString(), which allocates on every measurement — and these are on the
    // recording path. The fallbacks exist so that a value added to either enum is reported rather
    // than dropped; it costs an allocation until the switch is extended, which is the right way
    // round for a counter nobody would otherwise notice had stopped moving.
    private static string Name(AuditAction action) => action switch
    {
        AuditAction.Created => nameof(AuditAction.Created),
        AuditAction.Updated => nameof(AuditAction.Updated),
        AuditAction.Deleted => nameof(AuditAction.Deleted),
        _                   => action.ToString()
    };

    private static string Name(AuditSuppression reason) => reason switch
    {
        AuditSuppression.RecordingDisabled => nameof(AuditSuppression.RecordingDisabled),
        AuditSuppression.EntityIgnored     => nameof(AuditSuppression.EntityIgnored),
        AuditSuppression.NotOptedIn        => nameof(AuditSuppression.NotOptedIn),
        AuditSuppression.EntityNotDeclared => nameof(AuditSuppression.EntityNotDeclared),
        AuditSuppression.NothingChanged    => nameof(AuditSuppression.NothingChanged),
        _                                  => reason.ToString()
    };
}
