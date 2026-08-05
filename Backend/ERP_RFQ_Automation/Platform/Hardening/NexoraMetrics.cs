using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ERP_RFQ_Automation.Platform.Hardening;

/// <summary>
/// Central, DI-registered application metrics for Nexora. Registered as a
/// singleton by <see cref="ObservabilityExtensions.AddPlatformObservability"/> and
/// wired into the OpenTelemetry <c>MeterProvider</c> via
/// <see cref="MeterName"/> (see HARDENING-WIRING.md).
///
/// This class is intentionally decoupled from the existing extraction pipeline:
/// the hardening module cannot edit those files, so instead of emitting metrics
/// from here it EXPOSES the instruments. Any existing service
/// (e.g. <c>ExtractionWorker</c>, <c>ChunkedExtractionService</c>) can later
/// inject <see cref="NexoraMetrics"/> and call the helpers below — no change to
/// the observability wiring is required, the counters/histograms already flow to
/// the configured OTLP/console exporter.
/// </summary>
public sealed class NexoraMetrics : IDisposable
{
    /// <summary>
    /// Meter name registered with the OpenTelemetry <c>MeterProvider</c>
    /// (<c>metrics.AddMeter(NexoraMetrics.MeterName)</c>). Kept stable so
    /// dashboards/alerts can pin to it.
    /// </summary>
    public const string MeterName = "Nexora.Extraction";

    private readonly Meter _meter;

    // --- Extraction pipeline instruments ----------------------------------
    private readonly Counter<long> _jobsEnqueued;
    private readonly Counter<long> _jobsSucceeded;
    private readonly Counter<long> _jobsFailed;
    private readonly Histogram<double> _jobDurationMs;

    // --- LLM instruments --------------------------------------------------
    private readonly Counter<long> _llmCalls;
    private readonly Histogram<double> _llmLatencyMs;

    // --- Platform enforcement instruments ---------------------------------
    private readonly Counter<long> _tenantAccessFailOpen;

    public NexoraMetrics(IMeterFactory meterFactory)
    {
        // IMeterFactory (net8.0) ties the Meter lifetime to DI and lets the OTel
        // MeterProvider observe it. Falls back gracefully if no factory is present.
        _meter = meterFactory.Create(MeterName);

        _jobsEnqueued = _meter.CreateCounter<long>(
            "nexora.extraction.jobs.enqueued",
            unit: "{job}",
            description: "Extraction jobs accepted onto the durable queue.");

        _jobsSucceeded = _meter.CreateCounter<long>(
            "nexora.extraction.jobs.succeeded",
            unit: "{job}",
            description: "Extraction jobs that completed successfully.");

        _jobsFailed = _meter.CreateCounter<long>(
            "nexora.extraction.jobs.failed",
            unit: "{job}",
            description: "Extraction jobs that terminally failed.");

        _jobDurationMs = _meter.CreateHistogram<double>(
            "nexora.extraction.job.duration",
            unit: "ms",
            description: "Wall-clock duration of an extraction job.");

        _llmCalls = _meter.CreateCounter<long>(
            "nexora.llm.calls",
            unit: "{call}",
            description: "Calls issued to the LLM provider.");

        _llmLatencyMs = _meter.CreateHistogram<double>(
            "nexora.llm.latency",
            unit: "ms",
            description: "Latency of a single LLM provider call.");

        _tenantAccessFailOpen = _meter.CreateCounter<long>(
            "nexora.platform.tenant_access.fail_open",
            unit: "{resolution}",
            description: "Tenant/plan resolutions that failed and fell back to the contracted "
                + "fail-open snapshot (no status enforcement, no plan limits). A sustained "
                + "non-zero rate means the platform plane is unreadable (missing grant/outage) "
                + "and enforcement is silently disabled — alert on it. (Sec2)");
    }

    /// <summary>
    /// Sec2: record that a tenant-access resolution failed and the contracted
    /// fail-open path was taken for the given BusinessUnit.
    /// </summary>
    public void TenantAccessFailOpen(long? businessUnitId = null) =>
        _tenantAccessFailOpen.Add(1, Tenant(businessUnitId));

    /// <summary>Record that a job was enqueued (optionally tagged with the tenant).</summary>
    public void JobEnqueued(long? businessUnitId = null) =>
        _jobsEnqueued.Add(1, Tenant(businessUnitId));

    /// <summary>Record that a job succeeded and, if known, how long it took.</summary>
    public void JobSucceeded(double? durationMs = null, long? businessUnitId = null)
    {
        _jobsSucceeded.Add(1, Tenant(businessUnitId));
        if (durationMs is { } ms) _jobDurationMs.Record(ms, Tenant(businessUnitId));
    }

    /// <summary>Record that a job failed (optionally tagged with a reason + tenant).</summary>
    public void JobFailed(string? reason = null, long? businessUnitId = null)
    {
        var tags = new TagList { { "tenant.id", businessUnitId } };
        if (!string.IsNullOrWhiteSpace(reason)) tags.Add("failure.reason", reason);
        _jobsFailed.Add(1, tags);
    }

    /// <summary>Record an LLM provider call and its latency.</summary>
    public void LlmCall(double latencyMs, string? model = null, long? businessUnitId = null)
    {
        var tags = new TagList { { "tenant.id", businessUnitId } };
        if (!string.IsNullOrWhiteSpace(model)) tags.Add("llm.model", model);
        _llmCalls.Add(1, tags);
        _llmLatencyMs.Record(latencyMs, tags);
    }

    private static TagList Tenant(long? businessUnitId) =>
        new() { { "tenant.id", businessUnitId } };

    public void Dispose() => _meter.Dispose();
}
