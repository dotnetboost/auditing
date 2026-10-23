using DotNetBoost.Auditing.Core.Services;
using System.Globalization;
using System.Text;

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
    public void Truncation_never_splits_a_surrogate_pair()
    {
        // Each emoji is two chars, so an odd limit lands the cut between the halves of one. Half a
        // surrogate pair is not a character — it is invalid UTF-16, which a UTF-8 database either
        // mangles into U+FFFD or refuses outright.
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 20));

        for (var limit = 2; limit <= 20; limit++)
        {
            var (text, truncated) = AuditValues.Format(emoji, limit);

            Assert.True(truncated);
            Assert.True(text!.Length <= limit, $"limit {limit} produced {text.Length} chars");

            // Round-tripping through UTF-8 replaces a lone surrogate, so a value that survives it
            // unchanged contains none. This is the same encoding the database does.
            Assert.Equal(text, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text)));
        }
    }

    [Fact]
    public void Dropping_a_split_pair_costs_a_character_rather_than_validity()
    {
        // The marker takes one char, so the cut lands inside a pair at an EVEN limit: at four, the
        // two chars kept would be the second half of one emoji and the first half of the next. The
        // pair goes entirely instead, leaving one emoji and the marker — three chars where four
        // were allowed. Shorter than the limit is the right way to be wrong.
        var (text, truncated) = AuditValues.Format("\U0001F600\U0001F600\U0001F600", 4);

        Assert.True(truncated);
        Assert.Equal("\U0001F600" + AuditValues.TruncationSuffix, text);
        Assert.Equal(3, text!.Length);
    }

    [Fact]
    public void Enums_record_their_name_rather_than_their_number() =>
        Assert.Equal("Updated", AuditValues.Format(Core.Models.AuditAction.Updated, 100).Text);
}
