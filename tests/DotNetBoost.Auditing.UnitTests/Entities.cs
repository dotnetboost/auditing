using DotNetBoost.Auditing.Core.Attributes;
using System.ComponentModel.DataAnnotations;

namespace DotNetBoost.Auditing.UnitTests;

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}

[AuditEntity("Person")]
public class PersonEntity
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";

    [AuditRedact]
    public string PasswordHash { get; set; } = "";

    [AuditIgnore]
    public DateTime LastSeenAt { get; set; }
}

[AuditIgnore]
public class Heartbeat
{
    public int Id { get; set; }
}

[Audited]
public class Invoice
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}

[AuditEntity("Query")]
[AuditRetention(Days = 90)]
public class SearchQuery
{
    public long Id { get; set; }
}

[AuditRetention(Days = 0)]
public class ZeroRetention
{
    public int Id { get; set; }
}

public class Contradiction
{
    public int Id { get; set; }

    [AuditIgnore]
    [AuditRedact]
    public string Secret { get; set; } = "";
}

public class CompositeKeyed
{
    [Key] public int TenantId { get; set; }
    [Key] public int LocalId { get; set; }
}

public class Unkeyed
{
    public string Label { get; set; } = "";
}

/// <summary>The states a shipment can be in — an enum, which a diff has to compare as a value.</summary>
public enum ShipmentStatus
{
    Pending = 0,
    Dispatched = 1
}

/// <summary>
/// The property shapes a reflection diff has to be right about: a nullable value, a
/// <c>byte[]</c> that has to be compared by content, an enum, and the two kinds of property it must
/// not compare at all — a navigation to another entity and a collection.
/// </summary>
public class Shipment
{
    public int Id { get; set; }

    public string? Reference { get; set; }

    public DateOnly? DispatchedOn { get; set; }

    public ShipmentStatus Status { get; set; }

    /// <summary>A concurrency token. Two reads of one row give two arrays holding the same bytes.</summary>
    public byte[] RowVersion { get; set; } = [];

    /// <summary>A navigation property. Comparing it by reference would report a change on every write.</summary>
    public PersonEntity? Recipient { get; set; }

    /// <summary>A collection. Same problem, and nothing a single trail column could hold.</summary>
    public List<string> Labels { get; set; } = [];
}

/// <summary>A clock that does not move, so an assertion about a timestamp is an equality.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>
/// A second type claiming the stored name "Person", to prove the collision is caught at startup.
/// Nested in a container rather than its own namespace so the fixtures stay in one file.
/// </summary>
public static class Duplicate
{
    [AuditEntity("Person")]
    public class PersonEntity
    {
        public int Id { get; set; }
    }
}
