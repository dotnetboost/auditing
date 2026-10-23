using DotNetBoost.Auditing.Core.Models;

namespace DotNetBoost.Auditing.UnitTests;

/// <summary>
/// The cursor token. It crosses a process boundary as a query-string value and comes back, so what
/// matters is that it round-trips <b>exactly</b> — a cursor whose instant moved by one tick would
/// silently skip an entry or repeat one, which is the bug paging by cursor exists to avoid.
/// </summary>
public class AuditCursorTests
{
    [Fact]
    public void A_token_round_trips_to_the_tick()
    {
        // A timestamp with a non-zero tick component, which is what "O" is for and what a second's
        // precision would quietly lose.
        var cursor = new AuditCursor(
            new DateTime(2026, 10, 22, 9, 30, 15, DateTimeKind.Utc).AddTicks(1234567),
            Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"));

        Assert.True(AuditCursor.TryParse(cursor.ToString(), out var parsed));

        Assert.Equal(cursor, parsed);
        Assert.Equal(cursor.OccurredAt.Ticks, parsed.OccurredAt.Ticks);
        Assert.Equal(DateTimeKind.Utc, parsed.OccurredAt.Kind);
    }

    [Fact]
    public void A_token_is_safe_in_a_query_string_without_escaping()
    {
        var token = new AuditCursor(DateTime.UtcNow, Guid.NewGuid()).ToString();

        // Base64url: no '+', '/' or '=' to be mangled by a client that forgot to encode it.
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        Assert.Equal(token, Uri.EscapeDataString(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-token")]
    [InlineData("!!!!")]
    public void Anything_this_did_not_produce_is_refused(string? token)
    {
        Assert.False(AuditCursor.TryParse(token, out var cursor));
        Assert.Equal(default, cursor);
    }

    [Fact]
    public void A_truncated_token_is_refused_rather_than_read_as_a_different_position()
    {
        var token = new AuditCursor(DateTime.UtcNow, Guid.NewGuid()).ToString();

        // Half a token could decode to something parseable, which would be a cursor pointing
        // somewhere nobody asked about — a silently wrong page rather than an error.
        Assert.False(AuditCursor.TryParse(token[..(token.Length / 2)], out _));
    }

    [Fact]
    public void A_token_of_valid_base64_that_is_not_a_cursor_is_refused()
    {
        var notACursor = Convert.ToBase64String("hello|world"u8.ToArray()).TrimEnd('=');

        Assert.False(AuditCursor.TryParse(notACursor, out _));
    }

    [Fact]
    public void A_cursor_takes_its_position_from_an_entry()
    {
        var entry = new AuditEntry
        {
            Id            = Guid.NewGuid(),
            EntityName    = "Product",
            EntityId      = "42",
            Action        = AuditAction.Updated,
            CorrelationId = "c1",
            OccurredAt    = new DateTime(2026, 10, 22, 9, 30, 0, DateTimeKind.Utc)
        };

        var cursor = AuditCursor.Of(entry);

        Assert.Equal(entry.OccurredAt, cursor.OccurredAt);
        Assert.Equal(entry.Id, cursor.Id);
    }

    [Fact]
    public void Of_refuses_null_rather_than_throwing_a_null_reference() =>
        Assert.Throws<ArgumentNullException>(() => AuditCursor.Of(null!));
}
