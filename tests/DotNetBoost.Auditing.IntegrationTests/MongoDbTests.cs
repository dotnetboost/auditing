using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.MongoDb;
using MongoDB.Bson;
using MongoDB.Driver;

namespace DotNetBoost.Auditing.IntegrationTests;

/// <summary>
/// The MongoDB store's own concerns against a real server: the indexes it creates, the session it
/// can run inside, and the document it actually writes.
/// </summary>
[Collection("mongodb")]
public sealed class MongoDbTests(MongoFixture fixture) : IAsyncLifetime
{
    private readonly MongoDbOptions _options = new();
    private IMongoClient _client = null!;
    private IMongoDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _client   = new MongoClient(fixture.ConnectionString);
        _database = _client.GetDatabase("audit_mongo_own");

        await MongoAuditIndexes.EnsureCreatedAsync(_database, _options);
        await Collection().DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private IMongoCollection<BsonDocument> Collection() =>
        _database.GetCollection<BsonDocument>(_options.EntriesCollection);

    private MongoAuditStore Store() => new(_database, _options);

    private static AuditEntry Entry(string entityId = "42") => new()
    {
        Id            = Guid.NewGuid(),
        EntityName    = "Product",
        EntityId      = entityId,
        Action        = AuditAction.Updated,
        Actor         = "alice@example.com",
        CorrelationId = "mongo-1",
        OccurredAt    = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc),
        Changes       = { new AuditPropertyChange { PropertyName = "Price", OldValue = "1", NewValue = "2" } }
    };

    [Fact]
    public async Task Every_index_the_package_promises_exists_on_the_server()
    {
        var indexes = await (await Collection().Indexes.ListAsync()).ToListAsync();
        var names   = indexes.Select(i => i["name"].AsString).ToList();

        // Asserted against the server's own catalogue rather than against the definitions that were
        // handed to it. Before this suite, the unit test compared the definitions with themselves.
        Assert.Contains("entity_occurred", names);
        Assert.Contains("actor_occurred", names);
        Assert.Contains("occurred", names);
        Assert.Contains("correlation", names);
        Assert.Contains("change_property", names);
    }

    [Fact]
    public async Task Creating_the_indexes_twice_is_a_no_op()
    {
        var before = (await (await Collection().Indexes.ListAsync()).ToListAsync()).Count;

        await MongoAuditIndexes.EnsureCreatedAsync(_database, _options);

        Assert.Equal(before, (await (await Collection().Indexes.ListAsync()).ToListAsync()).Count);
    }

    [Fact]
    public async Task The_multikey_index_actually_serves_a_per_property_query()
    {
        await Store().AppendAsync(Entry());

        var plan = await Collection()
            .Find(Builders<BsonDocument>.Filter.Eq("Changes.PropertyName", "Price"))
            .Project<BsonDocument>(Builders<BsonDocument>.Projection.Include("_id"))
            .ToListAsync();

        // The whole justification for embedding instead of a second collection: "every change
        // anyone ever made to Price" is answerable. The index behind it was asserted above; this is
        // the query it exists for, run for real.
        Assert.Single(plan);
    }

    [Fact]
    public async Task The_stored_document_is_the_shape_the_unit_tests_predicted()
    {
        var written = Entry();
        await Store().AppendAsync(written);

        var document = await Collection().Find(FilterDefinition<BsonDocument>.Empty).FirstAsync();

        Assert.Equal(BsonBinarySubType.UuidStandard, document["_id"].AsBsonBinaryData.SubType);
        Assert.Equal(written.Id, document["_id"].AsBsonBinaryData.ToGuid());
        Assert.Equal("Updated", document["Action"].AsString);
        Assert.Single(document["Changes"].AsBsonArray);

        // Serializing to BSON in a unit test and having a server accept it are different claims.
        // This is the second one.
        Assert.False(document.Contains("Id"));
    }

    [Fact]
    public async Task An_absent_actor_is_absent_on_the_server_and_still_findable()
    {
        var anonymous = Entry("no-actor");
        anonymous.Actor = null;

        await Store().AppendAsync(anonymous);
        await Store().AppendAsync(Entry("has-actor"));

        var document = await Collection()
            .Find(Builders<BsonDocument>.Filter.Eq("EntityId", "no-actor"))
            .FirstAsync();

        Assert.False(document.Contains("Actor"));

        // And the query for exactly those still finds it, which is why the actor index is not
        // sparse.
        var missing = await Store().QueryAsync(new AuditQuery { ActorMissing = true, Take = 10 });
        Assert.Equal("no-actor", Assert.Single(missing.Entries).EntityId);
    }

    [Fact]
    public async Task A_session_commits_the_entry_with_the_rest_of_the_work()
    {
        using var session = await _client.StartSessionAsync();
        session.StartTransaction();

        await Store().WithSession(session).AppendAsync(Entry("committed"));
        await session.CommitTransactionAsync();

        Assert.Single((await Store().QueryAsync(new AuditQuery { Take = 10 })).Entries);
    }

    [Fact]
    public async Task Aborting_the_session_takes_the_entry_with_it()
    {
        using var session = await _client.StartSessionAsync();
        session.StartTransaction();

        await Store().WithSession(session).AppendAsync(Entry("aborted"));
        await session.AbortTransactionAsync();

        // The store's only route to atomicity with an application's own write, and it needs a
        // replica set — which is why the fixture starts one rather than a standalone server. On a
        // standalone mongod this test could not exist.
        Assert.Empty((await Store().QueryAsync(new AuditQuery { Take = 10 })).Entries);
    }

    [Fact]
    public async Task An_entry_is_written_whole_or_not_at_all_even_without_a_session()
    {
        await Store().AppendAsync(Entry());

        var document = await Collection().Find(FilterDefinition<BsonDocument>.Empty).FirstAsync();

        // What embedding buys over two collections: one insert, so there is no window in which the
        // trail holds an entry whose property rows have not arrived.
        Assert.Single(document["Changes"].AsBsonArray);
    }
}
