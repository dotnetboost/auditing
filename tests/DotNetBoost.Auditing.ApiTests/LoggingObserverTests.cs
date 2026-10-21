using DotNetBoost.Auditing.API;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using DotNetBoost.Auditing.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.Auditing.ApiTests;

/// <summary>
/// What the logging observer writes, and at which level — which is the whole of its design: the
/// suppressions that are configured intent have to stay out of an operator's way, and the two that
/// are nearly always a misconfiguration have to be impossible to miss.
/// </summary>
public class LoggingObserverTests
{
    private static (LoggingAuditObserver Observer, CapturingLogger Log) Build()
    {
        var log = new CapturingLogger();
        return (new LoggingAuditObserver(new TypedLogger<LoggingAuditObserver>(log)), log);
    }

    [Theory]
    [InlineData(AuditSuppression.RecordingDisabled)]
    [InlineData(AuditSuppression.EntityIgnored)]
    [InlineData(AuditSuppression.NotOptedIn)]
    [InlineData(AuditSuppression.NothingChanged)]
    public void A_suppression_that_is_configured_intent_is_debug(AuditSuppression reason)
    {
        var (observer, log) = Build();

        observer.WriteSuppressed(reason, "Product", "42", AuditAction.Updated);

        // Debug, because a high-churn ignored entity reports one of these on every save. At any
        // higher level this would be the noisiest thing in the application and the first thing an
        // operator filtered out — which would take the two that matter with it.
        Assert.Equal(LogLevel.Debug, Assert.Single(log.Lines).Level);
    }

    [Fact]
    public void An_undeclared_entity_under_opt_in_is_a_warning_that_names_the_fix()
    {
        var (observer, log) = Build();

        observer.WriteSuppressed(AuditSuppression.EntityNotDeclared, "Product", "42", AuditAction.Updated);

        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("DeclareEntitiesFrom", line.Message, StringComparison.Ordinal);
        Assert.Contains("Product", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_write_recorded_without_model_rules_is_a_warning_that_says_what_it_cost()
    {
        var (observer, log) = Build();

        observer.RecordedWithoutModelRules("Person", "42", AuditAction.Updated);

        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);

        // The line has to say what has already happened, not merely what is misconfigured: this is
        // the one failure in the library that cannot be undone.
        Assert.Contains("AuditRedact", line.Message, StringComparison.Ordinal);
        Assert.Contains("in full", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reason_this_package_has_not_been_taught_is_logged_rather_than_dropped()
    {
        var (observer, log) = Build();

        observer.WriteSuppressed((AuditSuppression)999, "Product", "42", AuditAction.Deleted);

        // A new suppression arriving as silence is the failure this observer exists to prevent, so
        // an unknown one gets more attention than a known one, not less.
        Assert.Equal(LogLevel.Warning, Assert.Single(log.Lines).Level);
    }

    [Fact]
    public void Every_line_names_the_entity_and_the_key_and_no_recorded_value()
    {
        var (observer, log) = Build();

        foreach (var reason in Enum.GetValues<AuditSuppression>())
            observer.WriteSuppressed(reason, "Product", "42", AuditAction.Updated);

        Assert.Equal(Enum.GetValues<AuditSuppression>().Length, log.Lines.Count);

        Assert.All(log.Lines, line =>
        {
            Assert.Contains("Product", line.Message, StringComparison.Ordinal);
            Assert.Contains("42", line.Message, StringComparison.Ordinal);
        });
    }

    /// <summary>Keeps every line, so a test can assert on the level as well as the text.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, EventId Id, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Lines.Add((logLevel, eventId, formatter(state, exception)));
        }
    }

    /// <summary>Wraps a logger as the <c>ILogger&lt;T&gt;</c> the observer's constructor asks for.</summary>
    private sealed class TypedLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            inner.BeginScope(state) ?? NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
                // Nothing to release.
            }
        }
    }
}

/// <summary>That the builder method reaches the observer at all.</summary>
public class LoggingObserverRegistrationTests
{
    private static IServiceProvider Build(bool logObserver)
    {
        var services = new ServiceCollection();

        services.AddLogging();

        var builder = services.AddAuditing().UseCustomStore(_ => new StubStore());

        if (logObserver)
            builder.UseLoggingObserver();

        return builder.Build().BuildServiceProvider();
    }

    [Fact]
    public void UseLoggingObserver_replaces_the_one_that_does_nothing() =>
        Assert.IsType<LoggingAuditObserver>(Build(logObserver: true).GetRequiredService<IAuditObserver>());

    [Fact]
    public void Without_it_the_default_stays() =>
        Assert.IsType<NullAuditObserver>(Build(logObserver: false).GetRequiredService<IAuditObserver>());

    /// <summary>Enough of a store to let the builder validate. Nothing here records anything.</summary>
    private sealed class StubStore : IAuditStore
    {
        public Task AppendAsync(AuditEntry entry, CancellationToken ct = default) => Task.CompletedTask;

        public Task AppendRangeAsync(IReadOnlyCollection<AuditEntry> entries, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<AuditEntry?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<AuditEntry?>(null);

        public Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken ct = default) =>
            Task.FromResult(new AuditPage([], null));

        public Task<int> DeleteOlderThanAsync(DateTime cutoff, AuditPruneScope scope, int batchSize, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<int> DeleteForEntityAsync(string entityName, string entityId, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<int> DeleteForActorAsync(string actor, CancellationToken ct = default) =>
            Task.FromResult(0);
    }
}
