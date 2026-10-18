using DotNetBoost.Auditing.Core.Models;
using System.Globalization;

namespace DotNetBoost.Auditing.Dapper;

/// <summary>
/// The rows as the three engines actually hand them back, and the mapping to the models.
/// </summary>
/// <remarks>
/// <b>Why these exist rather than letting Dapper map straight onto the models.</b> The identity
/// columns are the problem: SQL Server returns a <c>UNIQUEIDENTIFIER</c> as a
/// <see cref="Guid"/> and PostgreSQL returns a <c>UUID</c> as one, but SQLite has no GUID type and
/// returns the <c>TEXT</c> it stored — and Dapper cannot convert a string to a
/// <see cref="Guid"/>, because <see cref="Guid"/> does not implement
/// <see cref="IConvertible"/> and so misses the fallback that gets
/// <see cref="DateTime"/> and <see cref="bool"/> across the same gap. The result is
/// <c>Invalid cast from 'System.String' to 'System.Guid'</c> on the first read.
/// <para>
/// The alternative is <c>SqlMapper.AddTypeHandler</c>, which is global: a library that registered
/// one would change how every <see cref="Guid"/> in the host application is read, including in
/// queries it knows nothing about. Reading the column as it comes and converting here costs one
/// small type per table and changes nothing outside this store.
/// </para>
/// <para>
/// EF Core does not have this problem because its model tells it the column's CLR type. Which is
/// also why both stores can share one SQLite database: they agree on the stored
/// <i>representation</i>, and only disagree about who converts it.
/// </para>
/// </remarks>
internal static class AuditRows
{
    /// <summary>One entry, as read.</summary>
    internal sealed class EntryRow
    {
        public object? Id { get; set; }
        public string EntityName { get; set; } = string.Empty;
        public string EntityId { get; set; } = string.Empty;
        public int Action { get; set; }
        public string? Actor { get; set; }
        public string CorrelationId { get; set; } = string.Empty;
        public DateTime OccurredAt { get; set; }
        public string? Metadata { get; set; }
    }

    /// <summary>
    /// One identity, as read. A single-column projection and not a scalar query: Dapper maps
    /// <c>object</c> to its own dynamic row type, so <c>QueryAsync&lt;object&gt;</c> hands back a
    /// <c>DapperRow</c> rather than the value in it.
    /// </summary>
    internal sealed class IdRow
    {
        public object? Id { get; set; }
    }

    /// <summary>One property change, as read.</summary>
    internal sealed class ChangeRow
    {
        public object? AuditEntryId { get; set; }
        public string PropertyName { get; set; } = string.Empty;
        public string? OldValue { get; set; }
        public string? NewValue { get; set; }
        public bool IsRedacted { get; set; }
        public bool IsTruncated { get; set; }
    }

    /// <summary>The model for a row, with its timestamp stamped UTC.</summary>
    internal static AuditEntry ToEntry(EntryRow row) => new()
    {
        Id            = ToGuid(row.Id),
        EntityName    = row.EntityName,
        EntityId      = row.EntityId,
        Action        = (AuditAction)row.Action,
        Actor         = row.Actor,
        CorrelationId = row.CorrelationId,
        // SQL Server and SQLite both return Kind.Unspecified, which then serializes without a "Z"
        // and is read by every client as local time — an hour or eight wrong about when a change
        // happened. The EF Core store fixes this with a value converter; this is the same fix, so
        // the two stores agree about what a stored timestamp means.
        OccurredAt    = Utc(row.OccurredAt),
        Metadata      = row.Metadata
    };

    /// <summary>The model for a property-change row.</summary>
    internal static AuditPropertyChange ToChange(ChangeRow row) => new()
    {
        AuditEntryId = ToGuid(row.AuditEntryId),
        PropertyName = row.PropertyName,
        OldValue     = row.OldValue,
        NewValue     = row.NewValue,
        IsRedacted   = row.IsRedacted,
        IsTruncated  = row.IsTruncated
    };

    /// <summary>
    /// A GUID from whatever the engine returned for it: a <see cref="Guid"/> on SQL Server and
    /// PostgreSQL, <c>TEXT</c> on SQLite, and a 16-byte blob from a database that chose to store it
    /// that way.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is not a GUID in any of those forms.</exception>
    internal static Guid ToGuid(object? value) => value switch
    {
        Guid guid    => guid,
        string text  => Guid.Parse(text, CultureInfo.InvariantCulture),
        byte[] bytes => new Guid(bytes),
        null => throw new InvalidOperationException(
            "An audit row has a null identity column. The schema is not the one this store expects — " +
            "see DapperSchema.Script(dialect) for the shape it does."),
        _ => throw new InvalidOperationException(
            $"Cannot read an audit row's identity from a {value.GetType().Name}. Expected a GUID, a " +
            "string or a 16-byte array.")
    };

    /// <summary>Everything this library writes is UTC; an unspecified value has lost its kind, not its zone.</summary>
    internal static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc   => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _                  => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
