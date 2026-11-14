using System.Globalization;
using System.Transactions;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotNetBoost.Auditing.IntegrationTests;

/// <summary>
/// The capture sharing a context with another library's <c>SaveChanges</c> interceptor, on real
/// transactional engines. The same cases as <c>ProviderTests/EfCoreCompositionTests</c>, where
/// SQLite's transactions are a file lock; here they are MVCC (PostgreSQL) and locking with real
/// isolation (SQL Server), which is where "rolled back with it" is worth proving. Each case runs
/// through <c>SaveChanges</c> and <c>SaveChangesAsync</c>. The standalone fixes from
/// <c>ProviderTests/EfCoreSaveLifecycleTests</c> run here too: cancellation and concurrency
/// conflicts are reported by each engine's driver in its own way.
/// </summary>
public abstract class CompositionSuite : IAsyncLifetime
{
    private const string OtherRow = "written by the other library";

    private readonly List<ServiceProvider> _providers = [];
    private string _connectionString = null!;

    /// <summary>A connection string to this suite's database.</summary>
    protected abstract Task<string> DatabaseAsync();

    /// <summary>
    /// Points the options at the engine, with <see cref="TestRetryingStrategy"/> when
    /// <paramref name="retrying"/>: it retries the test's own transient failure, which the engines'
    /// built-in strategies would not.
    /// </summary>
    protected abstract void UseEngine(DbContextOptionsBuilder options, string connectionString, bool retrying);

