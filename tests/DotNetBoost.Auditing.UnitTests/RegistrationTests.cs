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
