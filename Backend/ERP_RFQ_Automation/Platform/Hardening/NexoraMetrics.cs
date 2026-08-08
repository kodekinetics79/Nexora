using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ERP_RFQ_Automation.Platform.Hardening;

/// <summary>
/// Central, DI-registered application metrics for Nexora. Registered as a
/// singleton by <see cref="ObservabilityExtensions.AddPlatformObservability"/> and
/// wired into the OpenTelemetry <c>MeterProvider</c> via
/// <see cref="MeterName"/> (see HARDENING-WIRING.md).
///
/// <para><b>Emission sites.</b> Every instrument below is emitted from the real pipeline;
/// an instrument that is defined but never written is worse than no instrument at all,
/// because a flat-zero dashboard reads as "healthy" rather than as "not measured":</para>
/// <list type="bullet">
///   <item><c>jobs.enqueued</c> — <c>Extraction/ExtractionQueue.EnqueueAsync</c>.</item>
///   <item><c>jobs.claimed</c>, <c>claims.refused</c> — <c>Extraction/ExtractionQueue.ClaimAsync</c>.</item>
///   <item><c>jobs.succeeded</c>/<c>failed</c>/<c>deadlettered</c>/<c>job.duration</c>,
///         <c>leases.lost</c> — <c>Extraction/ExtractionWorker.ProcessOnceAsync</c>.</item>
///   <item><c>llm.calls</c>/<c>llm.latency</c> — <c>Extraction/ChunkedExtractionService</c>.</item>
///   <item><c>llm.tokens</c>/<c>llm.cost</c> — <c>AI/AiGovernanceService.CompleteAsync</c>
///         (settlement, so the numbers match the governance ledger exactly).</item>
///   <item><c>tenant_access.fail_open</c> — <c>Platform/Entitlements/TenantAccessService</c>.</item>
///   <item>The <c>queue.*</c> ObservableGauges — read from
///         <see cref="IExtractionQueueSnapshotProvider"/>, which
///         <see cref="ExtractionQueueMetricsPoller"/> refreshes on a bounded interval.
///         Observation costs ZERO database round-trips.</item>
/// </list>
///
/// <para><b>Cardinality contract.</b> Tags are limited to values with a small, bounded
/// domain: tenant id, queue status, a fixed failure-category vocabulary, provider and
/// model. Document ids, job ids, file names, storage paths, error messages and worker
/// ids are NEVER tags — one of those turns a dashboard into an unbounded time-series
/// explosion. Per-tenant series are additionally capped by
/// <see cref="ExtractionQueueSnapshot.From"/>.</para>
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
    private readonly IExtractionQueueSnapshotProvider? _queue;

    // --- Extraction pipeline instruments ----------------------------------
    private readonly Counter<long> _jobsEnqueued;
    private readonly Counter<long> _jobsClaimed;
    private readonly Counter<long> _jobsSucceeded;
    private readonly Counter<long> _jobsFailed;
    private readonly Counter<long> _jobsDeadLettered;
    private readonly Counter<long> _claimsRefused;
    private readonly Counter<long> _leasesLost;
    private readonly Histogram<double> _jobDurationMs;

    // --- LLM instruments --------------------------------------------------
    private readonly Counter<long> _llmCalls;
    private readonly Histogram<double> _llmLatencyMs;
    private readonly Counter<long> _llmTokens;
    private readonly Counter<double> _llmCost;

    // --- Platform enforcement instruments ---------------------------------
    private readonly Counter<long> _tenantAccessFailOpen;

    /// <param name="queueSnapshots">
    /// Optional. When present the golden-signal ObservableGauges below are published;
    /// when absent (a unit test, or a host that does not run the poller) the gauges
    /// still exist but observe nothing, which is honest — no data is reported as no
    /// series, never as zero.
    /// </param>
    public NexoraMetrics(IMeterFactory meterFactory, IExtractionQueueSnapshotProvider? queueSnapshots = null)
    {
        // IMeterFactory (net8.0) ties the Meter lifetime to DI and lets the OTel
        // MeterProvider observe it. Falls back gracefully if no factory is present.
        _meter = meterFactory.Create(MeterName);
        _queue = queueSnapshots;

        _jobsEnqueued = _meter.CreateCounter<long>(
            "nexora.extraction.jobs.enqueued",
            unit: "{job}",
            description: "Extraction jobs accepted onto the durable queue.");

        _jobsClaimed = _meter.CreateCounter<long>(
            "nexora.extraction.jobs.claimed",
            unit: "{job}",
            description: "Extraction jobs successfully leased by a worker. Compare against "
                + "jobs.enqueued: a persistent gap with a flat claim rate is a stalled pipeline.");

        _jobsSucceeded = _meter.CreateCounter<long>(
            "nexora.extraction.jobs.succeeded",
            unit: "{job}",
            description: "Extraction jobs that completed successfully.");

        _jobsFailed = _meter.CreateCounter<long>(
            "nexora.extraction.jobs.failed",
            unit: "{job}",
            description: "Extraction jobs that terminally failed.");

        _jobsDeadLettered = _meter.CreateCounter<long>(
            "nexora.extraction.jobs.deadlettered",
            unit: "{job}",
            description: "Extraction jobs that exhausted their attempts and entered the "
                + "dead-letter queue, tagged with the same FailureCategory vocabulary the "
                + "dead-letter API reports. This is the dead-letter ARRIVAL RATE; "
                + "nexora.extraction.queue.deadletter is the standing depth.");

        _claimsRefused = _meter.CreateCounter<long>(
            "nexora.extraction.claims.refused",
            unit: "{claim}",
            description: "Claims a database invariant refused (poison-message pressure). "
                + "A sustained non-zero rate means head-of-line rows are being charged "
                + "attempts instead of being worked.");

        _leasesLost = _meter.CreateCounter<long>(
            "nexora.extraction.leases.lost",
            unit: "{lease}",
            description: "Worker lease losses/expiries detected mid-processing. Each one is "
                + "work that will be reclaimed and redone; a rising rate means the lease "
                + "duration no longer covers real processing time.");

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

        _llmTokens = _meter.CreateCounter<long>(
            "nexora.llm.tokens",
            unit: "{token}",
            description: "Tokens settled through the AI governance ledger, split by direction. "
                + "Sourced from the settlement path, so it reconciles with AiRequests exactly.");

        _llmCost = _meter.CreateCounter<double>(
            "nexora.llm.cost",
            unit: "{currency}",
            description: "Estimated external-provider spend settled through the AI governance "
                + "ledger. Only emitted when the tenant policy carries a pricing version; an "
                + "unpriced call reports tokens and no cost rather than a fabricated zero.");

        _tenantAccessFailOpen = _meter.CreateCounter<long>(
            "nexora.platform.tenant_access.fail_open",
            unit: "{resolution}",
            description: "Tenant/plan resolutions that failed and fell back to the contracted "
                + "fail-open snapshot (no status enforcement, no plan limits). A sustained "
                + "non-zero rate means the platform plane is unreadable (missing grant/outage) "
                + "and enforcement is silently disabled — alert on it. (Sec2)");

        // ---- golden-signal gauges -------------------------------------------------
        // ALL of these read the cached snapshot. There is deliberately no database access
        // on this path: an ObservableGauge callback runs on every collection cycle (and on
        // every Prometheus scrape), so a query here would be a per-scrape query storm.

        _meter.CreateObservableGauge(
            "nexora.extraction.queue.oldest_pending_age",
            ObserveOldestPendingAge,
            unit: "s",
            description: "Age of the OLDEST job still waiting, per tenant. THE stuck-tenant "
                + "signal: total queue depth stays flat while one tenant's head-of-line job "
                + "ages without bound, so depth alone cannot see a starving tenant. Measured "
                + "from CreatedOn, so a job looping through exponential backoff cannot hide "
                + "behind a freshly-written NextAttemptAt.");

        _meter.CreateObservableGauge(
            "nexora.extraction.queue.depth",
            ObserveQueueDepth,
            unit: "{job}",
            description: "Queue depth per tenant by state (pending / pending-ready / "
                + "pending-backed-off / in-flight / dead-letter).");

        _meter.CreateObservableGauge(
            "nexora.extraction.queue.expired_leases",
            ObserveExpiredLeases,
            unit: "{job}",
            description: "Jobs holding a LAPSED lease per tenant — crashed or overrun workers "
                + "whose rows are waiting to be reclaimed by the next claim.");

        _meter.CreateObservableGauge(
            "nexora.extraction.queue.invariant_blocked",
            ObserveInvariantBlocked,
            unit: "{job}",
            description: "Extraction jobs quarantined for a durable-intake invariant violation, "
                + "per tenant. Values come from stable redacted reason codes, never exception text.");

        _meter.CreateObservableGauge(
            "nexora.extraction.queue.oldest_invariant_blocked_age",
            ObserveOldestInvariantBlockedAge,
            unit: "s",
            description: "Age of the oldest intake-invariant quarantine per affected tenant.");

        _meter.CreateObservableGauge(
            "nexora.extraction.queue.invariant_affected_tenants",
            ObserveInvariantAffectedTenants,
            unit: "{tenant}",
            description: "Number of tenants with at least one governed intake-invariant quarantine.");

        _meter.CreateObservableGauge(
            "nexora.extraction.queue.retries",
            ObserveRetries,
            unit: "{job}",
            description: "Jobs with more than one processing attempt, per tenant.");

        _meter.CreateObservableGauge(
            "nexora.extraction.queue.repeated_invariant_violations",
            ObserveRepeatedInvariantViolations,
            unit: "{job}",
            description: "Legacy/backstop invariant quarantines that already accumulated attempts; "
                + "new scheduler-classified poison rows should remain zero.");

        _meter.CreateObservableGauge(
            "nexora.extraction.queue.snapshot_age",
            ObserveSnapshotAge,
            unit: "s",
            description: "Age of the cached queue snapshot the gauges above are reporting. If "
                + "this climbs past the configured poll interval the poller has stopped and "
                + "every queue gauge is stale — read it before trusting the others.");
    }

    // ---- counters --------------------------------------------------------

    /// <summary>
    /// Sec2: record that a tenant-access resolution failed and the contracted
    /// fail-open path was taken for the given BusinessUnit.
    /// </summary>
    public void TenantAccessFailOpen(long? businessUnitId = null) =>
        _tenantAccessFailOpen.Add(1, Tenant(businessUnitId));

    /// <summary>Record that a job was enqueued (optionally tagged with the tenant).</summary>
    public void JobEnqueued(long? businessUnitId = null) =>
        _jobsEnqueued.Add(1, Tenant(businessUnitId));

    /// <summary>Record that a worker successfully leased a job.</summary>
    public void JobClaimed(long? businessUnitId = null) =>
        _jobsClaimed.Add(1, Tenant(businessUnitId));

    /// <summary>Record that a job succeeded and, if known, how long it took.</summary>
    public void JobSucceeded(double? durationMs = null, long? businessUnitId = null)
    {
        _jobsSucceeded.Add(1, Tenant(businessUnitId));
        if (durationMs is { } ms) _jobDurationMs.Record(ms, Outcome(businessUnitId, "succeeded"));
    }

    /// <summary>Record that a job failed (optionally tagged with a reason + tenant).</summary>
    public void JobFailed(string? reason = null, long? businessUnitId = null, double? durationMs = null)
    {
        var tags = new TagList { { "tenant.id", businessUnitId } };
        if (!string.IsNullOrWhiteSpace(reason)) tags.Add("failure.reason", reason);
        _jobsFailed.Add(1, tags);
        if (durationMs is { } ms) _jobDurationMs.Record(ms, Outcome(businessUnitId, "failed"));
    }

    /// <summary>
    /// Record that a job exhausted its attempts and entered the dead-letter queue.
    /// <paramref name="failureCategory"/> must come from the bounded vocabulary
    /// (<c>ExtractionDeadLetterService.ClassifyFailure</c>), never from a raw error string.
    /// </summary>
    public void JobDeadLettered(string? failureCategory = null, long? businessUnitId = null)
    {
        var tags = new TagList { { "tenant.id", businessUnitId } };
        tags.Add("failure.category",
            string.IsNullOrWhiteSpace(failureCategory) ? "UNCLASSIFIED" : failureCategory);
        _jobsDeadLettered.Add(1, tags);
    }

    /// <summary>Record that a database invariant refused a claim (poison-message pressure).</summary>
    public void ClaimRefused(string? reason = null, long? businessUnitId = null)
    {
        var tags = new TagList { { "tenant.id", businessUnitId } };
        if (!string.IsNullOrWhiteSpace(reason)) tags.Add("refusal.reason", reason);
        _claimsRefused.Add(1, tags);
    }

    /// <summary>Record that a worker lost or outlived its lease while processing.</summary>
    public void LeaseLost(string? stage = null, long? businessUnitId = null)
    {
        var tags = new TagList { { "tenant.id", businessUnitId } };
        if (!string.IsNullOrWhiteSpace(stage)) tags.Add("lease.stage", stage);
        _leasesLost.Add(1, tags);
    }

    /// <summary>Record an LLM provider call and its latency.</summary>
    public void LlmCall(
        double latencyMs, string? model = null, long? businessUnitId = null,
        string? provider = null, string? outcome = null)
    {
        var tags = new TagList { { "tenant.id", businessUnitId } };
        if (!string.IsNullOrWhiteSpace(model)) tags.Add("llm.model", model);
        if (!string.IsNullOrWhiteSpace(provider)) tags.Add("llm.provider", provider);
        if (!string.IsNullOrWhiteSpace(outcome)) tags.Add("llm.outcome", outcome);
        _llmCalls.Add(1, tags);
        _llmLatencyMs.Record(latencyMs, tags);
    }

    /// <summary>
    /// Record the settled token/cost result of one governed AI request. Called from the
    /// governance settlement path so the metric and the ledger can never disagree.
    /// <paramref name="cost"/> is null for an unpriced call — no cost series is emitted
    /// rather than a misleading zero.
    /// </summary>
    public void LlmSettled(
        long inputTokens, long outputTokens, long? businessUnitId = null,
        string? provider = null, string? model = null, string? providerClass = null,
        decimal? cost = null, string? currency = null)
    {
        var tags = new TagList { { "tenant.id", businessUnitId } };
        if (!string.IsNullOrWhiteSpace(provider)) tags.Add("llm.provider", provider);
        if (!string.IsNullOrWhiteSpace(model)) tags.Add("llm.model", model);
        if (!string.IsNullOrWhiteSpace(providerClass)) tags.Add("llm.provider_class", providerClass);

        if (inputTokens > 0)
        {
            var inbound = tags;
            inbound.Add("llm.direction", "input");
            _llmTokens.Add(inputTokens, inbound);
        }
        if (outputTokens > 0)
        {
            var outbound = tags;
            outbound.Add("llm.direction", "output");
            _llmTokens.Add(outputTokens, outbound);
        }
        if (cost is { } amount && amount > 0m && !string.IsNullOrWhiteSpace(currency))
        {
            var priced = tags;
            priced.Add("cost.currency", currency);
            _llmCost.Add((double)amount, priced);
        }
    }

    // ---- observable gauge callbacks --------------------------------------

    private IEnumerable<Measurement<double>> ObserveOldestPendingAge()
    {
        var snapshot = _queue?.Current;
        if (snapshot is null || !snapshot.IsFresh) yield break;
        foreach (var tenant in snapshot.Tenants)
        {
            // A tenant with nothing pending reports 0 — an explicit "not starving",
            // which is what an alert needs to distinguish from "series disappeared".
            yield return new Measurement<double>(
                tenant.OldestPendingAgeSeconds, Tenant(tenant.BusinessUnitId));
        }
    }

    private IEnumerable<Measurement<long>> ObserveQueueDepth()
    {
        var snapshot = _queue?.Current;
        if (snapshot is null || !snapshot.IsFresh) yield break;
        foreach (var tenant in snapshot.Tenants)
        {
            yield return Depth(tenant.BusinessUnitId, "pending", tenant.Pending);
            yield return Depth(tenant.BusinessUnitId, "pending_ready", tenant.PendingReady);
            yield return Depth(tenant.BusinessUnitId, "pending_backed_off", tenant.PendingBackedOff);
            yield return Depth(tenant.BusinessUnitId, "in_flight", tenant.InFlight);
            yield return Depth(tenant.BusinessUnitId, "dead_letter", tenant.DeadLettered);
        }
        if (snapshot.UnreportedTenants > 0)
        {
            yield return new Measurement<long>(
                snapshot.UnreportedTenants,
                new TagList { { "queue.state", "tenants_over_cardinality_cap" } });
        }
    }

    private IEnumerable<Measurement<long>> ObserveExpiredLeases()
    {
        var snapshot = _queue?.Current;
        if (snapshot is null || !snapshot.IsFresh) yield break;
        foreach (var tenant in snapshot.Tenants)
            yield return new Measurement<long>(tenant.ExpiredLeases, Tenant(tenant.BusinessUnitId));
    }

    private IEnumerable<Measurement<long>> ObserveInvariantBlocked()
    {
        var snapshot = _queue?.Current;
        if (snapshot is null || !snapshot.IsFresh) yield break;
        foreach (var tenant in snapshot.Tenants)
            yield return new Measurement<long>(tenant.InvariantBlocked, Tenant(tenant.BusinessUnitId));
    }

    private IEnumerable<Measurement<double>> ObserveOldestInvariantBlockedAge()
    {
        var snapshot = _queue?.Current;
        if (snapshot is null || !snapshot.IsFresh) yield break;
        foreach (var tenant in snapshot.Tenants.Where(x => x.InvariantBlocked > 0))
            yield return new Measurement<double>(
                tenant.OldestInvariantBlockedAgeSeconds, Tenant(tenant.BusinessUnitId));
    }

    private IEnumerable<Measurement<long>> ObserveInvariantAffectedTenants()
    {
        var snapshot = _queue?.Current;
        if (snapshot is null || !snapshot.IsFresh) yield break;
        yield return new Measurement<long>(snapshot.InvariantAffectedTenants);
    }

    private IEnumerable<Measurement<long>> ObserveRetries()
    {
        var snapshot = _queue?.Current;
        if (snapshot is null || !snapshot.IsFresh) yield break;
        foreach (var tenant in snapshot.Tenants)
            yield return new Measurement<long>(tenant.Retried, Tenant(tenant.BusinessUnitId));
    }

    private IEnumerable<Measurement<long>> ObserveRepeatedInvariantViolations()
    {
        var snapshot = _queue?.Current;
        if (snapshot is null || !snapshot.IsFresh) yield break;
        foreach (var tenant in snapshot.Tenants.Where(x => x.RepeatedInvariantViolations > 0))
            yield return new Measurement<long>(
                tenant.RepeatedInvariantViolations, Tenant(tenant.BusinessUnitId));
    }

    private IEnumerable<Measurement<double>> ObserveSnapshotAge()
    {
        var snapshot = _queue?.Current;
        if (snapshot is null || !snapshot.IsFresh) yield break;
        yield return new Measurement<double>(
            Math.Max(0d, (DateTimeOffset.UtcNow - snapshot.TakenAtUtc).TotalSeconds));
    }

    private static Measurement<long> Depth(long businessUnitId, string state, long value) =>
        new(value, new TagList { { "tenant.id", businessUnitId }, { "queue.state", state } });

    private static TagList Tenant(long? businessUnitId) =>
        new() { { "tenant.id", businessUnitId } };

    private static TagList Outcome(long? businessUnitId, string outcome) =>
        new() { { "tenant.id", businessUnitId }, { "job.outcome", outcome } };

    public void Dispose() => _meter.Dispose();
}
