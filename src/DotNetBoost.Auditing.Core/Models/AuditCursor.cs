using System.Globalization;
using System.Text;

namespace DotNetBoost.Auditing.Core.Models;

/// <summary>
/// A position in a trail — the <c>(OccurredAt, Id)</c> of an entry already read — for asking for the
/// page after it with <see cref="AuditQuery.After"/>.
/// <code>
/// var page = await audit.QueryAsync(new AuditQuery { EntityName = "Product" }, ct);
///
/// while (page.NextCursor is { } cursor)
/// {
///     page = await audit.QueryAsync(new AuditQuery { EntityName = "Product", After = cursor }, ct);
///
///     if (page.Entries.Count == 0)
///         break;
/// }
/// </code>
/// </summary>
/// <remarks>
/// <b>Why a cursor exists alongside <see cref="AuditQuery.Skip"/>.</b> An offset makes the database
/// read and discard everything it skips, so page 4,000 of a trail costs 200,000 rows to serve — on
/// the one table in the application that only ever grows. Worse, it is not stable: an entry arriving
/// between two requests shifts every later row by one, so a reader paging a live trail sees a row
/// twice and misses another. An insert cannot move a row across a boundary that is defined by the
/// rows themselves, and the page costs what the first page costs — measured at depth 500,000 of a
/// million-entry trail on PostgreSQL: 0.1ms by cursor against 36ms by offset.
/// <para>
/// <b>The flat cost needs the engine's help, and SQL Server does not give it.</b> The keyset
/// predicate is spelled as a row-value comparison where the engine has one, which becomes an index
/// seek; SQL Server has none and gets the portable <c>OR</c> form, which the planner applies as a
/// filter over an index scan, so a deeper page there reads more rows. Stability is unaffected — it
/// is a property of what a cursor <i>is</i>, not of how the predicate is written — so a cursor is
/// still the right way to walk a trail on SQL Server. See <c>docs/limitations.md</c>.
/// </para>
/// <para>
/// It works because the sort every store already uses — <c>OccurredAt</c> descending with
/// <c>Id</c> as the tiebreaker — is a <i>total</i> order. Without the tiebreaker two entries
/// recorded in the same tick would have no defined position relative to each other and a cursor
/// between them would be ambiguous; it is there for the same reason, and this is what it buys.
/// </para>
/// <para>
/// <b>Round-trip it, do not compose it.</b> <see cref="ToString"/> and <see cref="TryParse"/> are
/// the supported way across a process boundary — the REST endpoints take the token as
/// <c>?after=…</c> and return the next one with the page. The shape inside is not a contract.
/// </para>
/// </remarks>
/// <param name="OccurredAt">The entry's timestamp. UTC.</param>
/// <param name="Id">The entry's id, which breaks a tie between two entries at the same instant.</param>
public readonly record struct AuditCursor(DateTime OccurredAt, Guid Id)
{
    private const char Separator = '|';

    /// <summary>The cursor pointing at <paramref name="entry"/>, for asking what comes after it.</summary>
    /// <param name="entry">The entry whose position to take.</param>
    /// <returns>The cursor.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <c>null</c>.</exception>
    public static AuditCursor Of(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new AuditCursor(entry.OccurredAt, entry.Id);
    }

    /// <summary>
    /// The cursor as one URL-safe token.
    /// </summary>
    /// <returns>The token.</returns>
    /// <remarks>
    /// Base64url over the round-trippable text, so it survives a query string with no escaping and
    /// reads as something to pass back rather than something to build. Not a secret and not
    /// tamper-proof — anyone can decode it, and a forged cursor only asks for a different page of
    /// data the caller may already read.
    /// </remarks>
    public override string ToString()
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{OccurredAt:O}{Separator}{Id:D}");

        // Base64url by hand rather than through System.Buffers.Text.Base64Url, which is .NET 9 and
        // this package also targets net8.0. Six characters of substitution is cheaper than a
        // conditional compilation block for the same six characters.
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Reads a token from <see cref="ToString"/> back into a cursor.
    /// </summary>
    /// <param name="token">The token, or <c>null</c>.</param>
    /// <param name="cursor">The cursor, when it parsed.</param>
    /// <returns><c>false</c> for anything this did not produce — a truncated token, a tampered one, or <c>null</c>.</returns>
    /// <remarks>
    /// A bool rather than an exception because the usual caller is a query string, where a bad
    /// value is the client's mistake and the endpoint turns it into one <c>400</c> with a message
    /// of its own.
    /// </remarks>
    public static bool TryParse(string? token, out AuditCursor cursor)
    {
        cursor = default;

        if (string.IsNullOrWhiteSpace(token))
            return false;

        string text;

        try
        {
            var padded = token.Replace('-', '+').Replace('_', '/');

            // Convert.FromBase64String insists on the padding that ToString() trimmed.
            text = Encoding.UTF8.GetString(
                Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '=')));
        }
        catch (FormatException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            // Valid base64 of something that is not UTF-8 — a token from somewhere else entirely.
            return false;
        }

        var separator = text.IndexOf(Separator, StringComparison.Ordinal);

        if (separator <= 0)
            return false;

        var occurredAt = text.AsSpan(0, separator);
        var id         = text.AsSpan(separator + 1);

        // Round-trip exactly, and UTC: a cursor whose instant moved by a tick or an hour would
        // silently skip entries or repeat them, which is the bug paging by cursor exists to avoid.
        if (!DateTime.TryParseExact(
                occurredAt,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsedAt))
        {
            return false;
        }

        if (!Guid.TryParseExact(id, "D", out var parsedId))
            return false;

        cursor = new AuditCursor(
            parsedAt.Kind == DateTimeKind.Utc ? parsedAt : parsedAt.ToUniversalTime(),
            parsedId);

        return true;
    }
}
