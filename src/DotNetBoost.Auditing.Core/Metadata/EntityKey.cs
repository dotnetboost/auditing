using System.Globalization;

namespace DotNetBoost.Auditing.Core.Metadata;

/// <summary>
/// Formats an entity's primary key as the string stored in <see cref="Models.AuditEntry.EntityId"/>.
/// One column serves <c>int</c>, <c>long</c>, <c>Guid</c> and <c>string</c> keys, so the format has
/// to be stable and culture-independent — a key formatted under <c>ar-SA</c> and read back under
/// <c>en-US</c> must be the same string, or an entity's history stops belonging to it.
/// </summary>
public static class EntityKey
{
    /// <summary>
    /// The entity-addressing spec version this implementation conforms to — <b>DotNetBoost Entity
    /// Addressing v1</b>.
    /// <para>
    /// This library, DotNetBoost.Media and DotNetBoost.MultiLingual resolve entity names and format
    /// keys the same way, <b>independently</b>: the <c>[…Entity("…")]</c> attribute or the type
    /// name, proxy types unwrapped; numbers invariant and GUIDs in <c>"D"</c> format;
    /// <see cref="MaxLength"/> for a key and <see cref="AuditEntityNames.MaxLength"/> for a name.
    /// That agreement is what lets an application join an audit entry to a media row or a
    /// translation row on the pair of columns.
    /// </para>
    /// <para>
    /// There is deliberately <b>no package reference</b> between the libraries that share this —
    /// the file is copied, not referenced, because the day one of them depends on another the suite
    /// has a shared kernel. What keeps the copies honest is that each publishes the same constant
    /// under the same name, so a consumer can assert the versions match and fail at startup rather
    /// than discover a mismatch as rows that stopped joining:
    /// <code>
    /// Assert.Equal(
    ///     DotNetBoost.Auditing.Core.Metadata.EntityKey.AddressingVersion,
    ///     DotNetBoost.Media.Core.Metadata.EntityKey.AddressingVersion);
    /// </code>
    /// That one equality is the whole of the relationship. This library also runs the full rule set
    /// against itself, so the version published here is one it is held to rather than one it merely
    /// claims.
    /// </para>
    /// <para>
    /// <b>Bump it for ANY change</b> to entity-name resolution, key discovery, key formatting,
    /// unset-key detection, or either length limit — a change that is invisible in this library is
    /// exactly the kind that silently stops two libraries addressing the same entity.
    /// </para>
    /// </summary>
    public const string AddressingVersion = "1.0";

    /// <summary>
    /// How long a stored key may be. 64 comfortably fits an <c>int</c>, a <c>long</c>, or a
    /// <c>Guid</c> in "D" format (36 characters), with room for a composite string key.
    /// </summary>
    public const int MaxLength = 64;

    /// <summary>
    /// <paramref name="key"/> as its stored string. Numbers are formatted invariantly, GUIDs in
    /// "D" format, everything else through <see cref="IFormattable"/> where it implements it.
    /// </summary>
    /// <param name="key">The key value to format.</param>
    /// <returns>The key as it is stored.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">The formatted key is empty or longer than <see cref="MaxLength"/>.</exception>
    public static string ToKeyString(object key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var text = key switch
        {
            string s       => s,
            Guid g         => g.ToString("D", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _              => key.ToString() ?? string.Empty
        };

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("An entity key cannot be empty.");

        if (text.Length > MaxLength)
        {
            throw new InvalidOperationException(
                $"Entity key '{text}' is {text.Length} characters; the column holds {MaxLength}.");
        }

        return text;
    }

    /// <summary>
    /// True when <paramref name="value"/> is the CLR default for its key type — an entity whose
    /// store-generated key has not been assigned yet, and so cannot be addressed.
    /// </summary>
    /// <param name="value">The key value to test.</param>
    /// <returns><c>true</c> when the key has not been assigned.</returns>
    /// <remarks>
    /// Only the key types listed below are recognised. A value of any OTHER type reads as SET, even
    /// at its own default — a strongly-typed id struct, or <c>ObjectId.Empty</c> — because Core
    /// cannot reference a store's types to know better.
    /// <para>
    /// A store whose keys use such a type must therefore return <c>null</c> from its
    /// <see cref="Interfaces.IEntityKeyAccessor"/> for the unassigned value: the accessors are the
    /// only callers of this method, so that is the one place the knowledge exists. Left to this
    /// method, an unsaved entity looks addressable and its entries are recorded under the default
    /// value's string form.
    /// </para>
    /// <para>
    /// The same rule, over the same types, as DotNetBoost Entity Addressing v1 requires — see
    /// <see cref="AddressingVersion"/>.
    /// </para>
    /// </remarks>
    public static bool IsUnset(object? value) => value switch
    {
        null     => true,
        int i    => i == 0,
        long l   => l == 0,
        short s  => s == 0,
        byte b   => b == 0,
        Guid g   => g == Guid.Empty,
        string s => s.Length == 0,
        _        => false
    };
}
