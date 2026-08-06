using System.Diagnostics.Metrics;
using ERP_RFQ_Automation.Platform.Hardening;

namespace ERP_RFQ_Automation.Tests.Support;

/// <summary>
/// One recorded measurement: the instrument that produced it, the value, and the tags.
/// </summary>
public sealed record RecordedMeasurement(string Instrument, double Value, IReadOnlyDictionary<string, string> Tags)
{
    public string? Tag(string name) => Tags.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// Captures everything a <see cref="NexoraMetrics"/> instance emits, using the BCL
/// <see cref="MeterListener"/> — no test-only metrics package required.
///
/// <para>Each harness creates its OWN <see cref="Meter"/> instance through a private
/// <see cref="IMeterFactory"/> and filters the listener on that exact instance, so
/// concurrently-running tests in the same process cannot see each other's measurements
/// even though they share the meter NAME.</para>
/// </summary>
public sealed class MetricsHarness : IDisposable
{
    private readonly MeterListener _listener;
    private readonly TestMeterFactory _factory = new();
    private readonly List<RecordedMeasurement> _measurements = new();
    private readonly object _gate = new();

    public MetricsHarness(IExtractionQueueSnapshotProvider? snapshots = null)
    {
        Metrics = new NexoraMetrics(_factory, snapshots);
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, _factory.Created))
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((i, m, t, _) => Record(i, m, t));
        _listener.SetMeasurementEventCallback<double>((i, m, t, _) => Record(i, m, t));
        _listener.SetMeasurementEventCallback<int>((i, m, t, _) => Record(i, m, t));
        _listener.Start();
    }

    public NexoraMetrics Metrics { get; }

    /// <summary>Pulls every ObservableGauge on the meter. Call before asserting on gauges.</summary>
    public void CollectObservable() => _listener.RecordObservableInstruments();

    public IReadOnlyList<RecordedMeasurement> Measurements
    {
        get { lock (_gate) return _measurements.ToList(); }
    }

    public IReadOnlyList<RecordedMeasurement> For(string instrument)
    {
        lock (_gate)
            return _measurements.Where(m => m.Instrument == instrument).ToList();
    }

    public double Total(string instrument) => For(instrument).Sum(m => m.Value);

    public void Clear() { lock (_gate) _measurements.Clear(); }

    private void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct, IConvertible
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in tags)
            if (tag.Value is not null)
                map[tag.Key] = Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        lock (_gate)
            _measurements.Add(new RecordedMeasurement(
                instrument.Name,
                Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture),
                map));
    }

    public void Dispose()
    {
        _listener.Dispose();
        Metrics.Dispose();
        _factory.Dispose();
    }

    /// <summary>Minimal <see cref="IMeterFactory"/>: one meter, held so the listener can
    /// filter on the instance rather than the (shared) name.</summary>
    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter? Created { get; private set; }

        public Meter Create(MeterOptions options) => Created ??= new Meter(options);

        public void Dispose() => Created?.Dispose();
    }
}

/// <summary>A snapshot provider a test can drive directly, counting reads so a test can
/// prove that observing gauges never triggers a refresh.</summary>
public sealed class StubQueueSnapshotProvider : IExtractionQueueSnapshotProvider
{
    private ExtractionQueueSnapshot _current = ExtractionQueueSnapshot.Empty;
    private long _publishCount;
    private long _readCount;

    public long ReadCount => Interlocked.Read(ref _readCount);
    public long PublishCount => Interlocked.Read(ref _publishCount);

    public ExtractionQueueSnapshot Current
    {
        get
        {
            Interlocked.Increment(ref _readCount);
            return _current;
        }
    }

    public void Publish(ExtractionQueueSnapshot snapshot)
    {
        _current = snapshot;
        Interlocked.Increment(ref _publishCount);
    }
}
