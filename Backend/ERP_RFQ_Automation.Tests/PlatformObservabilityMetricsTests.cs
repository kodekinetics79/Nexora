using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Hardening;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The platform used to define a full set of instruments and emit exactly ONE of them, so
/// every dashboard read flat zero and flat zero read as "healthy". These tests hold the
/// line the other way round: an instrument that exists must be provably written by the
/// real code path, the golden-signal gauges must compute correctly (including the empty
/// and no-pending cases), a metrics read must never cost a database query, and the
/// "nothing is exported" posture must be stated out loud at boot.
/// </summary>
public class PlatformObservabilityMetricsTests
{
    // ---------------------------------------------------------------- enqueue

    [Fact]
    public async Task Enqueue_EmitsJobEnqueuedCounter_TaggedWithTenant()
    {
        const long businessUnitId = 77_101;
        using var harness = new MetricsHarness();
        using var db = new TestDb();
        using var ctx = db.ContextFor(businessUnitId);
        var queue = new ExtractionQueue(ctx, new NoopLogger<ExtractionQueue>(), new StubTenant(businessUnitId), null, harness.Metrics);

        var result = await queue.EnqueueAsync(Request(businessUnitId, "aa01"));

        Assert.Equal(EnqueueOutcome.Enqueued, result.Outcome);
        var emitted = harness.For("nexora.extraction.jobs.enqueued");
        Assert.Single(emitted);
        Assert.Equal(1, emitted[0].Value);
        Assert.Equal(businessUnitId.ToString(), emitted[0].Tag("tenant.id"));
    }

    [Fact]
    public async Task Enqueue_DuplicateContent_DoesNotDoubleCount()
    {
        const long businessUnitId = 77_102;
        using var harness = new MetricsHarness();
        using var db = new TestDb();
        using var ctx = db.ContextFor(businessUnitId);
        var queue = new ExtractionQueue(ctx, new NoopLogger<ExtractionQueue>(), new StubTenant(businessUnitId), null, harness.Metrics);

        await queue.EnqueueAsync(Request(businessUnitId, "aa02"));
        var second = await queue.EnqueueAsync(Request(businessUnitId, "aa02"));

        // A duplicate accepted no new work. Counting it would make the enqueue rate spike
        // precisely when a sender is retrying — the opposite of what the number is for.
        Assert.Equal(EnqueueOutcome.Duplicate, second.Outcome);
        Assert.Equal(1, harness.Total("nexora.extraction.jobs.enqueued"));
    }

