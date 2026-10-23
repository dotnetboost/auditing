using System.Diagnostics.Metrics;

namespace DotNetBoost.Auditing.TestSupport;

/// <summary>
/// An <see cref="IMeterFactory"/> and a <see cref="MeterListener"/> in one: hand it to the engine,
/// then read back what the counters recorded.
/// <code>
/// using var probe   = new MetricsProbe();
/// var       metrics = new AuditMetrics(probe);
/// // … act …
/// Assert.Equal(1, probe.Total(AuditingTelemetry.EntriesRecorded, ("audit.entity", "Product")));
/// </code>
/// </summary>
/// <remarks>
/// <b>It listens to its own meter and nothing else.</b> The meter carries the real production name,
/// so a test can assert on that, but the listener filters by meter <i>instance</i> — otherwise
/// every test in the process would see every other test's measurements, and xUnit runs test classes
/// in parallel. That is the whole reason the engine takes an <see cref="IMeterFactory"/> rather
/// than owning a static meter.
/// <para>
/// No test-only package for this: a hand-rolled listener is a dozen lines, and
/// <c>Microsoft.Extensions.Diagnostics.Testing</c> would be a dependency the shipped code does not
/// have.
/// </para>
/// </remarks>
public sealed class MetricsProbe : IMeterFactory
{
    private readonly MeterListener _listener = new();
    private readonly List<Measurement> _measurements = [];
    private readonly object _gate = new();

    private Meter? _meter;

    /// <summary>Starts listening. Instruments created afterwards are picked up as they appear.</summary>
    public MetricsProbe()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            // By instance, not by name: the production meter name is shared with every other test
            // in the process, and a measurement from one of those would be indistinguishable here.
            if (ReferenceEquals(instrument.Meter, _meter))
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var copied = new Dictionary<string, object?>(tags.Length, StringComparer.Ordinal);

            foreach (var tag in tags)
                copied[tag.Key] = tag.Value;

            // The callback can arrive on any thread, and a test asserting on the list is on another.
            lock (_gate)
                _measurements.Add(new Measurement(instrument.Name, value, copied));
        });

        _listener.Start();
    }

    /// <summary>Every measurement seen, in order.</summary>
    public IReadOnlyList<Measurement> Measurements
    {
        get
        {
            lock (_gate)
                return [.. _measurements];
        }
    }

    /// <inheritdoc/>
    /// <remarks>Creates the one meter this probe listens to. Called by the engine, once.</remarks>
    public Meter Create(MeterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _meter = new Meter(options);
        return _meter;
    }

    /// <summary>
    /// The sum of <paramref name="instrument"/>'s measurements whose tags include all of
    /// <paramref name="tags"/> — zero when none match, which is the assertion a test makes about a
    /// counter that must not have moved.
    /// </summary>
    public long Total(string instrument, params (string Tag, object? Value)[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        lock (_gate)
        {
            return _measurements
                .Where(m => string.Equals(m.Instrument, instrument, StringComparison.Ordinal))
                .Where(m => tags.All(t => m.Tags.TryGetValue(t.Tag, out var value) && Equals(value, t.Value)))
                .Sum(m => m.Value);
        }
    }

    /// <summary>The name the engine asked its meter to be called, for the test that pins it.</summary>
    public string? MeterName => _meter?.Name;

    /// <summary>The version the engine reported with it.</summary>
    public string? MeterVersion => _meter?.Version;

    /// <inheritdoc/>
    public void Dispose()
    {
        _listener.Dispose();
        _meter?.Dispose();
    }

    /// <summary>One recorded measurement.</summary>
    /// <param name="Instrument">The instrument's name.</param>
    /// <param name="Value">The value added.</param>
    /// <param name="Tags">Its tags.</param>
    public sealed record Measurement(string Instrument, long Value, IReadOnlyDictionary<string, object?> Tags);
}
