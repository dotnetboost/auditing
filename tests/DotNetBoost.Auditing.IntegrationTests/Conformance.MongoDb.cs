using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.MongoDb;
using MongoDB.Bson;
using MongoDB.Driver;
using DotNetBoost.Auditing.TestSupport;

namespace DotNetBoost.Auditing.IntegrationTests;

/// <summary>
/// The MongoDB store against a real server — the first time any statement it issues has been
/// accepted by one.
/// </summary>
[Collection("mongodb")]
public sealed class MongoDbConformance(MongoFixture fixture) : StoreConformance
{
    private IMongoDatabase _database = null!;
    private readonly MongoDbOptions _options = new();

    protected override async Task PrepareAsync()
    {
        _database = new MongoClient(fixture.ConnectionString).GetDatabase("audit_mongo");

        // The package's own index creation, run for real.
        await MongoAuditIndexes.EnsureCreatedAsync(_database, _options);

        await _database.GetCollection<BsonDocument>(_options.EntriesCollection)
            .DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
    }

    protected override Task<IAuditStore> OpenAsync() =>
        Task.FromResult<IAuditStore>(new MongoAuditStore(_database, _options));

    /// <summary>Nothing: the MongoDB store writes when it is called.</summary>
    protected override Task FlushAsync() => Task.CompletedTask;

    /// <summary>
    /// The embedded changes, summed across the documents. There is no second collection to count —
    /// which is the point of embedding, and why an orphaned change row is not a thing that can
    /// happen here.
    /// </summary>
    protected override async Task<long> CountChangesAsync()
    {
        var documents = await _database
            .GetCollection<BsonDocument>(_options.EntriesCollection)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .ToListAsync();

        return documents.Sum(d => d.Contains("Changes") ? d["Changes"].AsBsonArray.Count : 0);
    }
}
