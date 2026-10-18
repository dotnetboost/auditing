using DotNetBoost.Auditing.Core.Attributes;
using DotNetBoost.Auditing.Core.Interfaces;

namespace DotNetBoost.Auditing.ProviderTests;

/// <summary>
/// The entities every store's tests share. One set rather than one per store: the stores are tested
/// against each other here, and two <c>Product</c> classes with different attributes would make
/// "the same entity" mean two things.
/// </summary>
[AuditEntity("Product")]
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }

    [AuditRedact]
    public string SupplierSecret { get; set; } = "";

    [AuditIgnore]
    public DateTime LastViewedAt { get; set; }
}

/// <summary>A client-assigned key, so nothing about it has to be deferred past the save.</summary>
public class Account
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";

    [AuditRedact]
    public string PasswordHash { get; set; } = "";
}

[AuditIgnore]
public class Heartbeat
{
    public int Id { get; set; }
    public DateTime At { get; set; }
}

[Audited]
public class Invoice
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}

/// <summary>A key EF knows about and reflection does not.</summary>
public class FluentlyKeyed
{
    public int Reference { get; set; }
    public string Label { get; set; } = "";
}

public class CompositeKeyed
{
    public int TenantId { get; set; }
    public int LocalId { get; set; }
    public string Label { get; set; } = "";
}

public class Order
{
    public int Id { get; set; }
    public string Reference { get; set; } = "";
    public ShippingAddress Address { get; set; } = new();
}

/// <summary>Owned: shares the order's key, so it has no address of its own. See EntityDiff.</summary>
public class ShippingAddress
{
    public string City { get; set; } = "";
    public string PostCode { get; set; } = "";
}

[AuditEntity("Query")]
[AuditRetention(Days = 90)]
public class SearchQuery
{
    public long Id { get; set; }
}

/// <summary>
/// An actor the test can change between calls — a function rather than a string, because the
/// atomicity tests need it to start answering differently part-way through a save.
/// </summary>
internal sealed class MutableActorAccessor : IAuditActorAccessor
{
    public Func<string?> Actor { get; set; } = () => null;

    public string? GetActor() => Actor();
}
