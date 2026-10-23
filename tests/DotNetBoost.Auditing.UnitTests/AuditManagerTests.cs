using DotNetBoost.Auditing.Core;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.Auditing.UnitTests;

/// <summary>
/// The merged surface: one injected service covering recording, reading and retention. What these
/// assert is that the consolidation did not cost any of the three their behaviour.
/// </summary>
public class AuditManagerTests
{
    private static (IAuditManager Audit, RecordingAuditStore Store) Build(Action<AuditingOptions>? configure = null)
    {
        var store = new RecordingAuditStore();

        var provider = new ServiceCollection()
            .AddAuditing(o => configure?.Invoke(o))
            .UseCustomStore(_ => store)
            .Build()
            .BuildServiceProvider();

        return (provider.CreateScope().ServiceProvider.GetRequiredService<IAuditManager>(), store);
    }

    [Fact]
    public async Task One_service_records_reads_and_prunes()
    {
        var (audit, _) = Build(o => o.Retention = TimeSpan.FromDays(365));

        var product = new Product { Id = 42 };
        await audit.For(product).UpdatedAsync([new AuditChange(nameof(Product.Price), 24.99m, 19.99m)]);

        var page = await audit.HistoryAsync(product);
        Assert.Equal("Product", Assert.Single(page.Entries).EntityName);

        // Nothing is old enough to go, which is the point: the verb is reachable and still refuses
        // to delete anything it was not asked to.
        Assert.Equal(0, await audit.PruneAsync());
        Assert.Single((await audit.HistoryAsync(product)).Entries);
    }

    [Fact]
    public async Task The_model_rules_survive_the_merge()
    {
        var (audit, _) = Build();

        var entry = await audit.For(new PersonEntity { Id = Guid.NewGuid() }).UpdatedAsync(
        [
            new AuditChange(nameof(PersonEntity.PasswordHash), "old-hash", "new-hash")
        ]);

        // The failure this guards against is the loud one: a redacted value written down in full
        // because the subject stopped reaching the recording engine.
        var change = Assert.Single(entry!.Changes);
        Assert.True(change.IsRedacted);
        Assert.Null(change.OldValue);
        Assert.Null(change.NewValue);
    }

    [Fact]
    public async Task Reads_through_the_manager_are_still_clamped()
    {
        var (audit, store) = Build(o => o.MaxPageSize = 100);

        await audit.QueryAsync(new AuditQuery { Take = 10_000 });

        // The clamp lives in one place and the merge must not have routed around it.
        Assert.Equal(100, store.LastQuery!.Take);
    }

    [Fact]
    public void A_subject_is_bound_to_the_manager_that_made_it()
    {
        var (audit, _) = Build();
        var (other, _) = Build();

        var product = new Product { Id = 42 };

        Assert.Equal(audit.For(product), audit.For(product));

        // Equality includes the manager, so two subjects on the same address through different
        // managers are not the same subject.
        Assert.NotEqual(audit.For(product), other.For(product));
    }

    [Fact]
    public async Task A_default_subject_says_what_to_do_instead_of_throwing_a_null_reference()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => default(AuditSubject).RecordAsync(AuditAction.Updated));

        Assert.Contains("IAuditManager.For", error.Message, StringComparison.Ordinal);
    }
}
