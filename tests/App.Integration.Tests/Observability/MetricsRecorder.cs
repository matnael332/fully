using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using WorkContinuity.Infrastructure.Observability;

namespace WorkContinuity.Integration.Tests.Observability;

/// <summary>One measurement: the instrument, the value and every tag as the listener saw it.</summary>
internal sealed record RecordedMeasurement(string Instrument, double Value, IReadOnlyDictionary<string, string?> Tags)
{
    public bool Has(string key, string value) => Tags.TryGetValue(key, out var actual) && actual == value;
}

/// <summary>
/// Listens to exactly one <see cref="EventBackboneMetrics"/> instance, the one a composed host resolved, so other
/// tests' hosts never show up here.
/// </summary>
internal sealed partial class MetricsRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<RecordedMeasurement> _measurements = new();

    public MetricsRecorder(EventBackboneMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.Start();
    }

    public IReadOnlyList<RecordedMeasurement> Measurements => [.. _measurements];

    public IReadOnlyList<RecordedMeasurement> Of(string instrument) =>
        Measurements.Where(measurement => measurement.Instrument == instrument).ToList();

    /// <summary>The sum of an instrument's values whose tags include every given pair.</summary>
    public double Sum(string instrument, params (string Key, string Value)[] tags) =>
        Of(instrument).Where(measurement => tags.All(tag => measurement.Has(tag.Key, tag.Value))).Sum(measurement => measurement.Value);

    /// <summary>
    /// Asserts every measurement carries only the declared tag keys, each with a short lowercase token, and never
    /// any of the given identifiers or content.
    /// </summary>
    public void AssertBoundedTags(params string[] forbidden)
    {
        var measurements = Measurements;
        Assert.NotEmpty(measurements);
        Assert.All(measurements, measurement =>
        {
            Assert.All(measurement.Tags, tag =>
            {
                Assert.Contains(tag.Key, EventBackboneMetrics.TagKeys);
                Assert.NotNull(tag.Value);
                Assert.Matches(TagValue(), tag.Value);
                Assert.All(forbidden, text => Assert.DoesNotContain(text, tag.Value, StringComparison.OrdinalIgnoreCase));
            });
        });
    }

    public void Dispose() => _listener.Dispose();

    [GeneratedRegex("^[a-z][a-z0-9_.]{0,63}$")]
    private static partial Regex TagValue();

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copied = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            copied[tag.Key] = tag.Value?.ToString();
        }

        _measurements.Enqueue(new RecordedMeasurement(instrument.Name, value, copied));
    }
}
