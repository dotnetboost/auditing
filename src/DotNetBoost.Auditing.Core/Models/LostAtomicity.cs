namespace DotNetBoost.Auditing.Core.Models;

/// <summary>
/// What the EF Core capture does when the transaction a save ran in was committed by someone else
/// before the capture wrote its deferred <c>Created</c> entries — so those entries can no longer
/// be atomic with the change they describe. See <c>docs/limitations.md</c>.
/// </summary>
public enum LostAtomicity
{
    /// <summary>
    /// Write the entries anyway, outside the transaction, and report it: to
    /// <see cref="Interfaces.IAuditObserver.AtomicityLost"/> and the
    /// <c>dotnetboost.auditing.atomicity.lost</c> counter. The default, so an application that
    /// composes interceptors this way does not start failing on upgrade.
    /// </summary>
    Report,

    /// <summary>
    /// Fail the save. The change itself is already committed by then, so this does not undo it;
    /// it makes the lost guarantee impossible to miss. Recommended in tests.
    /// </summary>
    Throw
}
