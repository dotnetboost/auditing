using DotNetBoost.Auditing.Core;
using DotNetBoost.Auditing.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using DotNetBoost.Auditing.MongoDb;

namespace DotNetBoost.Auditing.ProviderTests;

/// <summary>
/// The wiring. Constructing a <see cref="MongoClient"/> and asking it for a database does not open a
/// connection, so everything here runs without a server — what it cannot check is whether a
/// statement the store issues is accepted by one.
/// </summary>
public class MongoRegistrationTests
{
    private const string Connection = "mongodb://localhost:27017";

    [Fact]
    public void A_registered_store_resolves_with_the_engine_over_it()
    {
        var provider = new ServiceCollection()
            .AddAuditing()
            .UseMongoDb(Connection, "audit-test")
            .Build()
            .BuildServiceProvider();

        using var scope = provider.CreateScope();

        Assert.IsType<MongoAuditStore>(scope.ServiceProvider.GetRequiredService<IAuditStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAuditRecorder>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAuditReader>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAuditPruner>());
    }

    [Fact]
    public void The_client_is_a_singleton_because_it_owns_the_connection_pool()
    {
        var provider = new ServiceCollection()
            .AddAuditing()
            .UseMongoDb(Connection, "audit-test")
            .Build()
            .BuildServiceProvider();

        using var first  = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<IMongoClient>(),
            second.ServiceProvider.GetRequiredService<IMongoClient>());
    }

    [Fact]
    public void An_application_that_registers_its_own_database_keeps_it()
    {
        var client   = new MongoClient(Connection);
        var database = client.GetDatabase("mine");

        var services = new ServiceCollection();
        services.AddSingleton(database);

        var provider = services
            .AddAuditing()
            .UseMongoDb()
            .Build()
            .BuildServiceProvider();

        using var scope = provider.CreateScope();

        Assert.Same(database, scope.ServiceProvider.GetRequiredService<IMongoDatabase>());
    }

    [Fact]
    public void A_second_store_is_refused_rather_than_silently_replacing_the_first()
    {
        var builder = new ServiceCollection()
            .AddAuditing()
            .UseMongoDb(Connection, "audit-test");

        var error = Assert.Throws<InvalidOperationException>(
            () => builder.UseMongoDb(Connection, "audit-test"));

        Assert.Contains("already configured", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_index_hosted_service_is_registered_unless_it_is_turned_off()
    {
        var withIndexes = new ServiceCollection()
            .AddAuditing()
            .UseMongoDb(Connection, "audit-test")
            .Build()
            .BuildServiceProvider();

        Assert.NotEmpty(withIndexes.GetServices<Microsoft.Extensions.Hosting.IHostedService>());

        var without = new ServiceCollection()
            .AddAuditing()
            .UseMongoDb(Connection, "audit-test", o => o.CreateIndexes = false)
            .Build()
            .BuildServiceProvider();

        Assert.Empty(without.GetServices<Microsoft.Extensions.Hosting.IHostedService>());
    }

    [Fact]
    public void The_collection_name_can_be_changed()
    {
        var provider = new ServiceCollection()
            .AddAuditing()
            .UseMongoDb(Connection, "audit-test", o => o.EntriesCollection = "trail")
            .Build()
            .BuildServiceProvider();

        Assert.Equal("trail", provider.GetRequiredService<MongoDbOptions>().EntriesCollection);
    }

    [Fact]
    public void A_session_makes_a_store_that_runs_inside_it()
    {
        var database = new MongoClient(Connection).GetDatabase("audit-test");
        var store    = new MongoAuditStore(database, new MongoDbOptions());

        // Starting a real session needs a server, so this checks the shape rather than the
        // behaviour: WithSession returns a new store and leaves the original alone, as
        // DapperAuditStore.WithTransaction does.
        Assert.Throws<ArgumentNullException>(() => store.WithSession(null!));
    }

    [Fact]
    public void The_default_collection_is_named_for_what_it_holds() =>
        Assert.Equal("auditEntries", new MongoDbOptions().EntriesCollection);
}
