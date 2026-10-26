using DotNetBoost.Auditing.Core;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Metadata;
using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.Core.Services;
using DotNetBoost.Auditing.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.Auditing.UnitTests;

public class RegistrationTests
{
    [Fact]
    public void Build_without_a_store_says_which_packages_provide_one()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddAuditing().Build());

        Assert.Contains("DotNetBoost.Auditing.EntityFrameworkCore", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_store_is_refused_rather_than_silently_replacing_the_first()
    {
        var builder = new ServiceCollection().AddAuditing().UseCustomStore<RecordingAuditStore>();

        var error = Assert.Throws<InvalidOperationException>(() => builder.UseCustomStore<RecordingAuditStore>());
        Assert.Contains("already configured", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_registered_engine_resolves_every_verb()
    {
        var provider = new ServiceCollection()
            .AddAuditing()
            .UseCustomStore<RecordingAuditStore>()
            .Build()
            .BuildServiceProvider();

        using var scope = provider.CreateScope();

        // One interface now, so this is one assertion rather than three — and the engines
        // behind it must resolve too, or the manager could not have been constructed.
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAuditManager>());
    }

    [Fact]
    public void The_default_actor_accessor_captures_nothing()
    {
        var provider = new ServiceCollection()
            .AddAuditing()
            .UseCustomStore<RecordingAuditStore>()
            .Build()
            .BuildServiceProvider();

        Assert.Null(provider.GetRequiredService<IAuditActorAccessor>().GetActor());
    }

    [Fact]
    public void The_default_observer_is_the_one_that_does_nothing()
    {
        var provider = new ServiceCollection()
            .AddAuditing()
            .UseCustomStore<RecordingAuditStore>()
            .Build()
            .BuildServiceProvider();

        // Registered rather than absent, so the engine has one call site per decision instead of a
        // null check at each — and so turning observation on is a registration, not a code change.
        Assert.IsType<NullAuditObserver>(provider.GetRequiredService<IAuditObserver>());
    }

    [Fact]
    public async Task A_registered_observer_is_told_what_was_not_recorded()
    {
        var observer = new RecordingAuditObserver();

        var provider = new ServiceCollection()
            .AddAuditing(o => o.Enabled = false)
            .UseCustomStore<RecordingAuditStore>()
            .UseObserver<RecordingAuditObserver>()
            .Build()
            .BuildServiceProvider();

        // Singleton, so the instance the engine was given is the one a caller can read afterwards.
        observer = (RecordingAuditObserver)provider.GetRequiredService<IAuditObserver>();

        using var scope = provider.CreateScope();

        Assert.Null(await scope.ServiceProvider.GetRequiredService<IAuditManager>()
            .For(new Product { Id = 1 })
            .UpdatedAsync([new AuditChange(nameof(Product.Price), 1m, 2m)]));

        Assert.Equal(AuditSuppression.RecordingDisabled, Assert.Single(observer.Suppressed).Reason);
    }

    [Fact]
    public async Task An_end_to_end_registration_records_through_the_resolved_services()
    {
        var store = new RecordingAuditStore();

        var provider = new ServiceCollection()
            .AddAuditing()
            .UseCustomStore(_ => store)
            .DeclareEntity<PersonEntity>()
            .Build()
            .BuildServiceProvider();

        using var scope = provider.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IAuditManager>();

        await recorder.For("Person", "42").UpdatedAsync(
        [
            new AuditChange(nameof(PersonEntity.PasswordHash), "old", "new")
        ]);

        // Declared at Build(), so the redaction rule reached a write addressed by name.
        Assert.True(Assert.Single(Assert.Single(store.Entries).Changes).IsRedacted);
    }

    [Fact]
    public void Declaring_an_assembly_finds_the_entities_that_carry_the_librarys_attributes()
    {
        var services = new ServiceCollection()
            .AddAuditing()
            .UseCustomStore<RecordingAuditStore>()
            .DeclareEntitiesFrom(typeof(PersonEntity).Assembly);

        // This assembly deliberately contains two bad declarations — [AuditRetention(Days = 0)]
        // and two types claiming the name "Person". A scan must surface them at startup rather
        // than quietly skip whichever it reaches second.
        Assert.Throws<InvalidOperationException>(() => services.Build());
    }

    [Fact]
    public void Two_entities_under_one_stored_name_are_refused_at_startup()
    {
        var services = new ServiceCollection()
            .AddAuditing()
            .UseCustomStore<RecordingAuditStore>()
            .DeclareEntities(typeof(PersonEntity), typeof(Duplicate.PersonEntity));

        var error = Assert.Throws<InvalidOperationException>(() => services.Build());
        Assert.Contains("'Person'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Declaring_the_same_type_twice_is_harmless()
    {
        new ServiceCollection()
            .AddAuditing()
            .UseCustomStore<RecordingAuditStore>()
            .DeclareEntities(typeof(PersonEntity), typeof(PersonEntity))
            .Build();
    }

    // ── IgnoreEntity ───────────────────────────────────────────────────────

    /// <summary>
    /// A scope over an engine built with <paramref name="configure"/>, and the observer it reports
    /// to — the only place an ignored entity leaves a trace.
    /// </summary>
    private static (IServiceScope Scope, IAuditManager Audit, RecordingAuditStore Store, RecordingAuditObserver Observer)
        Ignoring(Func<AuditingBuilder, AuditingBuilder> configure, Action<AuditingOptions>? options = null)
    {
        var store = new RecordingAuditStore();

        var provider = configure(new ServiceCollection()
                .AddAuditing(o => options?.Invoke(o))
                .UseCustomStore(_ => store)
                .UseObserver<RecordingAuditObserver>())
            .Build()
            .BuildServiceProvider();

        var scope = provider.CreateScope();

        return (
            scope,
            scope.ServiceProvider.GetRequiredService<IAuditManager>(),
            store,
            (RecordingAuditObserver)provider.GetRequiredService<IAuditObserver>());
    }

    [Fact]
    public async Task An_ignored_entity_is_suppressed_as_ignored_whichever_way_it_is_addressed()
    {
        var (scope, audit, store, observer) = Ignoring(b => b.IgnoreEntity<Product>());
        using var lifetime = scope;

        Assert.False(audit.IsAudited(typeof(Product)));

        Assert.Null(await audit.For(new Product { Id = 1 })
            .UpdatedAsync([new AuditChange(nameof(Product.Price), 1m, 2m)]));

        // By name too: the ignore names the type, so the engine knows the name as surely as it
        // would for a declared [AuditIgnore] class — and the two must not differ.
        Assert.Null(await audit.For("Product", "1").DeletedAsync());

        Assert.Empty(store.Entries);
        Assert.Equal(
            [(AuditSuppression.EntityIgnored, "Product", "1", AuditAction.Updated),
             (AuditSuppression.EntityIgnored, "Product", "1", AuditAction.Deleted)],
            observer.Suppressed);
    }

    [Fact]
    public async Task IgnoreEntity_matches_a_declared_class_level_AuditIgnore()
    {
        var (attributeScope, byAttribute, _, attributeObserver) = Ignoring(b => b.DeclareEntity<Heartbeat>());
        var (configScope, byConfig, _, configObserver) = Ignoring(b => b.IgnoreEntity<Product>());
        using var a = attributeScope;
        using var c = configScope;

        await byAttribute.For(new Heartbeat { Id = 1 }).CreatedAsync();
        await byAttribute.For("Heartbeat", "1").DeletedAsync();
        await byConfig.For(new Product { Id = 1 }).CreatedAsync();
        await byConfig.For("Product", "1").DeletedAsync();

        Assert.Equal(attributeObserver.Reasons, configObserver.Reasons);
        Assert.Equal(byAttribute.IsAudited(typeof(Heartbeat)), byConfig.IsAudited(typeof(Product)));
    }

    [Fact]
    public async Task IgnoreEntity_wins_over_Audited_and_over_RequireOptIn()
    {
        var (scope, audit, store, observer) = Ignoring(
            b => b.IgnoreEntities(typeof(Invoice)),
            o => o.RequireOptIn = true);
        using var lifetime = scope;

        Assert.False(audit.IsAudited(typeof(Invoice)));
        Assert.Null(await audit.For(new Invoice { Id = 1 })
            .UpdatedAsync([new AuditChange(nameof(Invoice.Total), 1m, 2m)]));

        Assert.Empty(store.Entries);

        // Ignored, not NotOptedIn: the entity is [Audited], so "not opted in" would be false.
        Assert.Equal(AuditSuppression.EntityIgnored, Assert.Single(observer.Suppressed).Reason);
    }

    [Fact]
    public async Task Ignoring_one_entity_leaves_the_others_recorded()
    {
        var (scope, audit, store, _) = Ignoring(b => b.IgnoreEntity<Product>());
        using var lifetime = scope;

        Assert.NotNull(await audit.For(new Invoice { Id = 1 })
            .UpdatedAsync([new AuditChange(nameof(Invoice.Total), 1m, 2m)]));

        Assert.Equal("Invoice", Assert.Single(store.Entries).EntityName);
    }

    [Fact]
    public void Ignoring_the_same_type_twice_is_harmless()
    {
        new ServiceCollection()
            .AddAuditing()
            .UseCustomStore<RecordingAuditStore>()
            .IgnoreEntity<Product>()
            .IgnoreEntities(typeof(Product), typeof(Product))
            .Build();
    }

    [Fact]
    public void An_entity_both_declared_and_ignored_is_refused_at_build()
    {
        var declaredFirst = new ServiceCollection()
            .AddAuditing()
            .UseCustomStore<RecordingAuditStore>()
            .DeclareEntity<Invoice>()
            .IgnoreEntity<Invoice>();

        var ignoredFirst = new ServiceCollection()
            .AddAuditing()
            .UseCustomStore<RecordingAuditStore>()
            .IgnoreEntities(typeof(Invoice))
            .DeclareEntities(typeof(Invoice));

        foreach (var builder in new[] { declaredFirst, ignoredFirst })
        {
            var error = Assert.Throws<InvalidOperationException>(() => builder.Build());
            Assert.Contains(typeof(Invoice).FullName!, error.Message, StringComparison.Ordinal);
            Assert.Contains("IgnoreEntity", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_ignored_entity_sharing_a_declared_entitys_stored_name_is_refused_at_build()
    {
        // Two types, one name: a write addressed as For("Person", id) would be both recorded under
        // one type's rules and suppressed under the other's ignore.
        var builder = new ServiceCollection()
            .AddAuditing()
            .UseCustomStore<RecordingAuditStore>()
            .DeclareEntity<PersonEntity>()
            .IgnoreEntity<Duplicate.PersonEntity>();

        var error = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("'Person'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IgnoreEntity_validates_its_arguments()
    {
        var builder = new ServiceCollection().AddAuditing();

        Assert.Throws<ArgumentNullException>(() => AuditingBuilderExtensions.IgnoreEntity<Product>(null!));
        Assert.Throws<ArgumentNullException>(() => AuditingBuilderExtensions.IgnoreEntities(null!, typeof(Product)));
        Assert.Throws<ArgumentNullException>(() => builder.IgnoreEntities(null!));
        Assert.Throws<ArgumentNullException>(() => builder.IgnoreEntities(typeof(Product), null!));
    }

    [Fact]
    public void A_default_page_size_above_the_maximum_is_refused_at_build()
    {
        var services = new ServiceCollection()
            .AddAuditing(o =>
            {
                o.MaxPageSize     = 10;
                o.DefaultPageSize = 50;
            })
            .UseCustomStore<RecordingAuditStore>();

        var error = Assert.Throws<InvalidOperationException>(() => services.Build());
        Assert.Contains("MaxPageSize", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithPageSize_accepts_a_pair_that_would_be_illegal_applied_in_the_other_order()
    {
        var builder = new ServiceCollection()
            .AddAuditing(o =>
            {
                o.MaxPageSize     = 10;
                o.DefaultPageSize = 10;
            })
            .UseCustomStore<RecordingAuditStore>()
            .WithPageSize(defaultSize: 500, maxSize: 1000);

        builder.Build();
    }

    [Fact]
    public void Bad_option_values_are_refused_as_they_are_set()
    {
        var options = new AuditingOptions();

        Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxValueLength = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.DefaultPageSize = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxPageSize = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Retention = TimeSpan.Zero);
    }

    [Fact]
    public void A_value_limit_past_the_sanity_ceiling_is_refused_at_build()
    {
        var services = new ServiceCollection()
            .AddAuditing(o => o.MaxValueLength = AuditingOptions.MaxValueCeiling + 1)
            .UseCustomStore<RecordingAuditStore>();

        Assert.Throws<InvalidOperationException>(() => services.Build());
    }

    [Fact]
    public void An_empty_registry_has_no_retentions_and_no_plans()
    {
        Assert.True(AuditModelRegistry.Empty.IsEmpty);
        Assert.Empty(AuditModelRegistry.Empty.Retentions);
        Assert.Null(AuditModelRegistry.Empty.PlanFor("Product"));
    }
}
