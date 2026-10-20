using DotNetBoost.Auditing.Core.Metadata;

namespace DotNetBoost.Auditing.Core;

/// <summary>
/// Engine behaviour. Reached through <c>services.AddAuditing(o =&gt; …)</c> or the builder methods,
/// and validated once at <c>Build()</c>.
/// </summary>
public sealed class AuditingOptions
{
    private int      _maxValueLength = 4000;
    private int      _defaultPageSize = 50;
    private int      _maxPageSize = 200;
    private TimeSpan? _retention;

    /// <summary>
    /// Whether anything is recorded at all. A kill switch for an incident, flippable from
    /// configuration without removing the registration — the recorder stays resolvable and returns
    /// <c>null</c> from every call.
    /// <para>
    /// Reads are unaffected: turning recording off must not hide the trail already collected.
    /// </para>
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether an entity must be marked <c>[Audited]</c> to be recorded. Off by default — the
    /// useful default for an application that installs an auditing library is that it audits — and
    /// worth turning on when the trail should cover a named few tables rather than everything.
    /// </summary>
    public bool RequireOptIn { get; set; }

    /// <summary>
    /// Whether a <see cref="Models.AuditAction.Created"/> entry carries the entity's initial values.
    /// On by default: without them the trail says a row appeared and nothing about what it said,
    /// which is the question most often asked of a creation.
    /// </summary>
    public bool RecordCreatedValues { get; set; } = true;

    /// <summary>
    /// Whether a <see cref="Models.AuditAction.Deleted"/> entry carries the entity's final values.
    /// On by default, and the setting to think hardest about before turning off: after the row is
    /// gone this is the only copy of what it said.
    /// </summary>
    public bool RecordDeletedValues { get; set; } = true;

    /// <summary>
    /// Whether to record an update that arrives with <b>no before-image</b> — the values as they
    /// are now, with no old values. Default <c>true</c>.
    /// <para>
    /// <c>db.Update(entity)</c> and <c>Entry(entity).State = Modified</c> on an entity this context
    /// never loaded give EF nothing to diff against: it marks every property modified and sets each
    /// original value equal to the current one. There is no before-image anywhere in the process,
    /// and the only way to get one is a round trip per entity, which a library must not do behind
    /// an application's back.
    /// </para>
    /// <para>
    /// So the choice is between recording the new values with <c>OldValue</c> <c>null</c>, and
    /// recording nothing. The default is to record, because an auditing library that silently keeps
    /// no trail for a common EF pattern is worse than one that keeps a partial trail and says so:
    /// the application believes it is audited either way, and only one of those beliefs is true.
    /// Load the entity before changing it and the before-image comes back.
    /// </para>
    /// <para>
    /// The cost of the default: such an entry lists <i>every</i> property rather than the ones that
    /// moved, and an unchanged entity saved this way still produces an entry — the two are
    /// indistinguishable from outside, which is exactly the information EF does not have.
    /// </para>
    /// </summary>
    public bool RecordUpdatesWithoutBeforeImage { get; set; } = true;

    /// <summary>
    /// How long a recorded value may be before it is cut short and
    /// <see cref="Models.AuditPropertyChange.IsTruncated"/> is set. Default 4000.
    /// <para>
    /// A limit rather than none, because one <c>byte[]</c> column or a 2MB JSON blob would otherwise
    /// make the audit row larger than the row it describes — and the trail of a table is quickly
    /// bigger than the table.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Set to zero or less.</exception>
    public int MaxValueLength
    {
        get => _maxValueLength;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxValueLength = value;
        }
    }

    /// <summary>
    /// The page size a read uses when <see cref="Models.AuditQuery.Take"/> is not set. Default 50.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Set to zero or less.</exception>
    public int DefaultPageSize
    {
        get => _defaultPageSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _defaultPageSize = value;
        }
    }

    /// <summary>
    /// The largest page a read can ask for. Default 200. A larger <see cref="Models.AuditQuery.Take"/>
    /// is clamped to this rather than refused, so a caller asking for too much gets the most it may
    /// have instead of an error.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Set to zero or less.</exception>
    public int MaxPageSize
    {
        get => _maxPageSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxPageSize = value;
        }
    }

    /// <summary>
    /// How long entries are kept, for entities with no <c>[AuditRetention]</c> of their own.
    /// <c>null</c> (the default) keeps everything.
    /// <para>
    /// Setting it deletes nothing by itself —
    /// <see cref="Interfaces.IAuditManager.PruneAsync(int, CancellationToken)"/> does the work,
    /// when something runs it. A library that silently deleted history on a timer nobody asked for
    /// would be the wrong kind of helpful.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Set to zero or a negative span.</exception>
    public TimeSpan? Retention
    {
        get => _retention;
        set
        {
            if (value is { } span)
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(span, TimeSpan.Zero);

            _retention = value;
        }
    }

    /// <summary>
    /// Validates the combination. Called by <c>Build()</c>; the individual setters have already
    /// rejected their own bad values, so this is only for the pairs that are each valid alone.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options contradict each other.</exception>
    public void Validate()
    {
        if (DefaultPageSize > MaxPageSize)
        {
            throw new InvalidOperationException(
                $"DefaultPageSize ({DefaultPageSize}) is larger than MaxPageSize ({MaxPageSize}), so every " +
                "unsized read would be clamped below the default it was given. Raise MaxPageSize or lower " +
                "DefaultPageSize.");
        }

        if (MaxValueLength > MaxValueCeiling)
        {
            throw new InvalidOperationException(
                $"MaxValueLength ({MaxValueLength}) is above {MaxValueCeiling}. The value columns are " +
                "unbounded text in every store, but a limit this high records blobs no one will read and " +
                "makes the trail larger than the data. Keep a column's worth, not a file's.");
        }
    }

    /// <summary>
    /// The highest <see cref="MaxValueLength"/> <see cref="Validate"/> accepts — one megabyte of
    /// UTF-16. Not a store limit; a sanity limit.
    /// </summary>
    public const int MaxValueCeiling = 1024 * 1024;

    /// <summary>
    /// The stored width of the actor column, for a store building its schema. Alias of
    /// <see cref="AuditColumns.ActorMaxLength"/>, so a store configuring itself from options does
    /// not have to reach into a second static class.
    /// </summary>
    public static int ActorMaxLength => AuditColumns.ActorMaxLength;
}
