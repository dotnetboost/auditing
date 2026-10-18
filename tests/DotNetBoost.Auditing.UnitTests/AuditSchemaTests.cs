using DotNetBoost.Auditing.Core.Metadata;

namespace DotNetBoost.Auditing.UnitTests;

/// <summary>
/// The description the discovery endpoints hand out. Its one real obligation is that it describes
/// the same properties the recorder acts on — a description that omitted a redacted property would
/// be worse than no description at all.
/// </summary>
public class AuditSchemaTests
{
    private static AuditPropertyInfo Property<T>(string name) =>
        AuditSchema.Describe(typeof(T)).Single(p => p.Name == name);

    [Fact]
    public void Every_public_property_is_described_including_the_ignored_ones()
    {
        var names = AuditSchema.Describe(typeof(PersonEntity))
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // LastSeenAt is [AuditIgnore] and is listed anyway: "deliberately not in the trail" is the
        // answer to a question somebody would otherwise go and read the source for.
        Assert.Equal(
            [
                nameof(PersonEntity.Email),
                nameof(PersonEntity.Id),
                nameof(PersonEntity.LastSeenAt),
                nameof(PersonEntity.PasswordHash)
            ],
            names);
    }

    [Fact]
    public void A_property_is_described_under_the_rule_the_plan_gives_it()
    {
        var plan = AuditPlan.For(typeof(PersonEntity));

        foreach (var property in AuditSchema.Describe(typeof(PersonEntity)))
            Assert.Equal(plan.RuleFor(property.Name), property.Rule);

        Assert.Equal(AuditPropertyRule.Redact, Property<PersonEntity>(nameof(PersonEntity.PasswordHash)).Rule);
        Assert.Equal(AuditPropertyRule.Ignore, Property<PersonEntity>(nameof(PersonEntity.LastSeenAt)).Rule);
        Assert.Equal(AuditPropertyRule.Record, Property<PersonEntity>(nameof(PersonEntity.Email)).Rule);
    }

    [Fact]
    public void A_property_is_named_the_way_a_recorded_change_names_it() =>
        // AuditPropertyChange.PropertyName is the property as named on the class. A description in
        // some other casing would not match up with the trail.
        Assert.Equal(nameof(PersonEntity.PasswordHash), Property<PersonEntity>(nameof(PersonEntity.PasswordHash)).Name);

    [Fact]
    public void A_nullable_is_described_by_its_underlying_type()
    {
        var total = Property<NullableTotal>(nameof(NullableTotal.Total));

        Assert.Equal(nameof(Decimal), total.ClrType);
        Assert.True(total.IsNullable);

        var id = Property<NullableTotal>(nameof(NullableTotal.Id));
        Assert.Equal(nameof(Int32), id.ClrType);
        Assert.False(id.IsNullable);
    }

    [Fact]
    public void An_indexer_is_not_a_property_anything_could_record() =>
        Assert.DoesNotContain(AuditSchema.Describe(typeof(Indexed)), p => p.Name == "Item");

    [Fact]
    public void Describing_the_same_type_twice_hands_back_the_same_description() =>
        // Cached, because it is a fact about the class: reflecting per request over every entity in
        // a listing is the kind of cost that only shows up under load.
        Assert.Same(AuditSchema.Describe(typeof(Product)), AuditSchema.Describe(typeof(Product)));

    [Fact]
    public void A_null_type_is_refused() =>
        Assert.Throws<ArgumentNullException>(() => AuditSchema.Describe(null!));
}

public class NullableTotal
{
    public int Id { get; set; }
    public decimal? Total { get; set; }
}

public class Indexed
{
    public int Id { get; set; }
    public string this[int index] => index.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
