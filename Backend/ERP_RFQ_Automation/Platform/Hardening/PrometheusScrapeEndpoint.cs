using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Platform.Hardening;

/// <summary>Configuration for the built-in Prometheus scrape endpoint
/// (<c>Observability:Prometheus</c>).</summary>
public sealed class PrometheusScrapeOptions
{
    public const string SectionName = "Observability:Prometheus";

    /// <summary>
    /// Null means "decide automatically": the endpoint is enabled exactly when no OTLP
    /// endpoint is configured, i.e. it is the FALLBACK that stops the deployment from
    /// running blind. Set explicitly to force it on or off.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>Scrape path. Kept off <c>/api</c> so it can never collide with a controller.</summary>
    public string Path { get; set; } = "/metrics";

    /// <summary>
    /// Shared secret the scraper must present as <c>X-Scrape-Key</c>. When UNSET the
    /// endpoint is open, and startup logs a loud warning — the exposition carries tenant
    /// ids and queue depths, which is operational data, not public data.
    /// </summary>
    public string? ScrapeKey { get; set; }

    /// <summary>Header the scrape key is read from.</summary>
    public const string ScrapeKeyHeader = "X-Scrape-Key";
}

/// <summary>
/// A Prometheus text-exposition endpoint built on <see cref="MeterListener"/> — the
/// BCL primitive — and nothing else.
///
/// <para><b>Why hand-rolled.</b> The project already references the OTLP and Console
/// exporters; a Prometheus exporter would be an additional package
/// (<c>OpenTelemetry.Exporter.Prometheus.AspNetCore</c>, still pre-release) and this
/// change was scoped to add none. The Nexora meter emits counters, one histogram family
/// and a handful of observable gauges — a small enough surface that the BCL listener
/// covers it exactly, with no dependency and no version risk.</para>
///
/// <para><b>Deliberately scoped to <see cref="NexoraMetrics.MeterName"/>.</b> The ASP.NET
/// Core and HttpClient instrumentation meters carry per-route and per-host dimensions and
/// are the classic source of a cardinality blow-up in a hand-rolled exposition. Those stay
/// on the OTLP path, where the SDK's views can bound them. This endpoint exposes the
/// application's own bounded instruments only.</para>
/// </summary>
public sealed class NexoraPrometheusCollector : IDisposable
{
    // Milliseconds. Sized for extraction jobs (seconds to minutes) and LLM calls
    // (hundreds of ms to tens of seconds) — the two histogram users.
    private static readonly double[] BucketBoundsMs =
    {
        5, 10, 25, 50, 100, 250, 500, 1_000, 2_500, 5_000, 10_000, 30_000, 60_000, 300_000
    };

    private readonly MeterListener _listener;
    private readonly object _gate = new();
    private readonly Dictionary<string, InstrumentFamily> _families = new(StringComparer.Ordinal);
    private readonly string _meterName;

