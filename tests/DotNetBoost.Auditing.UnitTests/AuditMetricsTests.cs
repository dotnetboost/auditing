using DotNetBoost.Auditing.Core;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Metadata;
using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.Core.Services;
using DotNetBoost.Auditing.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.Metrics;

namespace DotNetBoost.Auditing.UnitTests;

/// <summary>
/// The three counters. They exist for the question a log cannot answer — "is the trail still being
/// written at all" — so what is asserted here is mostly the tags, because a counter with the wrong
/// tags cannot be grouped by the thing you need to group by.
/// </summary>
public class AuditMetricsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 30, 0, TimeSpan.Zero);

    private static (IAuditManager Audit, RecordingAuditStore Store) Build(
        MetricsProbe probe,
        Action<AuditingOptions>? configure = null,
        AuditModelRegistry? registry = null)
    {
        var options = new AuditingOptions();
        configure?.Invoke(options);

        var store   = new RecordingAuditStore();
        var keys    = new ConventionEntityKeyAccessor();
        var time    = new FixedTimeProvider(Now);
        var model   = registry ?? AuditModelRegistry.Empty;
        var metrics = new AuditMetrics(probe);

        var manager = new AuditManager(
            new AuditRecorder(store, new NullAuditActorAccessor(), keys, model, options, time, new NullAuditObserver(), metrics),
            new AuditReader(store, keys, options),
            new AuditPruner(store, keys, model, options, time, metrics));

        return (manager, store);
    }

    [Fact]
    public void The_meter_is_named_what_an_application_subscribes_to()
    {
        using var probe = new MetricsProbe();
        using var _     = new AuditMetrics(probe);

        // Pinned, because this string is what goes in AddMeter(...) — renaming it silently turns
        // off every dashboard built on it, and nothing else would fail.
        Assert.Equal("DotNetBoost.Auditing", AuditingTelemetry.MeterName);
        Assert.Equal(AuditingTelemetry.MeterName, probe.MeterName);
        Assert.Equal(AuditingTelemetry.MeterVersion, probe.MeterVersion);
    }

    [Fact]
    public async Task A_recorded_entry_is_counted_by_entity_and_action()
    {
        using var probe = new MetricsProbe();
        var (audit, _)  = Build(probe);

        await audit.For(new Product { Id = 1 }).UpdatedAsync([new AuditChange(nameof(Product.Price), 1m, 2m)]);
        await audit.For(new Product { Id = 2 }).CreatedAsync([new AuditChange(nameof(Product.Price), null, 5m)]);

        Assert.Equal(2, probe.Total(AuditingTelemetry.EntriesRecorded, (AuditingTelemetry.TagEntity, "Product")));

        Assert.Equal(1, probe.Total(
            AuditingTelemetry.EntriesRecorded,
            (AuditingTelemetry.TagEntity, "Product"),
            (AuditingTelemetry.TagAction, "Updated")));

        Assert.Equal(1, probe.Total(
            AuditingTelemetry.EntriesRecorded,
            (AuditingTelemetry.TagEntity, "Product"),
            (AuditingTelemetry.TagAction, "Created")));

        Assert.Equal(0, probe.Total(AuditingTelemetry.WritesSuppressed));
    }

    [Fact]
    public async Task A_suppressed_write_is_counted_by_reason()
    {
        using var probe = new MetricsProbe();
        var (audit, _)  = Build(probe, o => o.RequireOptIn = true);

        await audit.For(new Product { Id = 1 }).UpdatedAsync([new AuditChange(nameof(Product.Price), 1m, 2m)]);

        Assert.Equal(1, probe.Total(
            AuditingTelemetry.WritesSuppressed,
            (AuditingTelemetry.TagEntity, "Product"),
            (AuditingTelemetry.TagReason, nameof(AuditSuppression.NotOptedIn))));

        // Nothing recorded, which is the other half of the pair an alert is built on: suppressed
        // rising while recorded stays flat is the signal.
        Assert.Equal(0, probe.Total(AuditingTelemetry.EntriesRecorded));
    }

    [Theory]
    [InlineData(nameof(AuditSuppression.RecordingDisabled))]
    [InlineData(nameof(AuditSuppression.EntityIgnored))]
    public async Task Each_reason_reaches_the_counter_under_its_own_name(string expected)
    {
        using var probe = new MetricsProbe();

        var (audit, _) = expected == nameof(AuditSuppression.RecordingDisabled)
            ? Build(probe, o => o.Enabled = false)
            : Build(probe);

        var entity = expected == nameof(AuditSuppression.RecordingDisabled)
            ? (object)new Product { Id = 1 }
            : new Heartbeat { Id = 1 };

        await audit.For(entity).UpdatedAsync([new AuditChange("Anything", 1, 2)]);

        Assert.Equal(1, probe.Total(AuditingTelemetry.WritesSuppressed, (AuditingTelemetry.TagReason, expected)));
    }

    [Fact]
    public async Task A_prune_counts_what_it_deleted_as_a_prune()
    {
        using var probe     = new MetricsProbe();
        var (audit, store)  = Build(probe, o => o.Retention = TimeSpan.FromDays(30));

        await store.SeedAsync(
            Entry("1", Now.UtcDateTime.AddDays(-90)),
            Entry("2", Now.UtcDateTime.AddDays(-90)),
            Entry("3", Now.UtcDateTime));

        Assert.Equal(2, await audit.PruneAsync(batchSize: 1));

        Assert.Equal(2, probe.Total(
            AuditingTelemetry.EntriesDeleted,
            (AuditingTelemetry.TagOperation, AuditingTelemetry.OperationPrune)));

        // Counted per batch, so a long first sweep shows progress rather than one number at the end.
        Assert.Equal(
            2,
            probe.Measurements.Count(m => m.Instrument == AuditingTelemetry.EntriesDeleted));

        Assert.Equal(0, probe.Total(
            AuditingTelemetry.EntriesDeleted,
            (AuditingTelemetry.TagOperation, AuditingTelemetry.OperationErase)));
    }

    [Fact]
    public async Task An_erasure_counts_what_it_deleted_as_an_erasure()
    {
        using var probe    = new MetricsProbe();
        var (audit, store) = Build(probe);

        await store.SeedAsync(Entry("1", Now.UtcDateTime), Entry("1", Now.UtcDateTime));

        Assert.Equal(2, await audit.EraseForAsync("Product", "1"));

        Assert.Equal(2, probe.Total(
            AuditingTelemetry.EntriesDeleted,
            (AuditingTelemetry.TagOperation, AuditingTelemetry.OperationErase)));
    }

    [Fact]
    public async Task No_measurement_carries_the_entity_key_or_the_actor()
    {
        using var probe    = new MetricsProbe();
        var (audit, store) = Build(probe, o => o.RequireOptIn = true);

        await audit.For(new Product { Id = 4242 }).UpdatedAsync([new AuditChange(nameof(Product.Price), 1m, 2m)]);
        await audit.For(new Invoice { Id = 4243 }).UpdatedAsync([new AuditChange(nameof(Invoice.Total), 1m, 2m)]);

        await store.SeedAsync(Entry("4244", Now.UtcDateTime));
        await audit.EraseForActorAsync("alice@example.com");

        Assert.NotEmpty(probe.Measurements);

        // The cardinality rule, asserted rather than only documented: an entity key or an actor as
        // a tag is a time series per row or per person, which takes a metrics backend down — and
        // the actor is personal data the rest of the library is careful with. Both are right there
        // on every write, so this is the mistake worth a test.
        Assert.All(probe.Measurements, m =>
        {
            Assert.DoesNotContain("4242", m.Tags.Values.Select(v => v?.ToString()));
            Assert.DoesNotContain("4243", m.Tags.Values.Select(v => v?.ToString()));
            Assert.DoesNotContain("4244", m.Tags.Values.Select(v => v?.ToString()));
            Assert.DoesNotContain("alice@example.com", m.Tags.Values.Select(v => v?.ToString()));
        });
    }

    [Fact]
    public void The_engine_resolves_without_a_meter_factory()
    {
        // A console application that only called AddAuditing() has no IMeterFactory. Refusing to
        // start over telemetry nobody subscribed to would be the library's fault, not the host's.
        var provider = new ServiceCollection()
            .AddAuditing()
            .UseCustomStore<RecordingAuditStore>()
            .Build()
            .BuildServiceProvider();

        Assert.Null(provider.GetService<IMeterFactory>());

        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAuditManager>());
    }

    [Fact]
    public void The_host_meter_factory_is_used_when_there_is_one()
    {
        var probe = new MetricsProbe();

        var provider = new ServiceCollection()
            .AddSingleton<IMeterFactory>(probe)
            .AddAuditing()
            .UseCustomStore<RecordingAuditStore>()
            .Build()
            .BuildServiceProvider();

        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAuditManager>());

        // The engine created its meter on the host's factory rather than owning one, which is what
        // lets a host scope and dispose it.
        Assert.Equal(AuditingTelemetry.MeterName, probe.MeterName);
    }

    private static AuditEntry Entry(string entityId, DateTime occurredAt) => new()
    {
        Id            = Guid.NewGuid(),
        EntityName    = "Product",
        EntityId      = entityId,
        Action        = AuditAction.Updated,
        Actor         = "alice@example.com",
        CorrelationId = "c1",
        OccurredAt    = occurredAt
    };
}