    [Fact]
    public async Task Enqueue_TagsEachTenantSeparately()
    {
        using var harness = new MetricsHarness();
        using var db = new TestDb();
        using (var a = db.ContextFor(77_103))
            await new ExtractionQueue(a, new NoopLogger<ExtractionQueue>(), new StubTenant(77_103), null, harness.Metrics)
                .EnqueueAsync(Request(77_103, "aa03"));
        using (var b = db.ContextFor(77_104))
            await new ExtractionQueue(b, new NoopLogger<ExtractionQueue>(), new StubTenant(77_104), null, harness.Metrics)
                .EnqueueAsync(Request(77_104, "aa03"));

        var tenants = harness.For("nexora.extraction.jobs.enqueued")
            .Select(m => m.Tag("tenant.id")).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "77103", "77104" }, tenants);
    }

    // ----------------------------------------------------------- worker paths

    [Fact]
    public async Task Worker_Success_EmitsSucceededCounterAndDuration()
    {
        const long businessUnitId = 77_201;
        using var harness = new MetricsHarness();
        await RunWorkerOnceAsync(harness, businessUnitId, succeed: true);

        var succeeded = harness.For("nexora.extraction.jobs.succeeded");
        Assert.Single(succeeded);
        Assert.Equal(businessUnitId.ToString(), succeeded[0].Tag("tenant.id"));

        var duration = harness.For("nexora.extraction.job.duration");
        Assert.Single(duration);
        Assert.Equal("succeeded", duration[0].Tag("job.outcome"));
        Assert.True(duration[0].Value >= 0);

        Assert.Empty(harness.For("nexora.extraction.jobs.failed"));
    }

    [Fact]
    public async Task Worker_Failure_EmitsFailedCounterWithReason_AndDuration()
    {
        const long businessUnitId = 77_202;
        using var harness = new MetricsHarness();
        await RunWorkerOnceAsync(harness, businessUnitId, succeed: false);

        var failed = harness.For("nexora.extraction.jobs.failed");
        Assert.Single(failed);
        Assert.Equal(businessUnitId.ToString(), failed[0].Tag("tenant.id"));
        Assert.Equal("extraction_failed", failed[0].Tag("failure.reason"));

        var duration = harness.For("nexora.extraction.job.duration");
        Assert.Single(duration);
        Assert.Equal("failed", duration[0].Tag("job.outcome"));
    }

    [Fact]
    public async Task Worker_FailureOnFinalAttempt_AlsoCountsAsDeadLetterArrival()
    {
        const long businessUnitId = 77_203;
        using var harness = new MetricsHarness();
        // Attempts == MaxAttempts is exactly the condition under which the queue's
        // FailAsync moves the row to DeadLetter.
        await RunWorkerOnceAsync(harness, businessUnitId, succeed: false, attempts: 5, maxAttempts: 5);

        var deadLettered = harness.For("nexora.extraction.jobs.deadlettered");
        Assert.Single(deadLettered);
        Assert.Equal(businessUnitId.ToString(), deadLettered[0].Tag("tenant.id"));
        // The category comes from the dead-letter service's own closed vocabulary, so the
        // counter and the operator-facing dead-letter queue can never disagree.
        Assert.Equal("EXTRACTION_FAILURE", deadLettered[0].Tag("failure.category"));
    }

    [Fact]
    public async Task Worker_FailureWithAttemptsRemaining_IsNotADeadLetterArrival()
    {
        using var harness = new MetricsHarness();
        await RunWorkerOnceAsync(harness, 77_204, succeed: false, attempts: 1, maxAttempts: 5);

        Assert.Single(harness.For("nexora.extraction.jobs.failed"));
        Assert.Empty(harness.For("nexora.extraction.jobs.deadlettered"));
    }

    [Fact]
    public void DeadLetterCategory_UsesTheSameClosedVocabularyAsTheDeadLetterApi()
    {
        // The tag domain must stay small and closed — this is what makes it safe as a
        // metric dimension where the raw LastError text never would be.
        Assert.Equal("UNCLASSIFIED", ExtractionDeadLetterService.ClassifyFailure(null));
        Assert.Equal("EVIDENCE_INTEGRITY",
            ExtractionDeadLetterService.ClassifyFailure("Evidence integrity failure: bad hash"));
        Assert.Equal("UNSUPPORTED_DOCUMENT",
            ExtractionDeadLetterService.ClassifyFailure("unsupported document format"));
        Assert.Equal("INTAKE_INVARIANT",
            ExtractionDeadLetterService.ClassifyFailure(
                "[EXTRACTION_INTAKE_JOB_LINK_MISMATCH] redacted identifiers"));
        Assert.Equal("EXTRACTION_FAILURE",
            ExtractionDeadLetterService.ClassifyFailure("something else entirely"));
    }

    // ------------------------------------------------ oldest-pending-age gauge

    [Fact]
    public void OldestPendingAge_IsComputedPerTenant()
    {
        var now = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
        var snapshot = ExtractionQueueSnapshot.From(new[]
        {
            // Tenant 1: head-of-line job is 10 minutes old. Tenant 2: 30 seconds old.
            Group(1, "Pending", count: 3, oldest: now.UtcDateTime.AddMinutes(-10)),
            Group(2, "Pending", count: 300, oldest: now.UtcDateTime.AddSeconds(-30))
        }, now);

        var starving = Assert.Single(snapshot.Tenants, t => t.BusinessUnitId == 1);
        var busy = Assert.Single(snapshot.Tenants, t => t.BusinessUnitId == 2);

        // THE point of this gauge: tenant 2 owns 100x the depth and is perfectly healthy,
        // tenant 1 has three jobs and is starving. Depth cannot tell them apart; age can.
        Assert.Equal(600d, starving.OldestPendingAgeSeconds, precision: 0);
        Assert.Equal(30d, busy.OldestPendingAgeSeconds, precision: 0);
        Assert.True(busy.Pending > starving.Pending);
    }

    [Fact]
    public void OldestPendingAge_IsZeroForATenantWithNoPendingJobs()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = ExtractionQueueSnapshot.From(new[]
        {
            Group(5, "Succeeded", count: 40, oldest: now.UtcDateTime.AddDays(-3)),
            Group(5, "DeadLetter", count: 2, oldest: now.UtcDateTime.AddDays(-2))
        }, now);

        var tenant = Assert.Single(snapshot.Tenants);
        // An explicit zero, NOT the three-day-old Succeeded row: terminal work is not
        // waiting, and reporting its age would fire an alert on a healthy tenant.
        Assert.Equal(0d, tenant.OldestPendingAgeSeconds);
        Assert.Equal(0, tenant.Pending);
        Assert.Equal(2, tenant.DeadLettered);
    }

    [Fact]
    public void EmptyQueue_ProducesAFreshSnapshotWithNoTenantSeries()
    {
        var snapshot = ExtractionQueueSnapshot.From(
            Array.Empty<ExtractionQueueGroup>(), DateTimeOffset.UtcNow);

        // Fresh (we DID look) but empty (there is nothing to report). The distinction
        // matters: "no rows" and "the poller is broken" must not look the same.
        Assert.True(snapshot.IsFresh);
        Assert.Empty(snapshot.Tenants);
        Assert.Equal(0, snapshot.UnreportedTenants);
    }

    [Fact]
    public void BackedOffPendingJobs_StillAgeAndAreCountedSeparately()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = ExtractionQueueSnapshot.From(new[]
        {
            Group(9, "Pending", count: 1, oldest: now.UtcDateTime.AddHours(-4), ready: false),
            Group(9, "Pending", count: 2, oldest: now.UtcDateTime.AddMinutes(-1), ready: true)
        }, now);

        var tenant = Assert.Single(snapshot.Tenants);
        // A job looping through exponential backoff keeps a fresh NextAttemptAt forever.
        // Age is measured from CreatedOn precisely so it cannot hide there.
        Assert.Equal(4 * 3600d, tenant.OldestPendingAgeSeconds, precision: 0);
        Assert.Equal(3, tenant.Pending);
        Assert.Equal(1, tenant.PendingBackedOff);
        Assert.Equal(2, tenant.PendingReady);
    }

    [Fact]
    public void ExpiredLeases_AreCountedPerTenant()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = ExtractionQueueSnapshot.From(new[]
        {
            Group(11, "Leased", count: 2, oldest: now.UtcDateTime, leaseLapsed: true),
            Group(11, "Extracting", count: 3, oldest: now.UtcDateTime, leaseLapsed: false)
        }, now);

        var tenant = Assert.Single(snapshot.Tenants);
        Assert.Equal(5, tenant.InFlight);
        Assert.Equal(2, tenant.ExpiredLeases);
    }

    [Fact]
    public void TenantSeries_AreCappedWorstFirst_AndTheOverflowIsReported()
    {
        var now = DateTimeOffset.UtcNow;
        var groups = Enumerable.Range(1, 10)
            .Select(i => Group(i, "Pending", count: 1, oldest: now.UtcDateTime.AddSeconds(-i)))
            .ToArray();

        var snapshot = ExtractionQueueSnapshot.From(groups, now, maxTenants: 3);

        Assert.Equal(3, snapshot.Tenants.Count);
        Assert.Equal(7, snapshot.UnreportedTenants);
        // Ranked by oldest age, so the cap can never drop the tenant an operator is
        // about to be paged about.
        Assert.Equal(new long[] { 10, 9, 8 }, snapshot.Tenants.Select(t => t.BusinessUnitId).ToArray());
    }

    [Fact]
    public void QueueGauges_ObserveTheCachedSnapshot()
    {
        var provider = new StubQueueSnapshotProvider();
        using var harness = new MetricsHarness(provider);
        var now = DateTimeOffset.UtcNow;
        provider.Publish(ExtractionQueueSnapshot.From(new[]
        {
            Group(4_242, "Pending", count: 7, oldest: now.UtcDateTime.AddMinutes(-5))
        }, now));

        harness.CollectObservable();

        var age = Assert.Single(harness.For("nexora.extraction.queue.oldest_pending_age"));
        Assert.Equal("4242", age.Tag("tenant.id"));
        Assert.Equal(300d, age.Value, precision: 0);

        var pending = Assert.Single(harness.For("nexora.extraction.queue.depth"),
            m => m.Tag("queue.state") == "pending");
        Assert.Equal(7d, pending.Value);
    }

    [Fact]
    public void QueueGauges_ReportNothingBeforeTheFirstSuccessfulPoll()
    {
        var provider = new StubQueueSnapshotProvider();
        using var harness = new MetricsHarness(provider);

        harness.CollectObservable();

        // Never polled -> no series at all. A zero here would be a lie an alert believes.
        Assert.Empty(harness.For("nexora.extraction.queue.oldest_pending_age"));
        Assert.Empty(harness.For("nexora.extraction.queue.depth"));
    }

    [Fact]
    public void IntakeInvariantGaugesUseOnlyBoundedRedactedSnapshotFields()
    {
        var now = DateTimeOffset.UtcNow;
        var provider = new StubQueueSnapshotProvider();
        provider.Publish(ExtractionQueueSnapshot.From(new[]
        {
            new ExtractionQueueGroup(4_243, "DeadLetter", true, true, 2,
                now.UtcDateTime.AddMinutes(-12), InvariantBlocked: true),
            new ExtractionQueueGroup(4_243, "DeadLetter", true, true, 1,
                now.UtcDateTime.AddMinutes(-4), InvariantBlocked: true,
                Retry: true, RepeatedInvariantViolation: true)
        }, now));
        using var harness = new MetricsHarness(provider);

        harness.CollectObservable();

        Assert.Equal(3, Assert.Single(harness.For(
            "nexora.extraction.queue.invariant_blocked")).Value);
        Assert.Equal(720, Assert.Single(harness.For(
            "nexora.extraction.queue.oldest_invariant_blocked_age")).Value, precision: 0);
        Assert.Equal(1, Assert.Single(harness.For(
            "nexora.extraction.queue.invariant_affected_tenants")).Value);
        Assert.Equal(1, Assert.Single(harness.For(
            "nexora.extraction.queue.retries")).Value);
        Assert.Equal(1, Assert.Single(harness.For(
            "nexora.extraction.queue.repeated_invariant_violations")).Value);
    }

    // ------------------------------------------------------------ the poller

    [Fact]
    public async Task Poller_RunsOneQueryPerPoll_AndObservationsAddNone()
    {
        using var db = new TestDb();
        var provider = new ExtractionQueueSnapshotProvider();
        using var harness = new MetricsHarness(provider);

        var services = new ServiceCollection()
            .AddScoped(_ => db.ContextFor(null))
            .BuildServiceProvider();
        // Every database read the poller performs happens inside a scope it creates, so
        // counting scopes counts round-trips to the database exactly.
        var scopes = new CountingScopeFactory(services.GetRequiredService<IServiceScopeFactory>());
        var poller = new ExtractionQueueMetricsPoller(
            scopes, provider, new ExtractionQueueMetricsOptions(),
            new NoopLogger<ExtractionQueueMetricsPoller>());

        await poller.PollOnceAsync(CancellationToken.None);
        Assert.Equal(1, scopes.Created);
        Assert.Equal(1, provider.PublishCount);

        // 50 collection cycles — which is what 50 Prometheus scrapes or 50 OTLP export
        // intervals do — must not touch the database once. A gauge callback that queried
        // would make observability cost scale with the number of people watching.
        for (var i = 0; i < 50; i++) harness.CollectObservable();

        Assert.Equal(1, scopes.Created);
        Assert.Equal(1, provider.PublishCount);

        await services.DisposeAsync();
    }

    [Fact]
    public async Task Poller_ComputesOldestPendingAgeFromRealRows()
    {
        const long starving = 77_301;
        const long healthy = 77_302;
        using var db = new TestDb();

        using (var seed = db.ContextFor(null))
        {
            seed.Set<ExtractionJob>().Add(Job(starving, DateTime.UtcNow.AddHours(-2)));
            seed.Set<ExtractionJob>().Add(Job(healthy, DateTime.UtcNow.AddSeconds(-5)));
            seed.Set<ExtractionJob>().Add(Job(healthy, DateTime.UtcNow.AddSeconds(-3)));
            await seed.SaveChangesAsync();
        }

        using var ctx = db.ContextFor(null);
        var groups = await ExtractionQueueMetricsPoller.QueryAsync(ctx, DateTime.UtcNow, default);
        var snapshot = ExtractionQueueSnapshot.From(groups, DateTimeOffset.UtcNow);

        var stuck = Assert.Single(snapshot.Tenants, t => t.BusinessUnitId == starving);
        var fine = Assert.Single(snapshot.Tenants, t => t.BusinessUnitId == healthy);
        Assert.InRange(stuck.OldestPendingAgeSeconds, 7_100, 7_300);
        Assert.InRange(fine.OldestPendingAgeSeconds, 0, 120);
        Assert.Equal(1, stuck.Pending);
        Assert.Equal(2, fine.Pending);
    }

    [Fact]
    public async Task Poller_FailureMarksTheSnapshotStaleInsteadOfThrowing()
    {
        var provider = new ExtractionQueueSnapshotProvider();
        provider.Publish(ExtractionQueueSnapshot.From(
            new[] { Group(1, "Pending", 1, DateTime.UtcNow) }, DateTimeOffset.UtcNow));

        // A scope factory with no DbContext registered: resolution throws, exactly as a
        // dead connection pool would.
        var services = new ServiceCollection().BuildServiceProvider();
        var poller = new ExtractionQueueMetricsPoller(
            services.GetRequiredService<IServiceScopeFactory>(), provider,
            new ExtractionQueueMetricsOptions(), new NoopLogger<ExtractionQueueMetricsPoller>());

        await poller.PollOnceAsync(CancellationToken.None); // must not throw

        Assert.False(provider.Current.IsFresh);
        Assert.NotNull(provider.Current.Error);
        await services.DisposeAsync();
    }

    // ------------------------------------------------------ exporter selection

    [Fact]
    public void SelectExporter_ProductionWithNoOtlp_FallsBackToThePrometheusEndpoint()
    {
        var selection = ObservabilityExtensions.SelectExporter(Config(
            ("Observability:Environment", "Production")));

        Assert.Equal(ObservabilityExporter.None, selection.Exporter);
        // This is the whole fix: no collector configured no longer means no metrics.
        Assert.True(selection.PrometheusEnabled);
        Assert.False(selection.IsBlind);
        Assert.Equal("/metrics", selection.PrometheusPath);
    }

    [Fact]
    public void AddPlatformObservability_ProductionScrapeWithoutKey_FailsClosed()
    {
        var services = new ServiceCollection();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddPlatformObservability(Config(
                ("Observability:Environment", "Production"))));

        Assert.Contains(ObservabilityExtensions.PrometheusScrapeKeyEnvironmentVariable,
            exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPlatformObservability_DevelopmentScrapeWithoutKey_RemainsAvailableLocally()
    {
        var services = new ServiceCollection();
        services.AddPlatformObservability(Config(
            ("Observability:Environment", "Development")));

        using var provider = services.BuildServiceProvider();
        var selection = provider.GetRequiredService<ObservabilitySelection>();
        Assert.True(selection.PrometheusEnabled);
        Assert.False(selection.PrometheusKeyConfigured);
        Assert.NotNull(provider.GetService<NexoraPrometheusCollector>());
    }

    [Fact]
    public void SelectExporter_ValidOtlpEndpoint_UsesOtlpAndLeavesTheScrapeEndpointOff()
    {
        var selection = ObservabilityExtensions.SelectExporter(Config(
            ("Observability:Environment", "Production"),
            ("Observability:OtlpEndpoint", "http://collector.internal:4317")));

        Assert.Equal(ObservabilityExporter.Otlp, selection.Exporter);
        Assert.Equal("http://collector.internal:4317/", selection.OtlpEndpoint!.ToString());
        Assert.False(selection.PrometheusEnabled);
        Assert.False(selection.IsBlind);
    }

    [Fact]
    public void SelectExporter_InvalidOtlpEndpoint_DegradesInsteadOfThrowing()
    {
        var selection = ObservabilityExtensions.SelectExporter(Config(
            ("Observability:Environment", "Production"),
            ("Observability:OtlpEndpoint", "not-a-uri")));

        Assert.Equal(ObservabilityExporter.None, selection.Exporter);
        Assert.True(selection.OtlpValueInvalid);
        Assert.True(selection.PrometheusEnabled); // still not blind
    }

    [Fact]
    public void LogSelection_NoneConfigured_LogsAnErrorSayingNothingIsExported()
    {
        var selection = ObservabilityExtensions.SelectExporter(Config(
            ("Observability:Environment", "Production"),
            ("Observability:Prometheus:Enabled", "false")));
        Assert.True(selection.IsBlind);

        var logger = new RecordingLogger();
        ObservabilityExtensions.LogSelection(logger, selection);

        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        // Silent no-op observability is what caused this defect; the boot log must say so
        // in words an operator reading a deploy log cannot skim past.
        Assert.Contains("NO METRICS ARE LEAVING THIS PROCESS", error.Message, StringComparison.Ordinal);
        Assert.Contains(ObservabilityExtensions.OtlpEndpointEnvironmentVariable, error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LogSelection_OtlpConfigured_LogsTheEndpointAndNoError()
    {
        var selection = ObservabilityExtensions.SelectExporter(Config(
            ("Observability:Environment", "Production"),
            ("Observability:OtlpEndpoint", "http://collector.internal:4317")));

        var logger = new RecordingLogger();
        ObservabilityExtensions.LogSelection(logger, selection);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(logger.Entries,
            e => e.Message.Contains("Metrics exporter selected: Otlp", StringComparison.Ordinal));
    }

    [Fact]
    public void LogSelection_DevelopmentUnauthenticatedScrapeEndpoint_WarnsThatItExposesTenantData()
    {
        var selection = ObservabilityExtensions.SelectExporter(Config(
            ("Observability:Environment", "Development")));

        var logger = new RecordingLogger();
        ObservabilityExtensions.LogSelection(logger, selection);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("UNAUTHENTICATED", warning.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------- prometheus exposition

    [Fact]
    public void PrometheusCollector_RendersCountersAndGaugesInTextFormat()
    {
        const long businessUnitId = 77_401;
        var provider = new StubQueueSnapshotProvider();
        var now = DateTimeOffset.UtcNow;
        provider.Publish(ExtractionQueueSnapshot.From(new[]
        {
            Group(businessUnitId, "Pending", count: 2, oldest: now.UtcDateTime.AddSeconds(-42))
        }, now));

        using var collector = new NexoraPrometheusCollector();
        using var harness = new MetricsHarness(provider);
        harness.Metrics.JobEnqueued(businessUnitId);

        var exposition = collector.Scrape();

        Assert.Contains("# TYPE nexora_extraction_jobs_enqueued_total counter", exposition, StringComparison.Ordinal);
        Assert.Contains($"nexora_extraction_jobs_enqueued_total{{tenant_id=\"{businessUnitId}\"}}",
            exposition, StringComparison.Ordinal);
        // The gauge is pulled at scrape time, from the cached snapshot.
        Assert.Contains($"nexora_extraction_queue_oldest_pending_age{{tenant_id=\"{businessUnitId}\"}} 42",
            exposition, StringComparison.Ordinal);
    }

    [Fact]
    public void PrometheusCollector_SanitizesInstrumentNames()
    {
        Assert.Equal("nexora_llm_calls", NexoraPrometheusCollector.SanitizeName("nexora.llm.calls"));
        Assert.Equal("tenant_id", NexoraPrometheusCollector.SanitizeName("tenant.id"));
    }

    // ------------------------------------------------------------- LLM ledger

    [Fact]
    public void LlmSettled_EmitsTokensByDirection_AndOmitsCostWhenUnpriced()
    {
        using var harness = new MetricsHarness();

        harness.Metrics.LlmSettled(
            inputTokens: 1_200, outputTokens: 340, businessUnitId: 77_501,
            provider: "ollama", model: "deepseek-v4", providerClass: "Local",
            cost: null, currency: null);

        var tokens = harness.For("nexora.llm.tokens");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(1_200, Assert.Single(tokens, t => t.Tag("llm.direction") == "input").Value);
        Assert.Equal(340, Assert.Single(tokens, t => t.Tag("llm.direction") == "output").Value);
        Assert.Equal("deepseek-v4", tokens[0].Tag("llm.model"));
        // An unpriced call reports tokens and NO cost series — never a fabricated zero,
        // which would understate spend on every dashboard that sums it.
        Assert.Empty(harness.For("nexora.llm.cost"));
    }

    [Fact]
    public void LlmSettled_EmitsCostWhenTheTenantPolicyCarriesAPricingVersion()
    {
        using var harness = new MetricsHarness();

        harness.Metrics.LlmSettled(
            inputTokens: 1_000, outputTokens: 500, businessUnitId: 77_502,
            provider: "anthropic", model: "claude", providerClass: "External",
            cost: 0.0125m, currency: "USD");

        var cost = Assert.Single(harness.For("nexora.llm.cost"));
        Assert.Equal(0.0125d, cost.Value, precision: 6);
        Assert.Equal("USD", cost.Tag("cost.currency"));
        Assert.Equal("External", cost.Tag("llm.provider_class"));
    }

    // ------------------------------------------------------------- helpers

    private static EnqueueExtractionRequest Request(long businessUnitId, string hash) => new()
    {
        BusinessUnitId = businessUnitId,
        SourceType = ExtractionSourceType.ManualUpload,
        StoragePath = "blob://f",
        FileName = "rfq.pdf",
        FileType = "pdf",
        ContentHash = hash
    };

    private static ExtractionQueueGroup Group(
        long businessUnitId, string status, long count, DateTime oldest,
        bool ready = true, bool leaseLapsed = false)
        => new(businessUnitId, status, ready, leaseLapsed, count, oldest);

    private static ExtractionJob Job(long businessUnitId, DateTime createdOn) => new()
    {
        BatchId = Guid.NewGuid(),
        BusinessUnitId = businessUnitId,
        SourceType = ExtractionSourceType.ManualUpload,
        ContentHash = Guid.NewGuid().ToString("N"),
        StoragePath = "blob://f",
        Status = ExtractionStatus.Pending,
        Attempts = 0,
        MaxAttempts = 5,
        NextAttemptAt = createdOn,
        CreatedOn = createdOn,
        UpdatedOn = createdOn
    };

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v =>
                new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    /// <summary>
    /// Drives ONE pass of the real <see cref="ExtractionWorker"/> loop over fakes, so the
    /// assertions are about the production emission sites rather than about a re-creation
    /// of them.
    /// </summary>
    private static async Task RunWorkerOnceAsync(
        MetricsHarness harness, long businessUnitId, bool succeed,
        int attempts = 1, int maxAttempts = 5)
    {
        var tenantScope = new TenantScopeAccessor();
        var queue = new SingleJobQueue(businessUnitId, attempts, maxAttempts);
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IExtractionQueue>(queue)
            .AddSingleton<IExtractionDocumentReader>(new FixedDocumentReader(businessUnitId))
            .AddSingleton<IChunkedExtractionService>(new FixedExtractor(succeed))
            .AddSingleton<ILeadPersister>(new FixedPersister())
            .BuildServiceProvider();

        var worker = new ExtractionWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            new ExtractionWorkerOptions
            {
                WorkerCount = 1,
                LeaseDuration = TimeSpan.FromSeconds(30),
                IdlePollDelay = TimeSpan.FromMilliseconds(20)
            },
            services.GetRequiredService<ILogger<ExtractionWorker>>(),
            tenantScope,
            workerHeartbeat: null,
            metrics: harness.Metrics);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await queue.Settled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // The metric is written just after the queue transition; give the loop a beat.
            for (var i = 0; i < 100 && harness.For("nexora.extraction.job.duration").Count == 0; i++)
                await Task.Delay(20);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
            await services.DisposeAsync();
        }
    }

    private sealed class CountingScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceScopeFactory _inner;
        private int _created;

        public CountingScopeFactory(IServiceScopeFactory inner) => _inner = inner;

        public int Created => Volatile.Read(ref _created);

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref _created);
            return _inner.CreateScope();
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class RecordingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }

    /// <summary>Hands out exactly one job, then reports the queue idle. Records the
    /// terminal transition so the test can wait for it deterministically.</summary>
    private sealed class SingleJobQueue : IExtractionQueue
    {
        private readonly ExtractionJob _job;
        private int _claimed;

        public SingleJobQueue(long businessUnitId, int attempts, int maxAttempts)
            => _job = new ExtractionJob
            {
                Id = 1,
                BatchId = Guid.NewGuid(),
                BusinessUnitId = businessUnitId,
                SourceType = ExtractionSourceType.ManualUpload,
                ContentHash = "hash",
                StoragePath = "blob://f",
                Status = ExtractionStatus.Leased,
                Attempts = attempts,
                MaxAttempts = maxAttempts,
                NextAttemptAt = DateTime.UtcNow,
                LeaseExpiresAt = DateTime.UtcNow.AddSeconds(30),
                CreatedOn = DateTime.UtcNow,
                UpdatedOn = DateTime.UtcNow
            };

        public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<EnqueueResult> EnqueueAsync(EnqueueExtractionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ExtractionJob?> ClaimAsync(
            string workerId, TimeSpan leaseDuration, int perTenantCap, CancellationToken ct = default)
            => Task.FromResult(Interlocked.Exchange(ref _claimed, 1) == 0 ? _job : null);

        public Task<bool> RenewLeaseAsync(
            long jobId, string workerId, int leaseAttempt, TimeSpan leaseDuration, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> SetStatusAsync(
            long jobId, string workerId, int leaseAttempt, ExtractionStatus status, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> CompleteAsync(
            long jobId, string workerId, int leaseAttempt, long? resultLeadId, CancellationToken ct = default)
        {
            Settled.TrySetResult();
            return Task.FromResult(true);
        }

        public Task<bool> FailAsync(
            long jobId, string workerId, int leaseAttempt, string error, CancellationToken ct = default)
        {
            Settled.TrySetResult();
            return Task.FromResult(true);
        }

        public Task<bool> FailPermanentlyAsync(
            long jobId, string workerId, int leaseAttempt, string error, CancellationToken ct = default)
        {
            Settled.TrySetResult();
            return Task.FromResult(true);
        }
    }

    private sealed class FixedDocumentReader : IExtractionDocumentReader
    {
        private readonly long _businessUnitId;
        public FixedDocumentReader(long businessUnitId) => _businessUnitId = businessUnitId;

        public Task<DocumentExtractionInput> ReadAsync(ExtractionJob job, CancellationToken ct = default)
            => Task.FromResult(new DocumentExtractionInput
            {
                BusinessUnitId = _businessUnitId,
                SourceDocumentName = "rfq.pdf",
                HeaderText = "RFQ-1",
                LineItemRegions = new[] { "Item 1" }
            });
    }

    private sealed class FixedExtractor : IChunkedExtractionService
    {
        private readonly bool _succeed;
        public FixedExtractor(bool succeed) => _succeed = succeed;

        public Task<ChunkedExtractionOutcome> ExtractAsync(
            DocumentExtractionInput input, CancellationToken ct = default) => ExtractUnstructuredAsync(input, ct);

        public Task<ChunkedExtractionOutcome> ExtractUnstructuredAsync(
            DocumentExtractionInput input, CancellationToken ct = default)
            => Task.FromResult(_succeed
                ? new ChunkedExtractionOutcome
                {
                    Status = ExtractionOutcomeStatus.Ok,
                    Result = Ext.Result(Ext.Items(1, 0.95), 0.95),
                    ExpectedItemCount = 1,
                    ExtractedItemCount = 1
                }
                : new ChunkedExtractionOutcome
                {
                    Status = ExtractionOutcomeStatus.Failed,
                    Result = null,
                    ReviewReason = "All chunks failed; no data extracted."
                });

        public Task<ChunkedExtractionOutcome> ExtractStructuredAsync(
            IReadOnlyList<RfqSpreadsheetRow> rows, long businessUnitId, string sourceName,
            CancellationToken ct = default, string? documentNarrative = null) => throw new NotSupportedException();
    }

    private sealed class FixedPersister : ILeadPersister
    {
        public Task<long> PersistAsync(
            ExtractionJob job, ChunkedExtractionOutcome outcome, CancellationToken ct = default)
            => Task.FromResult(1L);

        public async Task<long?> PersistAndCompleteAsync(
            ExtractionJob job, ChunkedExtractionOutcome outcome, IExtractionQueue queue,
            string workerId, int leaseAttempt, TimeSpan leaseDuration, CancellationToken ct = default)
        {
            await queue.CompleteAsync(job.Id, workerId, leaseAttempt, 1L, ct);
            return 1L;
        }
    }
}