    public NexoraPrometheusCollector(string meterName = NexoraMetrics.MeterName)
    {
        _meterName = meterName;
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, _meterName, StringComparison.Ordinal))
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((i, m, t, _) => Record(i, m, t));
        _listener.SetMeasurementEventCallback<int>((i, m, t, _) => Record(i, m, t));
        _listener.SetMeasurementEventCallback<double>((i, m, t, _) => Record(i, m, t));
        _listener.SetMeasurementEventCallback<float>((i, m, t, _) => Record(i, m, t));
        _listener.SetMeasurementEventCallback<short>((i, m, t, _) => Record(i, m, t));
        _listener.SetMeasurementEventCallback<byte>((i, m, t, _) => Record(i, m, t));
        _listener.SetMeasurementEventCallback<decimal>((i, m, t, _) => Record(i, (double)m, t));
        _listener.Start();
    }

    /// <summary>
    /// Renders the current exposition. Observable instruments are pulled at this moment
    /// (<see cref="MeterListener.RecordObservableInstruments"/>), which for the queue
    /// gauges reads the cached snapshot — no database access on the scrape path.
    /// </summary>
    public string Scrape()
    {
        lock (_gate)
        {
            // Drop the previous pull's gauge series first so a tenant that no longer has
            // a queue stops being reported instead of freezing at its last value.
            foreach (var family in _families.Values)
                if (family.Kind == FamilyKind.Gauge)
                    family.Series.Clear();
        }

        _listener.RecordObservableInstruments();

        var builder = new StringBuilder(4_096);
        lock (_gate)
        {
            foreach (var family in _families.Values.OrderBy(f => f.PrometheusName, StringComparer.Ordinal))
                family.Write(builder);
        }
        return builder.ToString();
    }

    private void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct, IConvertible
    {
        var measurement = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        var labels = FormatLabels(tags);
        lock (_gate)
        {
            if (!_families.TryGetValue(instrument.Name, out var family))
                _families[instrument.Name] = family = InstrumentFamily.For(instrument);
            family.Observe(labels, measurement);
        }
    }

    private static string FormatLabels(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0) return string.Empty;

        // Sorted so that the same logical tag set always produces the same series key,
        // regardless of the order the call site happened to add the tags in.
        var pairs = new List<KeyValuePair<string, object?>>(tags.Length);
        foreach (var tag in tags)
        {
            if (tag.Value is null) continue; // an unset tenant id is "no label", not "null"
            pairs.Add(tag);
        }
        if (pairs.Count == 0) return string.Empty;
        pairs.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));

        var builder = new StringBuilder("{");
        for (var i = 0; i < pairs.Count; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append(SanitizeName(pairs[i].Key)).Append("=\"")
                .Append(EscapeLabelValue(Convert.ToString(pairs[i].Value, CultureInfo.InvariantCulture) ?? ""))
                .Append('"');
        }
        return builder.Append('}').ToString();
    }

    private static string EscapeLabelValue(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>Prometheus names allow [a-zA-Z0-9_:] only; the OTel dotted names map by
    /// replacing every other character with an underscore.</summary>
    internal static string SanitizeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
            builder.Append(char.IsLetterOrDigit(c) || c == '_' || c == ':' ? c : '_');
        // A leading digit is illegal.
        if (builder.Length > 0 && char.IsDigit(builder[0])) builder.Insert(0, '_');
        return builder.ToString();
    }

    public void Dispose() => _listener.Dispose();

    private enum FamilyKind { Counter, Gauge, Histogram }

    private sealed class InstrumentFamily
    {
        public required string PrometheusName { get; init; }
        public required FamilyKind Kind { get; init; }
        public required string Help { get; init; }
        public Dictionary<string, SeriesSample> Series { get; } = new(StringComparer.Ordinal);

        public static InstrumentFamily For(Instrument instrument)
        {
            var kind = instrument switch
            {
                _ when instrument.GetType().Name.StartsWith("Histogram", StringComparison.Ordinal)
                    => FamilyKind.Histogram,
                _ when instrument.GetType().Name.StartsWith("ObservableGauge", StringComparison.Ordinal)
                    => FamilyKind.Gauge,
                _ when instrument.GetType().Name.StartsWith("ObservableUpDownCounter", StringComparison.Ordinal)
                    => FamilyKind.Gauge,
                _ when instrument.GetType().Name.StartsWith("UpDownCounter", StringComparison.Ordinal)
                    => FamilyKind.Gauge,
                _ => FamilyKind.Counter
            };
            var name = SanitizeName(instrument.Name);
            if (kind == FamilyKind.Counter) name += "_total";
            var help = (instrument.Description ?? instrument.Name)
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(instrument.Unit)) help = $"[{instrument.Unit}] {help}";
            return new InstrumentFamily { PrometheusName = name, Kind = kind, Help = help };
        }

        public void Observe(string labels, double value)
        {
            if (!Series.TryGetValue(labels, out var series))
                Series[labels] = series = new SeriesSample(Kind == FamilyKind.Histogram ? BucketBoundsMs.Length : 0);
            series.Observe(Kind, value);
        }

        public void Write(StringBuilder builder)
        {
            if (Series.Count == 0) return;
            builder.Append("# HELP ").Append(PrometheusName).Append(' ').Append(Help).Append('\n');
            builder.Append("# TYPE ").Append(PrometheusName).Append(' ')
                .Append(Kind switch
                {
                    FamilyKind.Counter => "counter",
                    FamilyKind.Gauge => "gauge",
                    _ => "histogram"
                }).Append('\n');

            foreach (var (labels, series) in Series.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (Kind == FamilyKind.Histogram)
                {
                    var cumulative = 0L;
                    for (var i = 0; i < BucketBoundsMs.Length; i++)
                    {
                        cumulative += series.Buckets![i];
                        builder.Append(PrometheusName).Append("_bucket")
                            .Append(WithLabel(labels, "le", Number(BucketBoundsMs[i])))
                            .Append(' ').Append(cumulative).Append('\n');
                    }
                    builder.Append(PrometheusName).Append("_bucket")
                        .Append(WithLabel(labels, "le", "+Inf"))
                        .Append(' ').Append(series.Count).Append('\n');
                    builder.Append(PrometheusName).Append("_sum").Append(labels)
                        .Append(' ').Append(Number(series.Value)).Append('\n');
                    builder.Append(PrometheusName).Append("_count").Append(labels)
                        .Append(' ').Append(series.Count).Append('\n');
                }
                else
                {
                    builder.Append(PrometheusName).Append(labels)
                        .Append(' ').Append(Number(series.Value)).Append('\n');
                }
            }
        }

        private static string WithLabel(string labels, string name, string value)
        {
            var pair = $"{name}=\"{value}\"";
            return labels.Length == 0 ? $"{{{pair}}}" : $"{labels[..^1]},{pair}}}";
        }

        private static string Number(double value) =>
            value.ToString("G17", CultureInfo.InvariantCulture);
    }

    private sealed class SeriesSample
    {
        public SeriesSample(int buckets) => Buckets = buckets > 0 ? new long[buckets] : null;

        public double Value { get; private set; }
        public long Count { get; private set; }
        public long[]? Buckets { get; }

        public void Observe(FamilyKind kind, double value)
        {
            switch (kind)
            {
                case FamilyKind.Counter:
                    Value += value;
                    Count++;
                    break;
                case FamilyKind.Gauge:
                    Value = value;
                    Count++;
                    break;
                default:
                    Value += value;
                    Count++;
                    for (var i = 0; i < BucketBoundsMs.Length; i++)
                    {
                        if (value <= BucketBoundsMs[i]) { Buckets![i]++; break; }
                    }
                    break;
            }
        }
    }
}

