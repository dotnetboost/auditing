namespace DotNetBoost.Auditing.MongoDb;

/// <summary>
/// Collection naming and index behaviour for the one collection this store adds to your database.
/// Configured through the <c>configure</c> argument of <c>UseMongoDb(...)</c>.
/// </summary>
public sealed class MongoDbOptions
{
    /// <summary>
    /// Collection holding the entry documents. Default: <c>auditEntries</c>.
    /// <para>
    /// <b>One collection, not two.</b> The relational stores keep the property changes in a table of
    /// their own, and the reason given there was indexability: in SQL, a JSON column makes "every
    /// change anyone ever made to <c>Price</c>" an unindexable scan. MongoDB indexes the fields of
    /// an array natively, so the same question is served by a multikey index on
    /// <c>Changes.PropertyName</c> and the argument does not carry over.
    /// </para>
    /// <para>
    /// What does carry over is the shape of the data: an entry is written once, read whole and never
    /// updated, which is exactly the aggregate a document is for. Embedding also makes a single
    /// insert atomic by itself — no transaction needed for the entry to be complete — and makes
    /// deleting one entry take its changes with it, with no second delete and no cascade to depend
    /// on.
    /// </para>
    /// <para>
    /// This is deliberately the opposite of what <c>DotNetBoost.Media</c> does with its
    /// attachments, which live in their own collection because it has to sort by a field of the
    /// matched array element — and MongoDB sorts an array field by its smallest or largest element
    /// rather than the one a filter matched. Nothing here sorts by a property change; entries sort
    /// by when they happened.
    /// </para>
    /// </summary>
    public string EntriesCollection { get; set; } = "auditEntries";

    /// <summary>
    /// Whether the store creates its indexes when the host starts. Default: <c>true</c>.
    /// <para>
    /// Without them, reading one entity's history is a collection scan — fine at a thousand
    /// documents and not at a million, and an audit collection only grows. Turn this off only when
    /// the indexes are created out of band (a migration, an ops script), and never to work around a
    /// failure: creating them needs <c>createIndex</c> on the database, and a role without it should
    /// fail the host at startup rather than run unindexed.
    /// </para>
    /// </summary>
    public bool CreateIndexes { get; set; } = true;
}
