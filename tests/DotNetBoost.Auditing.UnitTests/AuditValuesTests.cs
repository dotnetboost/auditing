using DotNetBoost.Auditing.Core.Services;
using System.Globalization;

namespace DotNetBoost.Auditing.UnitTests;

public class AuditValuesTests
{
    [Fact]
    public void Null_formats_as_null()
    {
        var (text, truncated) = AuditValues.Format(null, 100);

        Assert.Null(text);
        Assert.False(truncated);
    }

    [Fact]
    public void Decimals_and_dates_are_invariant_whatever_the_ambient_culture()
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            // de-DE writes 19,99 for this decimal. A trail that records the comma form cannot be
            // compared against one recorded on a machine set to en-US.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal("19.99", AuditValues.Format(19.99m, 100).Text);
            Assert.Equal(
                "2026-10-16T09:30:00.0000000Z",
                AuditValues.Format(new DateTime(2026, 10, 16, 9, 30, 0, DateTimeKind.Utc), 100).Text);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Booleans_are_lower_case_to_match_the_json_metadata_column() =>
        Assert.Equal("true", AuditValues.Format(true, 100).Text);

    [Fact]
    public void A_short_byte_array_keeps_its_value_as_hex() =>
        // A rowversion or a hash: the value is the point.
        Assert.Equal("0x0102FF", AuditValues.Format(new byte[] { 1, 2, 255 }, 100).Text);

    [Fact]
    public void A_long_byte_array_records_only_its_length()
    {
        // Refusing to copy a file into the audit table. The trail of a table is quickly bigger
        // than the table.
        var (text, truncated) = AuditValues.Format(new byte[5000], 4000);

        Assert.Equal("byte[5000]", text);
        Assert.False(truncated);
    }

    [Fact]
    public void An_oversized_value_is_cut_to_the_limit_INCLUDING_the_marker()
    {
        var (text, truncated) = AuditValues.Format(new string('x', 50), 10);

        Assert.True(truncated);
        // The store was told the column holds 10. It must never be handed 11.
        Assert.Equal(10, text!.Length);
        Assert.EndsWith(AuditValues.TruncationSuffix, text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_exactly_at_the_limit_is_not_truncated()
    {
        var (text, truncated) = AuditValues.Format(new string('x', 10), 10);

        Assert.False(truncated);
        Assert.Equal(new string('x', 10), text);
    }

    [Fact]
    public void Enums_record_their_name_rather_than_their_number() =>
        Assert.Equal("Updated", AuditValues.Format(Core.Models.AuditAction.Updated, 100).Text);
}
