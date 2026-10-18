using System.Globalization;

namespace DotNetBoost.Auditing.Core.Services;

/// <summary>
/// Turns a reported value into the string that is stored.
/// <para>
/// One place, because this is where culture bugs and oversized rows come from. A decimal formatted
/// under <c>de-DE</c> and read back under <c>en-US</c> is a different number; a <c>byte[]</c>
/// formatted naively is a base64 blob in a table that is already the largest in the database.
/// </para>
/// </summary>
internal static class AuditValues
{
    /// <summary>The marker appended to a value that was cut short, so a reader can see it was.</summary>
    internal const string TruncationSuffix = "…";

    /// <summary>
    /// <paramref name="value"/> as its stored string, and whether it had to be cut short.
    /// </summary>
    internal static (string? Text, bool Truncated) Format(object? value, int maxLength)
    {
        if (value is null)
            return (null, false);

        var text = value switch
        {
            string s          => s,
            bool b            => b ? "true" : "false",
            Guid g            => g.ToString("D", CultureInfo.InvariantCulture),
            // Round-trippable, to the tick, with the offset: a trail whose timestamps cannot be
            // parsed back is a trail of strings.
            DateTime d        => d.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset d  => d.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan t        => t.ToString("c", CultureInfo.InvariantCulture),
            byte[] bytes      => FormatBytes(bytes),
            Enum e            => e.ToString(),
            IFormattable f    => f.ToString(null, CultureInfo.InvariantCulture),
            _                 => value.ToString() ?? string.Empty
        };

        if (text.Length <= maxLength)
            return (text, false);

        // Cut to the limit INCLUDING the marker, so a store sizing a column at MaxValueLength
        // cannot be handed a longer string than it was told to expect.
        var keep = Math.Max(0, maxLength - TruncationSuffix.Length);
        return (string.Concat(text.AsSpan(0, keep), TruncationSuffix), true);
    }

    /// <summary>
    /// A short array as hex, a long one as its length alone.
    /// <para>
    /// The short case is there for the arrays that actually matter in a trail — a
    /// <c>rowversion</c>, a 16-byte hash — where the value is the point. The long case refuses to
    /// copy a file into the audit table: the trail of a table is quickly bigger than the table, and
    /// nobody has ever read a base64 image out of an audit row.
    /// </para>
    /// </summary>
    private static string FormatBytes(byte[] bytes) =>
        bytes.Length switch
        {
            0        => "0x",
            <= 32    => "0x" + Convert.ToHexString(bytes),
            _        => $"byte[{bytes.Length.ToString(CultureInfo.InvariantCulture)}]"
        };
}
