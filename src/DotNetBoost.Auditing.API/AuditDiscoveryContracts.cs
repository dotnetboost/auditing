namespace DotNetBoost.Auditing.API;

/// <summary>One declared entity, as <c>GET {prefix}/entities</c> lists it.</summary>
/// <param name="Name">
/// The name its entries are stored under — the value to pass as <c>entityName</c> when reading the
/// trail, which is not necessarily the class name.
/// </param>
/// <param name="ClrType">The class behind it, by simple name.</param>
/// <param name="Audited">
/// Whether a change to it would be recorded <b>right now</b>. This is the engine's own answer, so
/// it accounts for the kill switch and for opt-in mode as well as for the attributes — and
/// <c>false</c> with no <c>[AuditIgnore]</c> in sight is usually how an operator discovers one of
/// those is on.
/// </param>
/// <param name="OptedIn">Whether the class carries <c>[Audited]</c>, which only matters under opt-in mode.</param>
/// <param name="Ignored">Whether the class carries <c>[AuditIgnore]</c> and is never recorded.</param>
/// <param name="RetentionDays">
/// How long its entries are kept once a prune runs, in days: its own <c>[AuditRetention]</c> if it
/// has one, otherwise the configured default. <c>null</c> means nothing is configured either way,
/// so a prune leaves its entries alone — never that they expire immediately.
/// <para>
/// Fractional when the configured default is not a whole number of days, because rounding a
/// retention to report it would state a policy the prune does not apply.
/// </para>
/// </param>
/// <param name="HasOwnRetention">
/// Whether <paramref name="RetentionDays"/> came from the entity's own <c>[AuditRetention]</c>
/// rather than the configured default. The two are kept apart because changing the default moves
/// one and not the other.
/// </param>
public sealed record AuditEntityDescriptor(
    string Name,
    string ClrType,
    bool Audited,
    bool OptedIn,
    bool Ignored,
    double? RetentionDays,
    bool HasOwnRetention);

/// <summary>One property, as <c>GET {prefix}/entities/{entityName}</c> describes it.</summary>
/// <param name="Name">
/// The property as named on the class, which is how <c>propertyName</c> is spelled in a recorded
/// change — so a client can match the two up without guessing at a casing convention.
/// </param>
/// <param name="Type">The CLR type name — the underlying type for a nullable.</param>
/// <param name="Nullable">Whether the property is a nullable value type.</param>
/// <param name="Rule">
/// <c>Record</c>, <c>Redact</c> or <c>Ignore</c>. Serialized as its name rather than its number,
/// for the same reason an entry's <c>action</c> is: a client reads it without a lookup table, and
/// a renumbering cannot silently change what a stored response meant.
/// <para>
/// <c>Redact</c> is the one worth surfacing in a UI. It is why a change to that property arrives
/// with both values <c>null</c> and <c>isRedacted</c> set, and saying so next to the field is more
/// honest than rendering a blank.
/// </para>
/// </param>
public sealed record AuditPropertyDescriptor(
    string Name,
    string Type,
    bool Nullable,
    string Rule);

/// <summary>
/// The shape of one audited entity. What the model says about it — never anything recorded.
/// </summary>
/// <param name="Name">The name its entries are stored under.</param>
/// <param name="ClrType">The class behind it, by simple name.</param>
/// <param name="Audited">Whether a change to it would be recorded right now.</param>
/// <param name="RetentionDays">Its effective retention in days, fractional if need be, or <c>null</c> when none is configured.</param>
/// <param name="HasOwnRetention">Whether that retention is its own rather than the configured default.</param>
/// <param name="Properties">
/// Every public instance property of the class, with the rule each falls under. The ignored ones
/// are listed too: "this property is deliberately not in the trail" is the answer to a question
/// somebody is otherwise going to ask by reading the source.
/// </param>
public sealed record AuditEntitySchema(
    string Name,
    string ClrType,
    bool Audited,
    double? RetentionDays,
    bool HasOwnRetention,
    IReadOnlyList<AuditPropertyDescriptor> Properties);
