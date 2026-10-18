namespace DotNetBoost.Auditing.Core.Metadata;

/// <summary>
/// The stored width of the columns that are identity rather than payload. Every store package
/// builds its schema from these, so the four schemas cannot drift from each other or from what the
/// engine is willing to write.
/// <para>
/// Deliberately constants and not options. A value column is payload and gets a configurable limit
/// (<see cref="AuditingOptions.MaxValueLength"/>); these name <i>who</i> and <i>what</i>, and an
/// application that could widen them would produce rows its own other store could not read.
/// </para>
/// </summary>
public static class AuditColumns
{
    /// <summary>
    /// The longest actor that can be recorded. 256, matching the width the other libraries in the
    /// suite give the same concept.
    /// </summary>
    public const int ActorMaxLength = 256;

    /// <summary>
    /// The longest correlation id that can be recorded. 128 fits a W3C trace parent
    /// (55 characters) with room to spare.
    /// </summary>
    public const int CorrelationIdMaxLength = 128;

    /// <summary>The longest property name that can be recorded. 256 is past any real identifier.</summary>
    public const int PropertyNameMaxLength = 256;
}