/// <summary>Endpoint mapping for <see cref="NexoraPrometheusCollector"/>.</summary>
public static class PrometheusScrapeEndpointExtensions
{
    /// <summary>
    /// Maps the Prometheus scrape endpoint when it is enabled. A no-op otherwise, so the
    /// single call site in Program.cs is unconditional and the decision stays in one place
    /// (<see cref="ObservabilityExtensions.SelectExporter"/>).
    /// </summary>
    public static IEndpointRouteBuilder MapNexoraMetricsScrape(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var collector = endpoints.ServiceProvider.GetService<NexoraPrometheusCollector>();
        if (collector is null) return endpoints; // exporter selection said no
        var options = endpoints.ServiceProvider.GetService<PrometheusScrapeOptions>()
            ?? new PrometheusScrapeOptions();

        var path = string.IsNullOrWhiteSpace(options.Path) ? "/metrics" : options.Path.Trim();

        // AllowAnonymous because the app sets an authorization FallbackPolicy; the scrape
        // key below is this endpoint's own, explicit authentication, exactly as the health
        // probes carry their own posture.
        endpoints.MapGet(path, (HttpContext context) =>
        {
            if (!string.IsNullOrWhiteSpace(options.ScrapeKey))
            {
                var presented = context.Request.Headers[PrometheusScrapeOptions.ScrapeKeyHeader].ToString();
                if (!FixedTimeEquals(presented, options.ScrapeKey))
                    return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }
            return Results.Text(collector.Scrape(), "text/plain; version=0.0.4; charset=utf-8");
        }).AllowAnonymous();

        return endpoints;
    }

    private static bool FixedTimeEquals(string presented, string expected)
    {
        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
