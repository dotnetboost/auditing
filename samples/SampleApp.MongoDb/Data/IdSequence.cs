using MongoDB.Bson;
using MongoDB.Driver;

namespace SampleApp.MongoDb.Data;

/// <summary>
/// Hands out sequential <c>int</c> keys, which MongoDB does not generate. The EF Core sample gets
/// them from the database's identity column; here they come from a <c>counters</c> collection — the
/// standard MongoDB pattern, and atomic, because <c>findAndModify</c> is.
/// <para>
/// A greenfield MongoDB model would use an <c>ObjectId</c> and delete this. These documents keep
/// <c>int</c> keys so the routes, the responses and the dashboard are unchanged from the EF Core
/// sample — and because the trail is addressed by the key as a <b>string</b>, which is what makes
/// <c>GET /api/audit?entityName=Product&amp;entityId=42</c> read the same against all three stores.
/// </para>
/// <para>
/// Note which problem this solves and which it does not. A key has to exist before an entry can be
/// addressed by it, and here it exists <i>before</i> the insert rather than after — so, unlike the
/// EF Core store's deferred second save, recording never has to wait for the write. What it does not
/// solve is the pairing: see <c>POST /api/products/{id}/fail</c>.
/// </para>
/// </summary>
/// <param name="database">The database holding the counters collection.</param>
public sealed class IdSequence(IMongoDatabase database)
{
    private readonly IMongoCollection<BsonDocument> _counters =
        database.GetCollection<BsonDocument>("counters");

    /// <summary>The next key for <paramref name="name"/>, starting at 1.</summary>
    /// <param name="name">The counter's name — a collection name, by convention.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The next key.</returns>
    public async Task<int> NextAsync(string name, CancellationToken ct = default)
    {
        var updated = await _counters.FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", name),
            Builders<BsonDocument>.Update.Inc("value", 1),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
            ct);

        return updated["value"].AsInt32;
    }
}
