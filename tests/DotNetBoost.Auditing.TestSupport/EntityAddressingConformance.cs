using DotNetBoost.Auditing.Core.Metadata;
using System.Globalization;
using Xunit;

namespace DotNetBoost.Auditing.TestSupport;

/// <summary>
/// The <b>DotNetBoost Entity Addressing v1</b> conformance suite: everything two libraries have to
/// agree about before an application can join their rows on <c>(EntityName, EntityId)</c>.
/// <para>
/// This library, <c>DotNetBoost.Media</c> and <c>DotNetBoost.MultiLingual</c> arrive at the same
/// addressing independently — same name resolution, same key format, same two length limits — and
/// that agreement is what lets an audit entry, a media row and a translation row about one product
/// be matched up. Nothing enforces it: there is deliberately no shared package, so the guarantee
/// rests on three implementations that happen to agree, which is a promise with no test behind it.
/// </para>
/// <para>
/// Derive from this in a test project, point it at another implementation, and the agreement becomes
/// a suite you run:
/// <code>
/// public sealed class MediaAddressingTests : EntityAddressingConformance
/// {
///     protected override string Implementation      =&gt; "DotNetBoost.Media";
///     protected override string ConformsToVersion   =&gt; Media.Core.Metadata.EntityKey.AddressingVersion;
///     protected override int    MaxKeyLength        =&gt; Media.Core.Metadata.EntityKey.MaxLength;
///     protected override int    MaxEntityNameLength =&gt; Media.Core.Metadata.MediaEntityNames.MaxLength;
///
///     protected override string FormatKey(object key)     =&gt; Media.Core.Metadata.EntityKey.ToKeyString(key);
///     protected override bool   IsUnsetKey(object? value) =&gt; Media.Core.Metadata.EntityKey.IsUnset(value);
///     protected override string ResolveEntityName(Type t) =&gt; Media.Core.Metadata.MediaEntityNames.For(t);
/// }
/// </code>
/// </para>
/// <para>
/// Every assertion compares the other implementation against <b>this</b> one. It is deliberately
/// not the spec written out a third time — a third copy of the rules is a third thing to drift.
/// </para>
/// </summary>
public abstract class EntityAddressingConformance
{
    /// <summary>What is being checked, for the failure message — e.g. <c>DotNetBoost.Media</c>.</summary>
    protected abstract string Implementation { get; }

    /// <summary>The other library's published addressing version — its own <c>AddressingVersion</c>.</summary>
    protected abstract string ConformsToVersion { get; }

    /// <summary>Its stored-key length limit.</summary>
    protected abstract int MaxKeyLength { get; }

    /// <summary>Its stored entity-name length limit.</summary>
    protected abstract int MaxEntityNameLength { get; }

    /// <summary>Its key formatting.</summary>
    protected abstract string FormatKey(object key);

    /// <summary>Its unset-key detection.</summary>
    protected abstract bool IsUnsetKey(object? value);

    /// <summary>Its entity-name resolution.</summary>
    protected abstract string ResolveEntityName(Type type);

    /// <summary>A type with no addressing attribute, so both implementations fall back to its name.</summary>
    private sealed class Unattributed
    {
        public int Id { get; set; }
    }

    [Fact]
    public void It_publishes_the_same_addressing_version() =>
        Assert.Equal(EntityKey.AddressingVersion, ConformsToVersion);

    [Fact]
    public void It_uses_the_same_two_length_limits()
    {
        Assert.Equal(EntityKey.MaxLength, MaxKeyLength);
        Assert.Equal(AuditEntityNames.MaxLength, MaxEntityNameLength);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(long.MaxValue)]
    [InlineData((short)7)]
    [InlineData((byte)3)]
    public void It_formats_integral_keys_the_same_way(object key) =>
        Assert.Equal(EntityKey.ToKeyString(key), FormatKey(key));

    [Fact]
    public void It_formats_a_guid_the_same_way()
    {
        var key = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

        Assert.Equal(EntityKey.ToKeyString(key), FormatKey(key));
    }

    [Fact]
    public void It_formats_a_string_key_the_same_way() =>
        Assert.Equal(EntityKey.ToKeyString("SKU-001"), FormatKey("SKU-001"));

    [Fact]
    public void It_formats_keys_the_same_way_under_a_hostile_culture()
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            // de-DE groups with '.' and would turn 1234567 into "1.234.567" through a
            // culture-sensitive ToString. Two libraries disagreeing here would stop joining on a
            // machine whose locale changed, which is the worst kind of bug to find.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal(EntityKey.ToKeyString(1234567), FormatKey(1234567));
            Assert.Equal(EntityKey.ToKeyString(1234567L), FormatKey(1234567L));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(0L)]
    [InlineData((short)0)]
    [InlineData((byte)0)]
    [InlineData("")]
    [InlineData(42)]
    [InlineData("SKU-001")]
    public void It_agrees_about_which_keys_are_unset(object? value) =>
        Assert.Equal(EntityKey.IsUnset(value), IsUnsetKey(value));

    [Fact]
    public void It_agrees_that_an_empty_guid_is_unset()
    {
        Assert.Equal(EntityKey.IsUnset(Guid.Empty), IsUnsetKey(Guid.Empty));
        Assert.Equal(EntityKey.IsUnset(Guid.NewGuid()), IsUnsetKey(Guid.NewGuid()));
    }

    [Fact]
    public void It_falls_back_to_the_type_name_for_an_unattributed_entity() =>
        // The attribute itself differs per library — [AuditEntity], [MediaEntity] — so what is
        // comparable is the fallback, which is the case most entities are in.
        Assert.Equal(AuditEntityNames.For(typeof(Unattributed)), ResolveEntityName(typeof(Unattributed)));

    [Fact]
    public void It_refuses_an_over_long_key()
    {
        var tooLong = new string('x', MaxKeyLength + 1);

        // Both should refuse rather than truncate: half a key addresses a different entity.
        Assert.Throws<InvalidOperationException>(() => EntityKey.ToKeyString(tooLong));
        Assert.ThrowsAny<Exception>(() => FormatKey(tooLong));
    }
}
