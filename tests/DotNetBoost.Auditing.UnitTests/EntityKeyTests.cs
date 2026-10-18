using DotNetBoost.Auditing.Core.Metadata;
using System.Globalization;

namespace DotNetBoost.Auditing.UnitTests;

/// <summary>
/// DotNetBoost Entity Addressing v1, asserted rather than claimed. These are the rules the sibling
/// libraries implement independently; if one of them changes, the constant must change with it.
/// </summary>
public class EntityKeyTests
{
    [Fact]
    public void AddressingVersion_is_the_version_this_implementation_conforms_to() =>
        Assert.Equal("1.0", EntityKey.AddressingVersion);

    [Fact]
    public void Guid_keys_use_D_format() =>
        Assert.Equal(
            "0f8fad5b-d9cb-469f-a165-70867728950e",
            EntityKey.ToKeyString(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e")));

    [Fact]
    public void Numeric_keys_are_formatted_invariantly_whatever_the_ambient_culture()
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            // de-DE groups with '.' and would turn 1234567 into "1.234.567" through a
            // culture-sensitive ToString — a key that no other culture can read back.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal("1234567", EntityKey.ToKeyString(1234567));
            Assert.Equal("1234567", EntityKey.ToKeyString(1234567L));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void An_empty_key_is_refused() =>
        Assert.Throws<InvalidOperationException>(() => EntityKey.ToKeyString(""));

    [Fact]
    public void A_key_longer_than_the_column_is_refused()
    {
        var tooLong = new string('x', EntityKey.MaxLength + 1);
        var error   = Assert.Throws<InvalidOperationException>(() => EntityKey.ToKeyString(tooLong));

        Assert.Contains(EntityKey.MaxLength.ToString(CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0L)]
    [InlineData((short)0)]
    [InlineData((byte)0)]
    [InlineData("")]
    [InlineData(null)]
    public void Default_keys_read_as_unset(object? value) => Assert.True(EntityKey.IsUnset(value));

    [Fact]
    public void Empty_guid_reads_as_unset() => Assert.True(EntityKey.IsUnset(Guid.Empty));

    [Fact]
    public void An_unrecognised_type_reads_as_SET_even_at_its_own_default() =>
        // The documented rule: Core cannot reference a store's types, so a strongly-typed id at its
        // default looks assigned. An accessor for such a key must return null itself.
        Assert.False(EntityKey.IsUnset(default(DateTime)));

    [Fact]
    public void Entity_names_come_from_the_attribute_when_there_is_one()
    {
        Assert.Equal("Person", AuditEntityNames.For(typeof(PersonEntity)));
        Assert.Equal("Product", AuditEntityNames.For(typeof(Product)));
    }

    [Fact]
    public void The_two_length_limits_are_the_ones_the_spec_fixes()
    {
        Assert.Equal(64, EntityKey.MaxLength);
        Assert.Equal(128, AuditEntityNames.MaxLength);
    }
}
