namespace DotNetBoost.Auditing.Core;

/// <summary>
/// The names of the metrics the engine emits, for an application subscribing to them.
/// <code>
/// builder.Services.AddOpenTelemetry()
///        .WithMetrics(metrics =&gt; metrics.AddMeter(AuditingTelemetry.MeterName));
/// </code>
/// </summary>
/// <remarks>
/// <b>Nothing is emitted until something subscribes</b>, which is why this is opt-in and why it
/// costs an application that does not want it almost nothing: each instrument is asked whether it
/// is enabled before any tag is built, so an unsubscribed counter is a field read and a branch.
/// <para>
/// <b>What is metered and what is logged are different things on purpose.</b> A counter is for what
/// you watch as a rate — is the trail still being written, and how much is being suppressed. A log
/// line is for what you fix once, which is why the two misconfigurations go to
/// <see cref="Interfaces.IAuditObserver"/> as warnings and are not instruments of their own: they
/// are a property of a deployment, not a signal that varies.
/// </para>
/// </remarks>
public static class AuditingTelemetry
{
    /// <summary>The meter every instrument below belongs to.</summary>
    public const string MeterName = "DotNetBoost.Auditing";

    /// <summary>The meter's version, reported to a subscriber alongside the name.</summary>
    public const string MeterVersion = "1.0.0";

    /// <summary>
    /// Entries the engine recorded, tagged <see cref="TagEntity"/> and <see cref="TagAction"/>.
    /// <para>
    /// <b>The one to alert on, and it is the absence that matters.</b> "This entity recorded nothing
    /// for an hour" is the shape of every way a trail stops covering a table — and it is a question
    /// only this counter can answer, because a suppression report is about the writes that were
    /// declined and says nothing about the ones that should have arrived and did not.
    /// </para>
    /// <para>
    /// It counts what the engine handed to the store, which is not the same as what is committed:
    /// the EF Core store only tracks an entry, and the caller's <c>SaveChanges</c> is what makes it
    /// durable. A rolled-back save therefore counts an entry that no longer exists. Metering the
    /// commit instead would mean the engine knowing when each store becomes durable, which is
    /// exactly what <c>IAuditStore</c> leaves to the store.
    /// </para>
    /// </summary>
    public const string EntriesRecorded = "dotnetboost.auditing.entries.recorded";

    /// <summary>
    /// Writes the engine deliberately did not record, tagged <see cref="TagEntity"/> and
    /// <see cref="TagReason"/> — the reasons on <see cref="Models.AuditSuppression"/>.
    /// <para>
    /// Three of those reasons are logged at Debug, which is off in most production deployments, so
    /// this is the only form in which they are visible there. Read against
    /// <see cref="EntriesRecorded"/>: a ratio that moves is a configuration that changed.
    /// </para>
    /// </summary>
    public const string WritesSuppressed = "dotnetboost.auditing.writes.suppressed";

    /// <summary>
    /// Entries deleted, tagged <see cref="TagOperation"/> — <c>prune</c> or <c>erase</c>.
    /// <para>
    /// The number to have when someone asks why the trail is shorter than they expected. Deleting
    /// is the only irreversible thing the library does, and until now the count existed only in
    /// whatever the scheduled job that called it chose to log.
    /// </para>
    /// </summary>
    public const string EntriesDeleted = "dotnetboost.auditing.entries.deleted";

    /// <summary>The entity's stored name — <c>Product</c>.</summary>
    /// <remarks>
    /// Bounded by the application's model, which is what makes it safe to tag by. <b>The entity's
    /// key is deliberately not a tag anywhere:</b> it is unbounded, so one of those turns a counter
    /// into a time series per row and takes the metrics backend with it. Nor is the actor — the same
    /// problem, and it is personal data that the rest of the library is careful about.
    /// </remarks>
    public const string TagEntity = "audit.entity";

    /// <summary>What was recorded — <c>Created</c>, <c>Updated</c> or <c>Deleted</c>.</summary>
    public const string TagAction = "audit.action";

    /// <summary>Which rule declined the write — a <see cref="Models.AuditSuppression"/> name.</summary>
    public const string TagReason = "audit.reason";

    /// <summary>What deleted the entries — <c>prune</c> or <c>erase</c>.</summary>
    public const string TagOperation = "audit.operation";

    /// <summary><see cref="TagOperation"/> for a retention sweep.</summary>
    public const string OperationPrune = "prune";

    /// <summary><see cref="TagOperation"/> for an erasure of one entity's or one actor's trail.</summary>
    public const string OperationErase = "erase";
}
