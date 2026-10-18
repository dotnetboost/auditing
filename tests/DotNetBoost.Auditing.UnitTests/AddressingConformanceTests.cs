using DotNetBoost.Auditing.Core.Metadata;
using DotNetBoost.Auditing.TestSupport;

namespace DotNetBoost.Auditing.UnitTests;

/// <summary>
/// The addressing conformance suite, pointed at this library's own implementation.
/// </summary>
/// <remarks>
/// Trivially green, and worth having anyway for two reasons. It holds this library to the rules it
/// publishes rather than letting <c>AddressingVersion</c> be a claim, and it keeps the suite itself
/// working — a conformance suite that nobody runs is a conformance suite that has quietly stopped
/// compiling by the time a consumer tries to derive from it.
/// <para>
/// It cannot be pointed at <c>DotNetBoost.Media</c> or <c>DotNetBoost.MultiLingual</c> from here:
/// that would need a project reference to them, which is the one thing this suite exists to avoid.
/// The comparison belongs in an application that already has both, or in the sample.
/// </para>
/// </remarks>
public sealed class SelfAddressingConformanceTests : EntityAddressingConformance
{
    protected override string Implementation => "DotNetBoost.Auditing (itself)";

    protected override string ConformsToVersion => EntityKey.AddressingVersion;

    protected override int MaxKeyLength => EntityKey.MaxLength;

    protected override int MaxEntityNameLength => AuditEntityNames.MaxLength;

    protected override string FormatKey(object key) => EntityKey.ToKeyString(key);

    protected override bool IsUnsetKey(object? value) => EntityKey.IsUnset(value);

    protected override string ResolveEntityName(Type type) => AuditEntityNames.For(type);
}