    public async Task InitializeAsync()
    {
        _connectionString = await DatabaseAsync();

        var (provider, _) = Build(null, auditingFirst: true);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        await db.Database.EnsureCreatedAsync();
        await db.Set<AuditPropertyChange>().ExecuteDeleteAsync();
        await db.Set<AuditEntry>().ExecuteDeleteAsync();
        await db.Products.ExecuteDeleteAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var provider in _providers)
            await provider.DisposeAsync();
    }

    private (ServiceProvider Provider, RecordingAuditObserver Observer) Build(
        IInterceptor? other,
        bool auditingFirst,
        bool retrying = false,
        LostAtomicity onLost = LostAtomicity.Report)
    {
        var observer = new RecordingAuditObserver();
        var services = new ServiceCollection();

        services.AddDbContext<AuditDbContext>((sp, o) =>
        {
            UseEngine(o, _connectionString, retrying);

            if (other is not null && !auditingFirst)
                o.AddInterceptors(other);

            o.UseAuditing(sp);

            if (other is not null && auditingFirst)
                o.AddInterceptors(other);
        });

        services.AddAuditing()
            .UseEntityFrameworkCore<AuditDbContext>()
            .DeclareEntity<Product>()
            .OnLostAtomicity(onLost)
            .Build();

        services.Replace(ServiceDescriptor.Singleton<IAuditObserver>(observer));

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return (provider, observer);
    }

    private static async Task InScopeAsync(ServiceProvider provider, Func<AuditDbContext, Task> work)
    {
        using var scope = provider.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<AuditDbContext>());
    }

    private async Task<List<Product>> ProductsAsync()
    {
        var (provider, _) = Build(null, auditingFirst: true);
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Products.AsNoTracking().ToListAsync();
    }

    private async Task<List<AuditEntry>> EntriesAsync()
    {
        var (provider, _) = Build(null, auditingFirst: true);
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Set<AuditEntry>().AsNoTracking().ToListAsync();
    }

    private static string Id(Product product) => product.Id.ToString(CultureInfo.InvariantCulture);

    private static PostSaveWriterInterceptor Writer(WriterCommit commit) =>
        new(() => new Product { Name = OtherRow }) { Commit = commit };

    private async Task AssertEachCreationRecordedOnceAsync()
    {
        var products = await ProductsAsync();
        var created = (await EntriesAsync()).Where(e => e.Action == AuditAction.Created).Select(e => e.EntityId).Order();
        Assert.Equal(products.Select(Id).Order(), created);
    }

    [Theory]
    [InlineData(true, WriterCommit.InSavedChanges, false)]
    [InlineData(true, WriterCommit.InSavedChanges, true)]
    [InlineData(true, WriterCommit.OnSavedChangesEvent, false)]
    [InlineData(true, WriterCommit.OnSavedChangesEvent, true)]
    [InlineData(false, WriterCommit.OnSavedChangesEvent, false)]
    [InlineData(false, WriterCommit.OnSavedChangesEvent, true)]
    public async Task Both_creations_are_recorded_once_with_their_real_ids(bool auditingFirst, WriterCommit commit, bool sync)
    {
        var (provider, observer) = Build(Writer(commit), auditingFirst);

        await InScopeAsync(provider, async db =>
        {
            db.Products.Add(new Product { Name = "Widget" });
            await db.SaveAsync(sync);
            Assert.Null(db.Database.CurrentTransaction);
        });

        Assert.Equal(2, (await ProductsAsync()).Count);
        await AssertEachCreationRecordedOnceAsync();
        Assert.Empty(observer.LostAtomicity);
    }

    [Theory]
    [InlineData(true, WriterCommit.InSavedChanges, false)]
    [InlineData(true, WriterCommit.InSavedChanges, true)]
    [InlineData(true, WriterCommit.OnSavedChangesEvent, false)]
    [InlineData(true, WriterCommit.OnSavedChangesEvent, true)]
    [InlineData(false, WriterCommit.InSavedChanges, false)]
    [InlineData(false, WriterCommit.InSavedChanges, true)]
    [InlineData(false, WriterCommit.OnSavedChangesEvent, false)]
    [InlineData(false, WriterCommit.OnSavedChangesEvent, true)]
    public async Task A_failed_write_in_the_other_library_rolls_back_everything(bool auditingFirst, WriterCommit commit, bool sync)
    {
        var writer = Writer(commit);
        writer.FailWrite = true;
        var (provider, _) = Build(writer, auditingFirst);

        await Assert.ThrowsAsync<InvalidOperationException>(() => InScopeAsync(provider, async db =>
        {
            db.Products.Add(new Product { Name = "Widget" });
            await db.SaveAsync(sync);
        }));

        Assert.Empty(await ProductsAsync());
        Assert.Empty(await EntriesAsync());
    }

    [Theory]

    [InlineData(false)]

    [InlineData(true)]
    public async Task A_throw_from_a_later_SavedChanges_rolls_back_everything(bool sync)
    {
        var (provider, _) = Build(new ThrowAfterSaveInterceptor(), auditingFirst: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => InScopeAsync(provider, async db =>
        {
            db.Products.Add(new Product { Name = "Widget" });
            await db.SaveAsync(sync);
        }));

        Assert.Empty(await ProductsAsync());
        Assert.Empty(await EntriesAsync());
    }

    [Theory]

    [InlineData(false)]

    [InlineData(true)]
    public async Task An_early_commit_by_the_other_library_fails_the_save_under_Throw(bool sync)
    {
        var (provider, _) = Build(Writer(WriterCommit.InSavedChanges), auditingFirst: false, onLost: LostAtomicity.Throw);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => InScopeAsync(provider, async db =>
        {
            db.Products.Add(new Product { Name = "Widget" });
            await db.SaveAsync(sync);
        }));

        Assert.Contains("committed before", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, 0, false)]
    [InlineData(true, 0, true)]
    [InlineData(false, 0, false)]
    [InlineData(false, 0, true)]
    [InlineData(true, 1, false)]
    [InlineData(true, 1, true)]
    [InlineData(false, 1, false)]
    [InlineData(false, 1, true)]
    public async Task A_replayed_save_records_each_change_once(bool auditingFirst, int failedAttempts, bool sync)
    {
        var replay = new ReplayingInterceptor();
        var (provider, _) = Build(replay, auditingFirst, retrying: true);

        await InScopeAsync(provider, async db =>
        {
            db.Products.Add(new Product { Name = "Widget", Price = 1m });
            await db.SaveChangesAsync();
        });

        replay.FailAttempts = failedAttempts;
        await InScopeAsync(provider, async db =>
        {
            (await db.Products.SingleAsync()).Price = 2m;
            db.Products.Add(new Product { Name = "Gadget" });
            await db.SaveAsync(sync);
            Assert.Null(db.Database.CurrentTransaction);
        });

        Assert.Single(await EntriesAsync(), e => e.Action == AuditAction.Updated);
        await AssertEachCreationRecordedOnceAsync();
    }

    [Theory]

    [InlineData(false)]

    [InlineData(true)]
    public async Task A_creation_inside_a_completed_TransactionScope_is_recorded(bool sync)
    {
        var (provider, _) = Build(null, auditingFirst: true);

        await InScopeAsync(provider, async db =>
        {
            using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);

            // A store-generated key, so the creation is deferred. The interceptor must not open a
            // transaction of its own inside the ambient one: EF refuses that.
            db.Products.Add(new Product { Name = "Widget", Price = 1m });
            await db.SaveAsync(sync);

            scope.Complete();
        });

        Assert.Single(await ProductsAsync());
        Assert.Single(await EntriesAsync(), e => e.Action == AuditAction.Created);
    }

    [Theory]

    [InlineData(false)]

    [InlineData(true)]
    public async Task Abandoning_a_TransactionScope_takes_the_trail_with_it(bool sync)
    {
        var (provider, _) = Build(null, auditingFirst: true);

        await InScopeAsync(provider, async db =>
        {
            using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);

            db.Products.Add(new Product { Name = "Widget", Price = 1m });
            await db.SaveAsync(sync);

            // Disposed without Complete(): the scope rolls back, and the deferred entry with it.
        });

        Assert.Empty(await ProductsAsync());
        Assert.Empty(await EntriesAsync());
    }

    // ---- The standalone fixes, on a real engine --------------------------------------------

    [Fact]
    public async Task A_cancelled_save_leaves_no_transaction_open_and_a_retry_records_once()
    {
        var cancel = new CancellingInterceptor();
        var (provider, _) = Build(cancel, auditingFirst: true);

        await InScopeAsync(provider, async db =>
        {
            db.Products.Add(new Product { Name = "Widget" });

            using var request = new CancellationTokenSource();
            cancel.Cancel = request;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.SaveChangesAsync(request.Token));
            Assert.Null(db.Database.CurrentTransaction);

            cancel.Cancel = null;
            await db.SaveChangesAsync();
        });

        await AssertEachCreationRecordedOnceAsync();
        Assert.Single(await ProductsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_save_refused_by_a_later_SavingChanges_hook_is_cleaned_up_by_the_next_save(bool sync)
    {
        var refuse = new RefusingInterceptor();
        var (provider, _) = Build(refuse, auditingFirst: true);

        await InScopeAsync(provider, async db =>
        {
            db.Products.Add(new Product { Name = "Widget" });

            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveAsync(sync));

            refuse.Active = false;
            await db.SaveAsync(sync);
            Assert.Null(db.Database.CurrentTransaction);
        });

        await AssertEachCreationRecordedOnceAsync();
        Assert.Single(await ProductsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_concurrency_conflict_leaves_no_transaction_open(bool sync)
    {
        var (provider, _) = Build(null, auditingFirst: true);

        await InScopeAsync(provider, async db =>
        {
            db.Products.Add(new Product { Name = "Gone", Price = 1m });
            await db.SaveChangesAsync();
        });

        await InScopeAsync(provider, async db =>
        {
            var gone = await db.Products.SingleAsync();

            // Deleted behind this context's back, so updating it affects no row.
            await InScopeAsync(provider, async other =>
                await other.Products.Where(p => p.Id == gone.Id).ExecuteDeleteAsync());

            gone.Price = 2m;
            db.Products.Add(new Product { Name = "Widget" });

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveAsync(sync));
            Assert.Null(db.Database.CurrentTransaction);
        });

        // Neither the conflicting save's creation nor its entry.
        Assert.Empty(await ProductsAsync());
        Assert.DoesNotContain(await EntriesAsync(), e => e.Action == AuditAction.Updated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_suppressed_save_is_not_recorded_and_opens_no_transaction(bool sync)
    {
        var suppress = new SuppressingInterceptor();
        var (provider, _) = Build(suppress, auditingFirst: false);

        await InScopeAsync(provider, async db =>
        {
            db.Products.Add(new Product { Name = "Widget" });

            suppress.Active = true;
            Assert.Equal(0, await db.SaveAsync(sync));
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Empty(db.ChangeTracker.Entries<AuditEntry>());
        });

        Assert.Empty(await ProductsAsync());
        Assert.Empty(await EntriesAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_second_context_in_the_scope_writes_its_own_entries(bool sync)
    {
        var (provider, _) = Build(null, auditingFirst: true);

        using (var scope = provider.CreateScope())
        {
            var registered = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            await using var second = new AuditDbContext(
                scope.ServiceProvider.GetRequiredService<DbContextOptions<AuditDbContext>>());

            second.Products.Add(new Product { Name = "Widget", Price = 1m });
            await second.SaveAsync(sync);

            (await second.Products.SingleAsync()).Price = 2m;
            await second.SaveAsync(sync);

            Assert.Empty(registered.ChangeTracker.Entries<AuditEntry>());
        }

        await AssertEachCreationRecordedOnceAsync();
        Assert.Single(await EntriesAsync(), e => e.Action == AuditAction.Updated);
    }
}

[Collection("postgres")]
public sealed class PostgresCompositionTests(PostgresFixture fixture) : CompositionSuite
{
    protected override Task<string> DatabaseAsync() => fixture.DatabaseAsync("audit_composition");

    protected override void UseEngine(DbContextOptionsBuilder options, string connectionString, bool retrying) =>
        options.UseNpgsql(connectionString, b =>
        {
            if (retrying)
                b.ExecutionStrategy(d => new TestRetryingStrategy(d));
        });
}

[Collection("sqlserver")]
public sealed class SqlServerCompositionTests(SqlServerFixture fixture) : CompositionSuite
{
    protected override Task<string> DatabaseAsync() => fixture.DatabaseAsync("audit_composition");

    protected override void UseEngine(DbContextOptionsBuilder options, string connectionString, bool retrying) =>
        options.UseSqlServer(connectionString, b =>
        {
            if (retrying)
                b.ExecutionStrategy(d => new TestRetryingStrategy(d));
        });
}
