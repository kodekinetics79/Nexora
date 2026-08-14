using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ERP_RFQ_Automation.HealthChecks;
using ERP_RFQ_Automation.Billing.Metering;

namespace ERP_RFQ_Automation.Extraction;

/// <summary>Tuning for the extraction worker pool. Register as a singleton (see WIRING.md).</summary>
public sealed class ExtractionWorkerOptions
{
    /// <summary>Number of concurrent claim loops. Start 4–8.</summary>
    public int WorkerCount { get; set; } = 4;

    /// <summary>Process-wide ceiling on in-flight LLM calls, independent of WorkerCount. Start 8.</summary>
    public int MaxConcurrentLlmCalls { get; set; } = 8;

    /// <summary>Max simultaneously-processing jobs per tenant (fairness / anti-monopoly).</summary>
    public int PerTenantConcurrencyCap { get; set; } = 4;

    /// <summary>Lease length. Must exceed the slowest single-document processing time.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Backoff when a loop finds no claimable work.</summary>
    public TimeSpan IdlePollDelay { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Reads and parses one immutably-stored source document into the extractor's input
/// shape. The default implementation is a text/CSV baseline; production should replace
/// it with a reader that reuses the existing PDF/OCR/DOCX/XLSX extraction and structured
/// detection (see WIRING.md).
/// </summary>
public interface IExtractionDocumentReader
{
    Task<DocumentExtractionInput> ReadAsync(ExtractionJob job, CancellationToken ct = default);
}

/// <summary>
/// Persists an extraction outcome as a single Lead + its LeadItems in one per-document
/// transaction (implicit, via a single SaveChanges over the object graph). No merging,
/// no truncation of items; NoOfLineItems == persisted item count.
/// </summary>
public interface ILeadPersister
{
    Task<long> PersistAsync(ExtractionJob job, ChunkedExtractionOutcome outcome, CancellationToken ct = default);

    /// <summary>Persist the lead graph and complete the fenced queue claim in one transaction.</summary>
    Task<long?> PersistAndCompleteAsync(
        ExtractionJob job,
        ChunkedExtractionOutcome outcome,
        IExtractionQueue queue,
        string workerId,
        int leaseAttempt,
        TimeSpan leaseDuration,
        CancellationToken ct = default);
}

/// <summary>
/// Bounded worker pool (<see cref="ExtractionWorkerOptions.WorkerCount"/> loops) that
/// claims jobs, routes them to the chunked/deterministic extractor, and persists per
/// document. A process-wide <see cref="SemaphoreSlim"/> caps concurrent LLM calls
/// regardless of worker count. Poison docs are isolated: any failure is caught,
/// recorded, rescheduled with backoff, and dead-lettered after MaxAttempts — the loop
/// itself never dies and one slow document never blocks the others.
/// </summary>
public sealed class ExtractionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ExtractionWorkerOptions _options;
    private readonly ILogger<ExtractionWorker> _log;
    private readonly ITenantScopeAccessor _tenantScope;
    private readonly SemaphoreSlim _llmGate; // process-wide LLM concurrency cap
    private readonly IExtractionWorkerHeartbeat? _workerHeartbeat;
    private readonly ERP_RFQ_Automation.Platform.Hardening.NexoraMetrics? _metrics;

    public ExtractionWorker(
        IServiceScopeFactory scopeFactory,
        ExtractionWorkerOptions options,
        ILogger<ExtractionWorker> log,
        ITenantScopeAccessor tenantScope,
        IExtractionWorkerHeartbeat? workerHeartbeat = null,
        ERP_RFQ_Automation.Platform.Hardening.NexoraMetrics? metrics = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _log = log;
        _tenantScope = tenantScope;
        _workerHeartbeat = workerHeartbeat;
        _metrics = metrics;
        _llmGate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentLlmCalls));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var count = Math.Max(1, _options.WorkerCount);
        _log.LogInformation("ExtractionWorker starting {Count} loop(s); LLM cap {Llm}, per-tenant cap {Cap}.",
            count, _options.MaxConcurrentLlmCalls, _options.PerTenantConcurrencyCap);
        _workerHeartbeat?.Beat();

        var loops = new Task[count];
        var runId = Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < count; i++)
        {
            var workerId = $"{Environment.MachineName}:{runId}:{i}";
            loops[i] = Task.Run(() => RunLoopAsync(workerId, stoppingToken), stoppingToken);
        }
        return Task.WhenAll(loops);
    }

    private async Task RunLoopAsync(string workerId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _workerHeartbeat?.Beat();
            try
            {
                var processed = await ProcessOnceAsync(workerId, ct);
                if (!processed)
                    await Task.Delay(_options.IdlePollDelay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break; // graceful shutdown
            }
            catch (Exception ex)
            {
                // A loop must never die on an unexpected error (e.g. transient DB issue).
                _log.LogError(ex, "Worker {Worker} loop error; backing off.", workerId);
                try { await Task.Delay(_options.IdlePollDelay, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Claim and process at most one job. Returns false when the queue is idle.</summary>
    private async Task<bool> ProcessOnceAsync(string workerId, CancellationToken ct)
    {
        ExtractionJob? job;
        using (var claimScope = _scopeFactory.CreateScope())
        {
            var claimQueue = claimScope.ServiceProvider.GetRequiredService<IExtractionQueue>();
            job = await claimQueue.ClaimAsync(
                workerId, _options.LeaseDuration, _options.PerTenantConcurrencyCap, ct);
        }
        if (job is null)
            return false;

        // Duration is measured from the moment the lease is held to the moment the job
        // leaves this method, so it is the number an operator cares about (how long a
        // document occupies a worker), not just model time.
        var processingStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        double ElapsedMs() => System.Diagnostics.Stopwatch.GetElapsedTime(processingStartedAt).TotalMilliseconds;

        // The push precedes the scope because ITenantContext captures the ambient tenant in its
        // CONSTRUCTOR: a scope created first would resolve a DbContext that believes in no tenant,
        // whatever is pushed afterwards.
        using var tenantScope = _tenantScope.Push(job.BusinessUnitId);
        using var scope = _scopeFactory.CreateScope();

        // Fail closed, the contract SlaSweepWorker, QuoteDeliveryDispatcher, RoutingReconciliation-
        // Worker and FinanceOutboxDispatcherService all carry and this worker did not. If the
        // DbContext did not pick the pushed scope up, everything below — the document read, the
        // extraction, the Lead + LeadItems write and the queue transitions — would run with a null
        // tenant, which makes the EF query filters no-ops AND routes the connection to
        // nexora_pipeline_app, created BYPASSRLS. Refusing the job leaves it leased; the lease
        // expires and it is reclaimed, so this defers work rather than destroying it.
        //
        // GetService, not GetRequiredService: a container with no ErpRfqAutomationContext (the
        // lease/heartbeat unit harnesses) can reach no tenant row from this scope at all — the
        // queue, the persister and the reader all take the context — so there is nothing there to
        // fail closed on. Wherever a context exists, the check is unconditional.
        var scopedContext = scope.ServiceProvider.GetService<ErpRfqAutomationContext>();
        if (scopedContext is not null && scopedContext.ScopedTenantId != job.BusinessUnitId)
        {
            throw new InvalidOperationException(
                $"Extraction job {job.Id} refused to run for BU {job.BusinessUnitId}: the DbContext "
                + $"resolved tenant {scopedContext.ScopedTenantId?.ToString() ?? "<none>"}. "
                + "Tenant scope is mandatory for this worker.");
        }

        var queue = scope.ServiceProvider.GetRequiredService<IExtractionQueue>();

        using var workCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var leaseState = new LeaseState(job.LeaseExpiresAt ?? DateTime.UtcNow);
        var initialLeaseRemaining = leaseState.ExpiresAtUtc - DateTime.UtcNow;
        if (initialLeaseRemaining <= TimeSpan.Zero)
        {
            leaseState.MarkLost();
            workCts.Cancel();
        }
        else
        {
            workCts.CancelAfter(initialLeaseRemaining);
        }
        var heartbeatTask = MaintainLeaseAsync(
            job.Id, workerId, job.Attempts, workCts, leaseState, heartbeatCts.Token);
        var workToken = workCts.Token;

        try
        {
            var reader = scope.ServiceProvider.GetRequiredService<IExtractionDocumentReader>();
            var extractor = scope.ServiceProvider.GetRequiredService<IChunkedExtractionService>();
            var persister = scope.ServiceProvider.GetRequiredService<ILeadPersister>();

            if (!await queue.SetStatusAsync(job.Id, workerId, job.Attempts, ExtractionStatus.Extracting, workToken))
            {
                LogLeaseLost(job.Id, workerId, "starting extraction");
                return true;
            }
            await MarkIntakeProcessingAsync(job, workToken);
            if (await IsNonLeadCommercialDocumentAsync(job, workToken))
            {
                if (!await queue.SetStatusAsync(job.Id, workerId, job.Attempts,
                        ExtractionStatus.Persisting, workToken))
                {
                    LogLeaseLost(job.Id, workerId, "routing a commercial document away from Lead persistence");
                    return true;
                }
                heartbeatCts.Cancel();
                await ObserveHeartbeatAsync(heartbeatTask);
                if (!await queue.CompleteAsync(job.Id, workerId, job.Attempts, null, ct))
                {
                    LogLeaseLost(job.Id, workerId, "completing non-Lead commercial intake");
                    return true;
                }
                await MarkIntakeFinalizedAsync(job, ct);
                _metrics?.JobSucceeded(ElapsedMs(), job.BusinessUnitId);
                _log.LogInformation(
                    "Job {JobId} completed as a non-Lead commercial document for tenant {BusinessUnitId}.",
                    job.Id, job.BusinessUnitId);
                return true;
            }
            var input = await reader.ReadAsync(job, workToken);
            var structured = input.IsStructured && input.StructuredRows is { Count: > 0 };
            // Only the non-structured path can be a conversational body, so the provenance
            // lookup is paid only where it can change the routing.
            var jobMetadata = structured ? null : await ReadJobMetadataAsync(job, workToken);

            ChunkedExtractionOutcome outcome;
            if (structured)
            {
                // Deterministic path bypasses the LLM entirely — no gate needed.
                outcome = await extractor.ExtractStructuredAsync(
                    input.StructuredRows!, job.BusinessUnitId, input.SourceDocumentName, workToken,
                    input.DocumentNarrative);
            }
            else if (IsProseBody(jobMetadata)
                && scope.ServiceProvider.GetService<Conversational.IConversationalExtractionService>()
                    is { } conversational)
            {
                // ING-07: a conversational email BODY. The structured RFQ prompt cannot
                // describe free prose (see ConversationalPrompt), so the body takes its own
                // extractor — under the SAME process-wide LLM concurrency gate. The document
                // path (ChunkedExtractionService) is untouched.
                await _llmGate.WaitAsync(workToken);
                try
                {
                    outcome = await conversational.ExtractAsync(
                        input, jobMetadata?.ThreadContinuation == true, workToken);
                }
                finally
                {
                    _llmGate.Release();
                }
            }
            else
            {
                // Bound total in-flight LLM calls across the whole process.
                await _llmGate.WaitAsync(workToken);
                try
                {
                    outcome = await extractor.ExtractUnstructuredAsync(input, workToken);
                }
                finally
                {
                    _llmGate.Release();
                }
            }

            if (outcome.Status == ExtractionOutcomeStatus.Failed || outcome.Result is null)
            {
                // A failure that a retry could clear (a timeout, a truncated response, a
                // provider outage) is rescheduled with backoff, as before. A failure whose
                // cause is a DECISION is not: the allow-list refusal for unstructured
                // extraction is a deterministically closed gate, and re-asking it five times
                // on exponential backoff bought an hour of pointless retries and no new
                // information. The gate itself is unchanged — this is about reporting.
                var failureReason = ComposeFailureReason(outcome, input.StructuredFallbackNote);
                var permanent = outcome.PermanentFailure;
                var errorCode = permanent && failureReason.Contains(
                        ChunkedExtractionService.AiNotAuthorizedCode, StringComparison.Ordinal)
                    ? "ai_not_authorized"
                    : "extraction_failed";

                var recorded = permanent
                    ? await queue.FailPermanentlyAsync(job.Id, workerId, job.Attempts, failureReason, workToken)
                    : await queue.FailAsync(job.Id, workerId, job.Attempts, failureReason, workToken);
                if (!recorded)
                    LogLeaseLost(job.Id, workerId, "recording extraction failure");
                else
                {
                    await MarkIntakeFailureAsync(job, errorCode, workToken, permanent: permanent);
                    RecordFailureMetrics(job, errorCode, failureReason, ElapsedMs(), permanent: permanent);
                }
                return true;
            }

            // Renew before the (potentially large) persist so a slow write isn't reclaimed.
            if (!await queue.RenewLeaseAsync(job.Id, workerId, job.Attempts, _options.LeaseDuration, workToken))
            {
                LogLeaseLost(job.Id, workerId, "renewing before persistence");
                return true;
            }
            if (!await queue.SetStatusAsync(job.Id, workerId, job.Attempts, ExtractionStatus.Persisting, workToken))
            {
                LogLeaseLost(job.Id, workerId, "starting persistence");
                return true;
            }

            // The persister takes the queue row lock and commits both durable output and
            // Succeeded together. Stop the independent heartbeat before that transaction.
            heartbeatCts.Cancel();
            await ObserveHeartbeatAsync(heartbeatTask);
            var leadId = await persister.PersistAndCompleteAsync(
                job, outcome, queue, workerId, job.Attempts, _options.LeaseDuration, ct);
            if (leadId is null)
            {
                LogLeaseLost(job.Id, workerId, "starting the atomic persistence transaction");
                return true;
            }
            await MarkIntakeFinalizedAsync(job, ct);
            _metrics?.JobSucceeded(ElapsedMs(), job.BusinessUnitId);

            _log.LogInformation(
                "Job {JobId} succeeded: lead {LeadId}, {Extracted}/{Expected} items, status {Status}.",
                job.Id, leadId.Value, outcome.ExtractedItemCount, outcome.ExpectedItemCount, outcome.Status);
            return true;
        }
        catch (OperationCanceledException) when (leaseState.IsLost && !ct.IsCancellationRequested)
        {
            LogLeaseLost(job.Id, workerId, "processing the document");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Leave the lease to expire; another worker reclaims it after shutdown.
            throw;
        }
        catch (DocumentParsingException ex)
        {
            _log.LogWarning(ex, "Document parsing stopped for tenant {BusinessUnitId}, job {JobId}.",
                job.BusinessUnitId, job.Id);
            try
            {
                var parseReason = ex is UnsupportedDocumentFormatException
                    ? "unsupported_format" : "document_parse_failed";
                if (!await queue.FailPermanentlyAsync(job.Id, workerId, job.Attempts,
                        ex.Message, CancellationToken.None))
                    LogLeaseLost(job.Id, workerId, "recording document parse failure");
                else
                {
                    await MarkIntakeFailureAsync(job, parseReason, CancellationToken.None, permanent: true);
                    // FailPermanentlyAsync dead-letters immediately, regardless of attempts.
                    RecordFailureMetrics(job, parseReason, ex.Message, ElapsedMs(), permanent: true);
                }
            }
            catch (Exception failEx)
            {
                _log.LogError(failEx, "Failed to persist document parse failure for job {JobId}.", job.Id);
            }
            return true;
        }
        catch (EvidenceIntegrityException ex)
        {
            _log.LogCritical(ex,
                "Evidence integrity incident for tenant {BusinessUnitId}, job {JobId}; source is being failed.",
                job.BusinessUnitId, job.Id);
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
                var source = await db.Set<SourceDocument>().Include(x => x.Corpus)
                    .SingleOrDefaultAsync(x => x.BusinessUnitId == job.BusinessUnitId
                        && x.ExtractionJobId == job.Id, CancellationToken.None);
                if (source is not null)
                {
                    if (source.ProcessingStatus is not (DocumentProcessingStatus.Completed or DocumentProcessingStatus.Failed))
                        source.Fail();
                    if (source.Corpus.Status is not (CorpusStatus.Completed or CorpusStatus.Failed))
                        source.Corpus.Fail();
                    await db.SaveChangesAsync(CancellationToken.None);
                }
                if (!await queue.FailAsync(job.Id, workerId, job.Attempts,
                        "Evidence integrity failure: " + ex.Message, CancellationToken.None))
                    LogLeaseLost(job.Id, workerId, "recording evidence integrity failure");
                else
                {
                    await MarkIntakeFailureAsync(job, "evidence_integrity_failure", CancellationToken.None);
                    RecordFailureMetrics(job, "evidence_integrity_failure",
                        "Evidence integrity failure: " + ex.Message, ElapsedMs());
                }
            }
            catch (Exception failEx)
            {
                _log.LogCritical(failEx, "Failed to persist evidence integrity incident for job {JobId}.", job.Id);
            }
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Job {JobId} failed; recording for retry/dead-letter.", job.Id);
            try
            {
                if (!await queue.FailAsync(job.Id, workerId, job.Attempts, ex.Message, CancellationToken.None))
                    LogLeaseLost(job.Id, workerId, "recording unexpected failure");
                else
                {
                    await MarkIntakeFailureAsync(job, "unexpected_extraction_failure", CancellationToken.None);
                    RecordFailureMetrics(job, "unexpected_extraction_failure", ex.Message, ElapsedMs());
                }
            }
            catch (Exception failEx) { _log.LogError(failEx, "Also failed to record failure for job {JobId}.", job.Id); }
            return true;
        }
        finally
        {
            heartbeatCts.Cancel();
            await ObserveHeartbeatAsync(heartbeatTask);
        }
    }

    private async Task MaintainLeaseAsync(
        long jobId,
        string workerId,
        int leaseAttempt,
        CancellationTokenSource workCts,
        LeaseState state,
        CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(1_000, _options.LeaseDuration.TotalMilliseconds / 3));
        while (!ct.IsCancellationRequested && !workCts.IsCancellationRequested)
        {
            try
            {
                var remaining = state.ExpiresAtUtc - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    state.MarkLost();
                    workCts.Cancel();
                    return;
                }
                await Task.Delay(remaining < interval ? remaining : interval, ct);
                remaining = state.ExpiresAtUtc - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    state.MarkLost();
                    workCts.Cancel();
                    return;
                }

                // The work cancellation is armed independently of the database call, so
                // a hung driver/proxy cannot let processing run beyond known ownership.
                workCts.CancelAfter(remaining);
                using var renewalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                renewalCts.CancelAfter(remaining);
                var renewalStartedAt = DateTime.UtcNow;
                using var scope = _scopeFactory.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<IExtractionQueue>();
                if (await queue.RenewLeaseAsync(
                        jobId, workerId, leaseAttempt, _options.LeaseDuration, renewalCts.Token))
                {
                    // The database computes expiry near request start. Using our own
                    // pre-call timestamp is conservative and never overstates the lease.
                    var renewedUntil = renewalStartedAt.Add(_options.LeaseDuration);
                    if (renewedUntil <= DateTime.UtcNow)
                    {
                        state.MarkLost();
                        workCts.Cancel();
                        return;
                    }
                    state.RenewedUntil(renewedUntil);
                    workCts.CancelAfter(renewedUntil - DateTime.UtcNow);
                    continue;
                }

                state.MarkLost();
                workCts.Cancel();
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                state.MarkLost();
                workCts.Cancel();
                return;
            }
            catch (Exception ex)
            {
                if (DateTime.UtcNow >= state.ExpiresAtUtc)
                {
                    state.MarkLost();
                    workCts.Cancel();
                    _log.LogError(ex,
                        "Lease heartbeat failed through the known deadline for worker {Worker}, job {JobId}; cancelling work.",
                        workerId, jobId);
                    return;
                }

                _log.LogError(ex, "Lease heartbeat failed for worker {Worker}, job {JobId}; retrying before expiry.", workerId, jobId);
                var retryDelay = state.ExpiresAtUtc - DateTime.UtcNow;
                if (retryDelay > TimeSpan.FromSeconds(1))
                    retryDelay = TimeSpan.FromSeconds(1);
                if (retryDelay > TimeSpan.Zero)
                    await Task.Delay(retryDelay, ct);
            }
        }
    }

    private async Task MarkIntakeProcessingAsync(ExtractionJob job, CancellationToken ct)
    {
        if (!job.SourceDocumentOccurrenceId.HasValue) return;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        // PostgreSQL synchronizes the occurrence in the same statement transaction as
        // the fenced job transition. The portable provider retains this explicit path.
        if (db.Database.IsNpgsql()) return;
        var occurrence = await db.Set<SourceDocumentOccurrence>()
            .SingleAsync(x => x.BusinessUnitId == job.BusinessUnitId && x.Id == job.SourceDocumentOccurrenceId, ct);
        if (occurrence.IntakeStatus is IntakeOccurrenceStatus.Queued or IntakeOccurrenceStatus.Retryable)
        {
            occurrence.MarkProcessing();
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<bool> IsNonLeadCommercialDocumentAsync(ExtractionJob job, CancellationToken ct)
    {
        if (!job.SourceDocumentOccurrenceId.HasValue) return false;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var sourceMetadata = await db.Set<SourceDocumentOccurrence>().AsNoTracking()
            .Where(x => x.BusinessUnitId == job.BusinessUnitId && x.Id == job.SourceDocumentOccurrenceId.Value)
            .Select(x => x.SourceMetadataJson)
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(sourceMetadata)) return false;
        try
        {
            using var document = JsonDocument.Parse(sourceMetadata);
            if (!document.RootElement.TryGetProperty("metadata", out var metadata) ||
                metadata.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
                !metadata.TryGetProperty(nameof(ExtractionJobMetadata.CommercialDocumentTypeHint), out var hint))
                return false;
            return ExtractionJobMetadata.IsNonLeadCommercialType(hint.GetString());
        }
        catch (JsonException exception)
        {
            _log.LogWarning(exception, "Commercial intake metadata for job {JobId} is invalid; failing closed.", job.Id);
            return true;
        }
    }

    /// <summary>ING-07: true when the intake door marked this job's payload as conversational
    /// prose (an email body), which routes it to the conversational extractor.</summary>
    private static bool IsProseBody(ExtractionJobMetadata? metadata)
        => string.Equals(metadata?.BodyShape, "prose", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Bound on the per-chunk diagnostics digest appended to a failure reason. The queue
    /// stores LastError truncated at 4,000 characters and truncation keeps the START of
    /// the string, so the digest is capped separately to guarantee the summary reason is
    /// never the part that gets cut off.
    /// </summary>
    private const int MaxDiagnosticsDigestChars = 3_400;

    /// <summary>
    /// Composes the failure reason persisted into <c>ExtractionJobs.LastError</c>. The
    /// summary reason comes first (prefixed with the reader's structured-fallback note
    /// when present, unchanged), followed by the extractor's collected per-chunk
    /// diagnostics. Those diagnostics used to be dropped here, so a dead-lettered job
    /// said only "All chunks failed; no data extracted" while the ledger showed every
    /// call succeeding — the stored error must instead say which stage failed and why.
    /// </summary>
    internal static string ComposeFailureReason(
        ChunkedExtractionOutcome outcome, string? structuredFallbackNote)
    {
        var reason = outcome.ReviewReason ?? "Extraction produced no usable result.";
        if (!string.IsNullOrWhiteSpace(structuredFallbackNote))
            reason = $"{structuredFallbackNote} {reason}";

        var details = outcome.Diagnostics
            .Where(d => !string.IsNullOrWhiteSpace(d)
                && !string.Equals(d, outcome.ReviewReason, StringComparison.Ordinal))
            .ToList();
        if (details.Count == 0)
            return reason;

        var digest = string.Join(" | ", details);
        if (digest.Length > MaxDiagnosticsDigestChars)
            digest = digest[..MaxDiagnosticsDigestChars] + "…";
        return $"{reason} [diagnostics: {digest}]";
    }

    /// <summary>
    /// Best-effort read of the job's ingest provenance: the database-owned occurrence record
    /// first, the file sidecar as the pre-database-provenance fallback. Never throws — a job
    /// with unreadable metadata simply keeps the default (document) routing.
    /// </summary>
    private async Task<ExtractionJobMetadata?> ReadJobMetadataAsync(ExtractionJob job, CancellationToken ct)
    {
        try
        {
            if (job.SourceDocumentOccurrenceId.HasValue)
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
                var json = await db.Set<SourceDocumentOccurrence>().AsNoTracking()
                    .Where(x => x.BusinessUnitId == job.BusinessUnitId
                        && x.Id == job.SourceDocumentOccurrenceId.Value)
                    .Select(x => x.SourceMetadataJson)
                    .SingleOrDefaultAsync(ct);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.TryGetProperty("metadata", out var element)
                        && element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                        return element.Deserialize<ExtractionJobMetadata>();
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Ingest metadata for job {JobId} is unreadable; using document routing.", job.Id);
            return null;
        }

        return ExtractionJobMetadata.TryLoad(job);
    }

    /// <summary>
    /// The terminal-for-this-attempt classification an operator reads on the intake record.
    /// A code with no first-class state leaves the occurrence's outcome alone rather than
    /// inventing one — "we do not know" is a real answer and is already the NONE default.
    /// </summary>
    private static IngestionOutcomeState? OutcomeStateFor(string errorCode) => errorCode switch
    {
        "unsupported_format" => IngestionOutcomeState.UNSUPPORTED_FORMAT,
        "ai_not_authorized" => IngestionOutcomeState.AI_NOT_AUTHORIZED,
        _ => null
    };

    /// <summary>
    /// Records the intake side of a failed attempt.
    ///
    /// <para>
    /// This used to begin <c>if (db.Database.IsNpgsql()) return;</c> — a blanket early return
    /// that meant NO intake occurrence was ever annotated in production. The guard was not
    /// arbitrary: on PostgreSQL <c>trg_release01c_sync_intake_from_job</c> on
    /// <c>"ExtractionJobs"</c> (function last replaced by migration 20260730193414) owns
    /// <c>intake_status</c>, <c>last_error_category</c>,
    /// <c>last_error_code</c> and <c>last_error_details</c>, deriving them from the job row,
    /// and having the application write them too would race the trigger and could rewind it.
    /// </para>
    /// <para>
    /// But that trigger does NOT write <c>outcome_state</c>, and never has. So the blanket
    /// return was too wide by exactly one column: the WHY of a failure was dropped on the
    /// floor for every production document. The guard is now scoped to the columns the
    /// trigger actually owns, and the outcome state — the only part nothing else writes — is
    /// recorded on both dialects.
    /// </para>
    /// <para>
    /// The whole body is best-effort. The queue row is already durable by the time this runs,
    /// and an annotation that threw would be caught by the caller's general handler and turned
    /// into a second failure recording for a job that has already been failed.
    /// </para>
    /// </summary>
    private async Task MarkIntakeFailureAsync(
        ExtractionJob job,
        string errorCode,
        CancellationToken ct,
        bool permanent = false)
    {
        if (!job.SourceDocumentOccurrenceId.HasValue) return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            var occurrence = await db.Set<SourceDocumentOccurrence>()
                .SingleAsync(x => x.BusinessUnitId == job.BusinessUnitId && x.Id == job.SourceDocumentOccurrenceId, ct);

            // Never overwrite a classification something else already made — a malware or
            // duplicate disposition outranks anything decided here.
            if (occurrence.OutcomeState == IngestionOutcomeState.NONE
                && OutcomeStateFor(errorCode) is { } state)
                occurrence.MarkOutcome(state);

            if (!db.Database.IsNpgsql() && occurrence.IntakeStatus == IntakeOccurrenceStatus.Processing)
            {
                if (permanent || job.Attempts >= job.MaxAttempts) occurrence.MarkDeadLetter(errorCode);
                else occurrence.MarkRetryable(errorCode);
            }

            if (db.ChangeTracker.HasChanges())
                await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Job {JobId} was failed as '{ErrorCode}' but its intake occurrence could not be annotated. "
                + "The queue row is authoritative; the intake record understates the reason.",
                job.Id, errorCode);
        }
    }

    private async Task MarkIntakeFinalizedAsync(ExtractionJob job, CancellationToken ct)
    {
        if (!job.SourceDocumentOccurrenceId.HasValue) return;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        if (db.Database.IsNpgsql()) return;
        var occurrence = await db.Set<SourceDocumentOccurrence>()
            .SingleAsync(x => x.BusinessUnitId == job.BusinessUnitId && x.Id == job.SourceDocumentOccurrenceId, ct);
        if (occurrence.IntakeStatus is IntakeOccurrenceStatus.Processing or IntakeOccurrenceStatus.Retryable)
        {
            var awaitsReview = await db.Set<ERP_RFQ_Automation.LeadIdentity.LeadIngestionOccurrence>()
                .AsNoTracking().AnyAsync(x => x.BusinessUnitId == job.BusinessUnitId
                    && x.SourceDocumentOccurrenceId == occurrence.Id
                    && x.Classification == ERP_RFQ_Automation.LeadIdentity.LeadOccurrenceClassification.PossibleMatchReviewRequired, ct);
            if (awaitsReview) occurrence.MarkReviewRequired();
            else occurrence.MarkResolved();
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task ObserveHeartbeatAsync(Task heartbeat)
    {
        try { await heartbeat; }
        catch (OperationCanceledException) { }
    }

    private sealed class LeaseState
    {
        private int _lost;
        private long _expiresAtTicks;

        public LeaseState(DateTime expiresAtUtc) => RenewedUntil(expiresAtUtc);

        public bool IsLost => Volatile.Read(ref _lost) != 0;
        public DateTime ExpiresAtUtc => new(Interlocked.Read(ref _expiresAtTicks), DateTimeKind.Utc);
        public void RenewedUntil(DateTime expiresAtUtc)
            => Interlocked.Exchange(ref _expiresAtTicks, expiresAtUtc.ToUniversalTime().Ticks);
        public void MarkLost() => Interlocked.Exchange(ref _lost, 1);
    }

    private void LogLeaseLost(long jobId, string workerId, string operation)
    {
        _log.LogWarning(
            "Worker {Worker} lost or expired its lease for job {JobId} while {Operation}; stale transition was fenced.",
            workerId, jobId, operation);
        // The tenant comes from the ambient scope pushed at the top of ProcessOnceAsync,
        // which every call site here runs inside. The stage string is a fixed literal at
        // each call site, so the tag domain is closed. Neither the job id nor the worker
        // id is tagged — both are unbounded.
        _metrics?.LeaseLost(operation, _tenantScope.BusinessUnitId);
    }

    /// <summary>
    /// Records a terminal-for-this-attempt failure. A job that has spent its attempts is
    /// ALSO a dead-letter arrival, categorised through the same closed vocabulary the
    /// dead-letter API reports, so the counter and the operator queue agree.
    /// </summary>
    private void RecordFailureMetrics(
        ExtractionJob job, string reason, string? errorText, double durationMs, bool permanent = false)
    {
        _metrics?.JobFailed(reason, job.BusinessUnitId, durationMs);
        if (permanent || job.Attempts >= job.MaxAttempts)
            _metrics?.JobDeadLettered(
                ExtractionDeadLetterService.ClassifyFailure(errorText), job.BusinessUnitId);
    }
}

/// <summary>
/// Baseline text/CSV document reader. Reads the immutably-stored file, treats non-empty
/// lines as line-item regions, and parses .csv into structured rows for the deterministic
/// path. Replace with a production reader that reuses the existing PDF/OCR/DOCX/XLSX
/// extractors and real structured detection (see WIRING.md).
/// </summary>
public sealed class DefaultExtractionDocumentReader : IExtractionDocumentReader
{
    private readonly ILogger<DefaultExtractionDocumentReader> _log;

    public DefaultExtractionDocumentReader(ILogger<DefaultExtractionDocumentReader> log) => _log = log;

    public async Task<DocumentExtractionInput> ReadAsync(ExtractionJob job, CancellationToken ct = default)
    {
        var name = job.FileName ?? Path.GetFileName(job.StoragePath);
        string text;
        try
        {
            text = File.Exists(job.StoragePath)
                ? await File.ReadAllTextAsync(job.StoragePath, Encoding.UTF8, ct)
                : string.Empty;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read stored file {Path}.", job.StoragePath);
            text = string.Empty;
        }

        var ext = (job.FileType ?? Path.GetExtension(job.StoragePath)).TrimStart('.').ToLowerInvariant();
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();

        if (ext == "csv" && lines.Count > 1)
        {
            var rows = ParseCsv(lines, name);
            if (rows.Count > 0)
            {
                return new DocumentExtractionInput
                {
                    BusinessUnitId = job.BusinessUnitId,
                    SourceId = $"job:{job.Id}",
                    // The lease attempt scopes every AI idempotency key this pass issues,
                    // so a retried job makes NEW governed requests (see AttemptNumber).
                    AttemptNumber = Math.Max(1, job.Attempts),
                    ExtractionJobId = job.Id,
                    SourceDocumentOccurrenceId = job.SourceDocumentOccurrenceId,
                    SourceDocumentName = name,
                    ProcessingPath = ExtractionProcessingPath.DeterministicRules,
                    IsStructured = true,
                    StructuredRows = rows,
                    HeaderText = string.Join('\n', lines.Take(1)),
                    LineItemRegions = rows.Select(r => r.ProductName ?? "").ToList()
                };
            }
        }

        // Unstructured baseline: top slice as header context, remaining lines as item regions.
        var headerLineCount = Math.Min(20, lines.Count);
        var header = string.Join('\n', lines.Take(headerLineCount));
        var regions = lines.Skip(headerLineCount).ToList();
        if (regions.Count == 0 && lines.Count > 0)
            regions = lines; // whole-doc pass

        return new DocumentExtractionInput
        {
            BusinessUnitId = job.BusinessUnitId,
            SourceId = $"job:{job.Id}",
            // The lease attempt scopes every AI idempotency key this pass issues, so a
            // retried job makes NEW governed requests (see AttemptNumber).
            AttemptNumber = Math.Max(1, job.Attempts),
            ExtractionJobId = job.Id,
            SourceDocumentOccurrenceId = job.SourceDocumentOccurrenceId,
            SourceDocumentName = name,
            ProcessingPath = ExtractionProcessingPath.NativeParser,
            IsStructured = false,
            HeaderText = header,
            LineItemRegions = regions
        };
    }

    private static List<RfqSpreadsheetRow> ParseCsv(List<string> lines, string name)
    {
        var headers = SplitCsv(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToArray();
        int Idx(params string[] keys) => Array.FindIndex(headers, h => keys.Contains(h));
        var iRfq = Idx("rfqno", "rfq no", "rfq");
        var iBuyer = Idx("buyername", "buyer name", "buyer");
        var iRecv = Idx("receiveddate", "received date");
        var iBid = Idx("bidclosingdate", "bid closing date");
        var iProduct = Idx("productname", "product name", "product");
        var iQty = Idx("quantity", "qty");
        var iPrice = Idx("unitprice", "unit price", "price");
        var iCurr = Idx("currency");
        var iMfr = Idx("manufacturername", "manufacturer");
        var iMpn = Idx("manufacturerpartnumber", "mpn", "part number");
        var iLead = Idx("leadtimedays", "lead time", "leadtime");

        string? Cell(string[] cells, int i) => i >= 0 && i < cells.Length ? cells[i].Trim() : null;

        var rows = new List<RfqSpreadsheetRow>();
        for (var r = 1; r < lines.Count; r++)
        {
            var cells = SplitCsv(lines[r]);
            rows.Add(new RfqSpreadsheetRow
            {
                RowNumber = r + 1,
                SourceDocumentName = name,
                RfqNo = Cell(cells, iRfq),
                BuyerName = Cell(cells, iBuyer),
                ReceivedDate = Cell(cells, iRecv),
                BidClosingDate = Cell(cells, iBid),
                ProductName = Cell(cells, iProduct),
                Quantity = Cell(cells, iQty),
                UnitPrice = Cell(cells, iPrice),
                Currency = Cell(cells, iCurr),
                ManufacturerName = Cell(cells, iMfr),
                ManufacturerPartNumber = Cell(cells, iMpn),
                LeadTimeDays = Cell(cells, iLead)
            });
        }
        return rows;
    }

    // Minimal RFC-4180-ish splitter (handles quoted fields + escaped quotes).
    private static string[] SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result.ToArray();
    }
}

/// <summary>
/// Default persister: one Lead + its LeadItems written in a single SaveChanges over the
/// object graph (EmailIngest -> Lead -> LeadItems), i.e. one implicit per-document
/// transaction. Change tracking is disabled during the add for throughput on large item
/// sets. NeedsReview outcomes are still persisted (never dropped) but flagged.
/// </summary>
public sealed class LeadPersister : ILeadPersister
{
    private readonly ErpRfqAutomationContext _context;
    private readonly ILogger<LeadPersister> _log;
    private readonly ERP_RFQ_Automation.Deduplication.ILeadDuplicateDetector? _duplicateDetector;
    private readonly ERP_RFQ_Automation.CommercialRouting.ICommercialRoutingApplicationService? _routing;
    private readonly ERP_RFQ_Automation.LeadIdentity.ILeadIdentityApplicationService? _leadIdentity;
    /// <summary>Present once email assembly is wired; absent leaves non-email ingestion untouched.</summary>
    private readonly ERP_RFQ_Automation.Ingestion.Assembly.IEmailInquiryAssemblyCoordinator? _emailAssemblies;
    private readonly ERP_RFQ_Automation.CustomerResolution.ILeadCustomerResolutionService? _customerResolution;
    private readonly UsageMeteringService? _usageMetering;

    // The detector is optional so persistence keeps working before (and without)
    // the Deduplication DI registration (see Deduplication/DEDUP-WIRING.md).
    public LeadPersister(
        ErpRfqAutomationContext context,
        ILogger<LeadPersister> log,
        ERP_RFQ_Automation.Deduplication.ILeadDuplicateDetector? duplicateDetector = null,
        ERP_RFQ_Automation.CommercialRouting.ICommercialRoutingApplicationService? routing = null,
        ERP_RFQ_Automation.LeadIdentity.ILeadIdentityApplicationService? leadIdentity = null,
        ERP_RFQ_Automation.Ingestion.Assembly.IEmailInquiryAssemblyCoordinator? emailAssemblies = null,
        ERP_RFQ_Automation.CustomerResolution.ILeadCustomerResolutionService? customerResolution = null,
        UsageMeteringService? usageMetering = null)
    {
        _context = context;
        _log = log;
        _duplicateDetector = duplicateDetector;
        _routing = routing;
        _leadIdentity = leadIdentity;
        _emailAssemblies = emailAssemblies;
        _customerResolution = customerResolution;
        _usageMetering = usageMetering;
    }

    public Task<long> PersistAsync(
        ExtractionJob job, ChunkedExtractionOutcome outcome, CancellationToken ct = default)
        => PersistInternalAsync(job, outcome, enrichAfterPersistence: true, ct);

    private async Task<long> PersistInternalAsync(
        ExtractionJob job,
        ChunkedExtractionOutcome outcome,
        bool enrichAfterPersistence,
        CancellationToken ct)
    {
        if (outcome.Result is null)
            throw new InvalidOperationException("Cannot persist a null extraction result.");

        // ---- ASSEMBLY SAFETY FENCE -------------------------------------------------------
        //
        // A job that belongs to an email inquiry component must NEVER reach the per-job Lead
        // reconciliation below. That path creates one Lead per document, which is the exact
        // defect the message-level barrier exists to remove: a body finishing before its
        // attachment would mint a Lead priced without the attachment.
        //
        // The signal is the PERSISTED component row joined on this job id — not the metadata
        // sidecar, which is best-effort by its own contract and therefore cannot be trusted to
        // withhold a commercial action.
        //
        // Until the barrier handler is wired, the component's outcome is recorded and the
        // message is left visibly at its assembly state. That is deliberate: the work is not
        // lost, the operator can see exactly where it stopped, and no Lead is invented from a
        // fragment. Non-email and manual-upload jobs never match this query and keep their
        // existing behaviour untouched.
        if (_emailAssemblies is not null
            && _context.Model.FindEntityType(typeof(ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponent)) is not null)
        {
            var assemblyComponent = await _context
                .Set<ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponent>()
                .AsNoTracking()
                .Where(x => x.BusinessUnitId == job.BusinessUnitId && x.ExtractionJobId == job.Id)
                .Select(x => new { x.AssemblyId, x.ComponentKey })
                .FirstOrDefaultAsync(ct);

            if (assemblyComponent is not null)
            {
                // HELD, NOT COMPLETED — and the distinction is the whole point.
                //
                // The first version of this fence marked the component Completed and returned,
                // discarding outcome.Result. That is silent result loss dressed as success: the
                // extraction really ran, really cost money, and its output went nowhere, while
                // the barrier would later see a Completed component carrying nothing and
                // assemble a Lead from the parts that happened to survive.
                //
                // There is no durable extraction-result store in this repository yet — the
                // result has only ever flowed straight into Lead creation — so it CANNOT be
                // persisted here without inventing one. Until the component result store and
                // the barrier handler land, the honest outcome is a visible recoverable hold:
                // the work is re-runnable, the operator can see exactly where it stopped, and
                // nothing is claimed to have finished that did not.
                await _emailAssemblies.RecordComponentOutcomeAsync(
                    job.BusinessUnitId, assemblyComponent.ComponentKey,
                    ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponentStatus.FailedRecoverable,
                    "assembly_result_store_pending",
                    "This part was read successfully, but the message-level assembly that "
                    + "combines it with the rest of the email is not available yet. It will be "
                    + "processed again automatically.",
                    job.SourceDocumentOccurrenceId, ct);

                _log.LogWarning(
                    "Extraction job {ExtractionJobId} is component {ComponentKey} of email "
                    + "assembly {AssemblyId} (business unit {BusinessUnitId}). Extraction "
                    + "succeeded but there is no durable component-result store yet, so the "
                    + "component is HELD as recoverable rather than completed. No Lead is "
                    + "created per component.",
                    job.Id, assemblyComponent.ComponentKey, assemblyComponent.AssemblyId,
                    job.BusinessUnitId);

                return 0;
            }

            // FAIL CLOSED. An Email-sourced job with no unambiguous component mapping must not
            // fall through to per-document Lead reconciliation: that is precisely the path that
            // mints one Lead per attachment. A missing mapping means the scheduler and the
            // worker disagree about who owns this job, which is an ownership failure to be seen
            // and repaired — never a licence to create commercial records.
            //
            // Non-email ingestion is untouched: manual upload and watched folders are not
            // ExtractionSourceType.Email and never reach this branch.
            if (job.SourceType == ExtractionSourceType.Email)
            {
                _log.LogError(
                    "Email extraction job {ExtractionJobId} (business unit {BusinessUnitId}, "
                    + "batch {BatchId}) has no authoritative email inquiry component. It is NOT "
                    + "reconciled into a Lead; the ownership failure is recoverable and visible.",
                    job.Id, job.BusinessUnitId, job.BatchId);
                return 0;
            }
        }
        // ---- END ASSEMBLY SAFETY FENCE ---------------------------------------------------

        // Multi-inquiry auto-split: N per-group results (a strict partition of the merged
        // items) persist as N Leads sharing ONE EmailIngest + the same source document.
        var results = outcome.SplitResults is { Count: > 1 }
            ? outcome.SplitResults
            : new List<LeadExtractionResult> { outcome.Result };

        // Ingest provenance sidecar (email door): real from/subject + a PRE-CREATED
        // EmailIngest id. Best-effort — a missing/corrupt sidecar falls back to the
        // synthetic-ingest behavior below.
        var metadata = await ResolveMetadataAsync(job, ct);

        var now = DateTime.UtcNow;
        // AI-derived commercial facts always require a human approval before promotion,
        // regardless of model confidence. Confidence still explains why review is needed.
        var parseStatus = "NeedsReview";
        var ingest = await ResolveIngestAsync(job, metadata, parseStatus, now, ct);

        var reviewNote = outcome.Status == ExtractionOutcomeStatus.NeedsReview
            ? $"[NEEDS REVIEW] {outcome.ReviewReason} "
            : string.Empty;
        // Email parity: legacy leads carried "Email: From X, Subject: Y" in the remarks.
        var sourceNote = metadata?.FromEmail != null || metadata?.Subject != null
            ? $"Email: From {metadata?.FromEmail}, Subject: {metadata?.Subject}. "
            : string.Empty;

        var leads = new List<Lead>(results.Count);
        for (var g = 0; g < results.Count; g++)
        {
            var splitNote = results.Count > 1
                ? $"Split from a multi-inquiry document (group {g + 1} of {results.Count}). "
                : string.Empty;
            leads.Add(BuildLead(job, metadata, results[g], ingest, now,
                $"{reviewNote}{splitNote}{sourceNote}"));
        }

        var reconciliation = new List<ERP_RFQ_Automation.LeadIdentity.LeadReconciliationResult>(leads.Count);
        if (_leadIdentity is not null)
        {
            long? sourceDocumentId = null;
            string? logicalGroupKey = null;
            // Document identity for FR-RFQ-06. The evidence ledger already holds the real
            // detected MIME type and byte size of these exact bytes; both used to be passed as
            // literal null, leaving the duplicate-detection signals empty on every occurrence.
            string? detectedMimeType = null;
            long? fileSize = null;
            if (_context.Model.FindEntityType(typeof(SourceDocument)) is not null)
            {
                var sourceDocument = await _context.Set<SourceDocument>()
                    .Where(x => x.BusinessUnitId == job.BusinessUnitId && x.ContentHash == job.ContentHash)
                    .Select(x => new { x.Id, x.DetectedMimeType, x.ByteSize })
                    .SingleOrDefaultAsync(ct);
                sourceDocumentId = sourceDocument?.Id;
                detectedMimeType = sourceDocument?.DetectedMimeType;
                fileSize = sourceDocument?.ByteSize;
                if (job.SourceDocumentOccurrenceId.HasValue)
                    logicalGroupKey = await _context.Set<SourceDocumentOccurrence>()
                        .Where(x => x.BusinessUnitId == job.BusinessUnitId && x.Id == job.SourceDocumentOccurrenceId)
                        .Select(x => x.LogicalGroupKey).SingleOrDefaultAsync(ct);
            }
            // Message identity for FR-RFQ-06. The mail door records the RFC-822 Message-Id on the
            // EmailIngest row it creates and as the job's logical group key ("email:{Message-Id}").
            // That id is a LOWER BOUND on thread identity: documents sharing it are provably from
            // one message and therefore one thread, so it can only ever miss a relationship, never
            // invent one. A reply carries its own Message-Id and will NOT match — real
            // In-Reply-To/References threading needs the mail door to persist the header chain.
            // Manual upload and watched folders have no mail message at all, so this stays null
            // rather than carrying a manufactured identity that the scorer would read as evidence.
            var emailThreadId = job.SourceType != ExtractionSourceType.Email
                ? null
                : metadata?.LogicalGroupKey is { Length: > 0 } messageGroupKey
                    && messageGroupKey.StartsWith("email:", StringComparison.Ordinal)
                    ? messageGroupKey
                    : ingest?.MessageId is { Length: > 0 } messageId ? $"email:{messageId}" : null;
            var externalRequests = await _context.Set<ERP_RFQ_Automation.AI.AiRequest>()
                .AsNoTracking()
                .Where(x => x.BusinessUnitId == job.BusinessUnitId && x.ExtractionJobId == job.Id
                    && x.ProviderClass == ERP_RFQ_Automation.AI.AiProviderClass.External)
                .ToListAsync(ct);
            var attributedExternalCost = ProcessingCostAttribution.Summarize(externalRequests).Amount;
            for (var i = 0; i < leads.Count; i++)
            {
                var path = outcome.CanonicalImport is not null
                    ? ERP_RFQ_Automation.LeadIdentity.LeadProcessingPath.Deterministic
                    : outcome.AiProviderClass == ERP_RFQ_Automation.AI.AiProviderClass.Local
                        ? ERP_RFQ_Automation.LeadIdentity.LeadProcessingPath.LocalModel
                        : ERP_RFQ_Automation.LeadIdentity.LeadProcessingPath.ExternalModel;
                reconciliation.Add(await _leadIdentity.ReconcileAsync(leads[i],
                    new ERP_RFQ_Automation.LeadIdentity.LeadIntakeDescriptor(
                        job.BatchId, job.SourceType.ToString(),
                        $"extraction:{job.BusinessUnitId}:{job.Id}:inquiry:{i + 1}",
                        results.Count > 1 && metadata?.SourceOccurrenceId is { } sourceOccurrenceId
                            ? $"{sourceOccurrenceId}:inquiry:{i + 1}" : metadata?.SourceOccurrenceId,
                        Truncate(emailThreadId, 512), job.SourceType.ToString(), metadata?.FromEmail,
                        metadata?.Subject, job.FileName, Truncate(detectedMimeType, 255), fileSize, job.ContentHash, sourceDocumentId, job.Id,
                        metadata?.SourceReceivedAtUtc, DateTimeOffset.UtcNow, path, path == ERP_RFQ_Automation.LeadIdentity.LeadProcessingPath.ExternalModel,
                        attributedExternalCost, "Service", "extraction-worker", $"extraction:{job.Id}")
                    {
                        SourceDocumentOccurrenceId = job.SourceDocumentOccurrenceId,
                        LogicalGroupKey = logicalGroupKey ?? metadata?.LogicalGroupKey
                    }, ct));
            }
            if (job.SourceDocumentOccurrenceId.HasValue)
            {
                var intakeOccurrence = await _context.Set<SourceDocumentOccurrence>()
                    .SingleAsync(x => x.BusinessUnitId == job.BusinessUnitId
                                      && x.Id == job.SourceDocumentOccurrenceId.Value, ct);
                var state = reconciliation.All(x => x.Classification == ERP_RFQ_Automation.LeadIdentity.LeadOccurrenceClassification.ExactDuplicate)
                    ? IngestionOutcomeState.BUSINESS_DUPLICATE_CONFIRMED
                    : reconciliation.Any(x => x.Classification == ERP_RFQ_Automation.LeadIdentity.LeadOccurrenceClassification.Revision)
                        ? IngestionOutcomeState.REVISION
                        : reconciliation.Any(x => x.Classification == ERP_RFQ_Automation.LeadIdentity.LeadOccurrenceClassification.PossibleMatchReviewRequired)
                            ? IngestionOutcomeState.POSSIBLE_MATCH
                            : IngestionOutcomeState.NONE;
                intakeOccurrence.MarkOutcome(state);
                intakeOccurrence.RecordActualCost(0m, attributedExternalCost ?? 0m,
                    attributedExternalCost.HasValue ? "RECORDED" : "LOCAL_COMPUTE_UNPRICED");
                await _context.SaveChangesAsync(ct);
            }
            var canonicalIds = reconciliation.Where(x => x.LeadId > 0).Select(x => x.LeadId).Distinct().ToArray();
            leads = await _context.Leads.Include(x => x.LeadItems).Where(x => canonicalIds.Contains(x.Id)).ToListAsync(ct);
        }
        else
        {
            var autoDetect = _context.ChangeTracker.AutoDetectChangesEnabled;
            _context.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                foreach (var lead in leads) _context.Add(lead);
                await _context.SaveChangesAsync(ct);
            }
            finally { _context.ChangeTracker.AutoDetectChangesEnabled = autoDetect; }
        }

        if (outcome.CanonicalImport is not null && leads.Count == results.Count)
        {
            var evidencePersister = new StructuredEvidenceLedgerPersister(_context);
            await evidencePersister.PersistAsync(job, outcome, leads, ct);
        }
        else
        {
            // Lightweight provider tests intentionally use a reduced model without the
            // evidence ledger. Production PostgreSQL contexts always include it.
            if (_context.Model.FindEntityType(typeof(SourceDocument)) is not null)
                await PersistUnstructuredRunAsync(job, outcome, ct);
        }

        _log.LogInformation(
            "Persisted {LeadCount} lead(s) ({LeadIds}) with {Count} item(s) total from job {JobId}.",
            leads.Count, string.Join(",", leads.Select(l => l.Id)),
            leads.Sum(l => l.LeadItems.Count), job.Id);

        // Evidence linkage: every produced lead gets an Attachment row pointing at the
        // immutable source document (shared across split leads). Best-effort — an
        // attachment failure must never fail the persistence.
        await TryAttachSourceDocumentAsync(job, leads, now, ct);

        // WP-A3: duplicate detection AFTER the save, PER produced lead. Best-effort by
        // contract — a detection failure must never fail (or roll back) the persistence.
        if (enrichAfterPersistence && _leadIdentity is null)
            await TryDetectDuplicatesAsync(job, leads, ct);

        // Every extracted lead enters the governed routing flow. Matching may assign
        // an effective owner or create one durable unassigned work item. Routing is
        // idempotent per job/lead; a transient routing failure cannot duplicate the
        // commercial record and can be replayed through the routing API/reconciler.
        if (enrichAfterPersistence)
        {
            var routeIds = _leadIdentity is null ? leads.Select(x => x.Id).ToHashSet()
                : reconciliation.Where(x => x.ShouldRoute).Select(x => x.LeadId).ToHashSet();
            // Client identity BEFORE routing, and AFTER reconciliation/persistence — the
            // ordering is load bearing, see TryResolveCustomersAsync.
            await TryResolveCustomersAsync(job, leads.Where(x => routeIds.Contains(x.Id)), ct);
            await TryRouteLeadsAsync(job, leads.Where(x => routeIds.Contains(x.Id)), ct);
        }

        return reconciliation.Count > 0 ? reconciliation[0].LeadId : leads[0].Id;
    }

    private async Task PersistUnstructuredRunAsync(
        ExtractionJob job, ChunkedExtractionOutcome outcome, CancellationToken ct)
    {
        var source = await _context.Set<SourceDocument>()
            .Include(x => x.Corpus)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == job.BusinessUnitId
                                       && x.ContentHash == job.ContentHash, ct)
            ?? throw new InvalidOperationException(
                $"Extraction job {job.Id} has no authoritative source-document record.");
        if (source.SecurityStatus != DocumentSecurityStatus.Cleared)
            throw new InvalidOperationException($"Source document {source.Id} has not passed security inspection.");

        var existing = await _context.Set<ExtractionRun>().AnyAsync(x =>
            x.BusinessUnitId == job.BusinessUnitId && x.ExtractionJobId == job.Id
            && x.AttemptNumber == job.Attempts, ct);
        if (existing)
            return;

        var ownsSourceLifecycle = source.ProcessingStatus == DocumentProcessingStatus.Received;
        if (ownsSourceLifecycle)
            source.StartExtraction();
        var run = ExtractionRun.Create(job.BusinessUnitId, source.Id, Guid.NewGuid(), job.Id,
            job.Attempts, "llm-unstructured/v1", "lead-extraction/v1");
        run.RecordProcessingEvidence(outcome.ProcessingPath, outcome.OcrStatus,
            outcome.OcrPageCount, outcome.OcrTruncated);
        var externalRequests = await _context.Set<ERP_RFQ_Automation.AI.AiRequest>()
            .AsNoTracking()
            .Where(x => x.BusinessUnitId == job.BusinessUnitId && x.ExtractionJobId == job.Id
                && x.ProviderClass == ERP_RFQ_Automation.AI.AiProviderClass.External)
            .ToListAsync(ct);
        var cost = ProcessingCostAttribution.Summarize(externalRequests);
        run.RecordCostStatus(
            cost.Status,
            outcome.OcrStatus == ExtractionOcrStatus.NotRequired ? "NotRequired" : "LocalUnpriced",
            cost.Amount,
            cost.Currency);
        run.Start();
        _context.Add(run);
        await _context.SaveChangesAsync(ct);

        if (ownsSourceLifecycle)
        {
            source.StartNormalization();
            source.RequireReview(0);
        }
        if (source.Corpus.Status == CorpusStatus.Processing)
            source.Corpus.RequireReview();
        run.Complete(outcome.PageCount, 0, 0, outcome.ExtractedItemCount, 0, 0);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<long?> PersistAndCompleteAsync(
        ExtractionJob job,
        ChunkedExtractionOutcome outcome,
        IExtractionQueue queue,
        string workerId,
        int leaseAttempt,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        var persisted = await _context.Database.CreateExecutionStrategy().ExecuteAsync(
            () => PersistAndCompleteCoreAsync(job, outcome, queue, workerId, leaseAttempt, leaseDuration, ct));
        if (persisted is null)
            return null;

        if (_leadIdentity is null)
            await TryDetectDuplicatesAsync(job, persisted.Leads, ct);
        if (_leadIdentity is null)
        {
            await TryResolveCustomersAsync(job, persisted.Leads, ct);
            await TryRouteLeadsAsync(job, persisted.Leads, ct);
        }
        else
        {
            var newLeadIds = await _context.Set<ERP_RFQ_Automation.LeadIdentity.LeadIngestionOccurrence>()
                .AsNoTracking()
                .Where(x => x.BusinessUnitId == job.BusinessUnitId && x.ExtractionJobId == job.Id
                    && x.Classification == ERP_RFQ_Automation.LeadIdentity.LeadOccurrenceClassification.New
                    && x.LeadId != null)
                .Select(x => x.LeadId!.Value).ToListAsync(ct);
            await TryResolveCustomersAsync(job, persisted.Leads.Where(x => newLeadIds.Contains(x.Id)), ct);
            await TryRouteLeadsAsync(job, persisted.Leads.Where(x => newLeadIds.Contains(x.Id)), ct);
        }
        return persisted.LeadId;
    }

    private async Task<PersistedExtraction?> PersistAndCompleteCoreAsync(
        ExtractionJob job,
        ChunkedExtractionOutcome outcome,
        IExtractionQueue queue,
        string workerId,
        int leaseAttempt,
        TimeSpan leaseDuration,
        CancellationToken ct)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(ct);

        // This conditional UPDATE both validates the fencing generation and holds the
        // queue row lock until commit, preventing reclaim during the persistence write.
        if (!await queue.RenewLeaseAsync(job.Id, workerId, leaseAttempt, leaseDuration, ct))
        {
            await transaction.RollbackAsync(ct);
            return null;
        }

        var previouslyTrackedLeadIds = _context.ChangeTracker.Entries<Lead>()
            .Select(entry => entry.Entity.Id)
            .Where(id => id > 0)
            .ToHashSet();
        var leadId = await PersistInternalAsync(job, outcome, enrichAfterPersistence: false, ct);
        var persistedLeads = _context.ChangeTracker.Entries<Lead>()
            .Where(entry => entry.Entity.Id > 0 && !previouslyTrackedLeadIds.Contains(entry.Entity.Id))
            .Select(entry => entry.Entity)
            .DistinctBy(lead => lead.Id)
            .ToArray();

        // The canonical document meter is committed in the same transaction as both the
        // business result and the fenced queue completion. A retry therefore cannot charge
        // twice, and a rollback leaves neither a successful job nor a usage occurrence.
        if (_usageMetering is not null)
        {
            var platformTenant = await _context.Set<ERP_RFQ_Automation.Platform.Models.Tenant>()
                .AsNoTracking()
                .Where(x => x.PrimaryBusinessUnitId == job.BusinessUnitId)
                .Select(x => new { x.Id, x.RateCardId })
                .SingleOrDefaultAsync(ct);
            if (platformTenant is not null)
            {
                var currency = platformTenant.RateCardId is long rateCardId
                    ? await _context.Set<ERP_RFQ_Automation.Billing.RateCard>().AsNoTracking()
                        .Where(x => x.Id == rateCardId).Select(x => x.Currency).SingleOrDefaultAsync(ct)
                    : null;
                var documentKey = $"extraction-job:{job.Id}:succeeded";
                await _usageMetering.RecordAsync(new RecordUsageEvent(
                    UsageEventIdentity.FromIdempotencyKey(platformTenant.Id, documentKey), platformTenant.Id, "documents", 1m, "document",
                    DateTime.SpecifyKind(job.CreatedOn, DateTimeKind.Utc), "ExtractionJob", job.Id.ToString(CultureInfo.InvariantCulture),
                    "ExtractionWorker", workerId, null, null, job.BatchId.ToString("N"),
                    documentKey, 0m, currency ?? "USD", job.ContentHash), ct);
                if (outcome.PageCountAuthoritative && outcome.PageCount > 0)
                {
                    var pageKey = $"extraction-job:{job.Id}:pages";
                    await _usageMetering.RecordAsync(new RecordUsageEvent(
                        UsageEventIdentity.FromIdempotencyKey(platformTenant.Id, pageKey), platformTenant.Id, "pages.processed", outcome.PageCount, "page",
                        DateTime.SpecifyKind(job.CreatedOn, DateTimeKind.Utc), "ExtractionRun", job.Id.ToString(CultureInfo.InvariantCulture),
                        "ExtractionWorker", workerId, null, null, job.BatchId.ToString("N"),
                        pageKey, 0m, currency ?? "USD", job.ContentHash), ct);
                }
                if (outcome.OcrPageCount > 0)
                {
                    var ocrKey = $"extraction-job:{job.Id}:ocr-pages";
                    await _usageMetering.RecordAsync(new RecordUsageEvent(
                        UsageEventIdentity.FromIdempotencyKey(platformTenant.Id, ocrKey), platformTenant.Id, "pages.ocr", outcome.OcrPageCount, "page",
                        DateTime.SpecifyKind(job.CreatedOn, DateTimeKind.Utc), "ExtractionRun", job.Id.ToString(CultureInfo.InvariantCulture),
                        "ExtractionWorker", workerId, null, null, job.BatchId.ToString("N"),
                        ocrKey, 0m, currency ?? "USD", job.ContentHash), ct);
                }
            }
        }
        if (!await queue.CompleteAsync(job.Id, workerId, leaseAttempt, leadId > 0 ? leadId : null, ct))
            throw new InvalidOperationException($"Fenced completion failed for extraction job {job.Id}.");

        await transaction.CommitAsync(ct);
        return new PersistedExtraction(leadId, persistedLeads);
    }

    private sealed record PersistedExtraction(long LeadId, Lead[] Leads);

    private async Task TryDetectDuplicatesAsync(
        ExtractionJob job, IEnumerable<Lead> leads, CancellationToken ct)
    {
        if (_duplicateDetector is null)
            return;

        foreach (var lead in leads)
        {
            try
            {
                var check = await _duplicateDetector.CheckAndFlagAsync(lead.Id, job.BusinessUnitId, ct);
                if (check.Flagged)
                    _log.LogInformation(
                        "Lead {LeadId} flagged as suspected duplicate of lead {OriginalId} ({Reason}).",
                        check.FlaggedLeadId, check.OriginalLeadId, check.Reason);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Duplicate detection failed for lead {LeadId}; persistence succeeded.", lead.Id);
            }
        }
    }

    /// <summary>
    /// Deterministic client-organisation resolution at INGESTION — the seam that turns
    /// "26 leads, 0 with a customer" into a lead a rep can act on without opening the PDF.
    ///
    /// ORDERING IS LOAD BEARING. It runs AFTER identity reconciliation and persistence
    /// because LeadIdentityApplicationService.CustomerScope() keys the dedup corpus on
    /// <c>customer:{Id}</c> once Lead.CustomerId is set and on <c>email:</c>/<c>buyer:</c>
    /// before that; resolving earlier would re-key occurrences already stored under the old
    /// scope and silently break duplicate/revision detection. It runs BEFORE routing so the
    /// routing engine sees the customer this pass established.
    ///
    /// Best-effort by contract: persistence has already committed, and an unresolved lead is
    /// a correct, recoverable outcome (the backfill entry point re-runs it). A resolution
    /// failure must never fail ingestion.
    /// </summary>
    private async Task TryResolveCustomersAsync(
        ExtractionJob job, IEnumerable<Lead> leads, CancellationToken ct)
    {
        if (_customerResolution is null)
            return;

        foreach (var lead in leads)
        {
            try
            {
                var outcome = await _customerResolution.ResolveAsync(job.BusinessUnitId, lead.Id, ct);
                _log.LogInformation(
                    "Client resolution for lead {LeadId}: {Status} ({Reason}, confidence {Confidence:F2}).",
                    lead.Id, outcome.Status, outcome.ReasonCode, outcome.Confidence);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "Client resolution failed for extracted lead {LeadId}; the lead stays unresolved and can be re-run.",
                    lead.Id);
                _context.ChangeTracker.Clear();
            }
        }
    }

    private async Task TryRouteLeadsAsync(
        ExtractionJob job, IEnumerable<Lead> leads, CancellationToken ct)
    {
        if (_routing is null)
            return;

        foreach (var lead in leads)
        {
            try
            {
                await _routing.RouteLeadAsync(job.BusinessUnitId,
                    new ERP_RFQ_Automation.CommercialRouting.RouteLeadCommand(
                        lead.Id,
                        $"extraction:{job.Id}:lead:{lead.Id}:route:v1",
                        $"extraction-job:{job.Id}"),
                    ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "Commercial routing failed for extracted lead {LeadId}; the lead remains available for reconciliation.",
                    lead.Id);
            }
        }
    }

    /// <summary>
    /// Resolves the pre-created EmailIngest for the email door. Other ingestion doors
    /// retain their own source occurrence and do not require synthetic email settings.
    /// </summary>
    private async Task<EmailIngest?> ResolveIngestAsync(
        ExtractionJob job, ExtractionJobMetadata? metadata, string parseStatus, DateTime now, CancellationToken ct)
    {
        if (metadata?.EmailIngestId is > 0)
        {
            // Tracked lookup so graph-add links (not re-inserts) it.
            var existing = await _context.EmailIngests
                .FirstOrDefaultAsync(e => e.Id == metadata.EmailIngestId.Value
                    && e.EmailConfiguration.BusinessUnitId == job.BusinessUnitId, ct);
            if (existing != null)
            {
                existing.ParseStatus = parseStatus;
                existing.ParsedAt = now;
                // AutoDetectChanges is disabled around SaveChanges — mark explicitly.
                _context.Entry(existing).Property(e => e.ParseStatus).IsModified = true;
                _context.Entry(existing).Property(e => e.ParsedAt).IsModified = true;
                return existing;
            }
            _log.LogWarning(
                "Job {JobId} referenced an EmailIngest unavailable in its tenant.",
                job.Id);
        }

        if (job.SourceType == ExtractionSourceType.Email)
            throw new InvalidOperationException("The email ingestion provenance record is unavailable.");

        return null;
    }

    private async Task<ExtractionJobMetadata?> ResolveMetadataAsync(
        ExtractionJob job,
        CancellationToken ct)
    {
        string? json = null;
        if (_context.Model.FindEntityType(typeof(SourceDocumentOccurrence)) is not null)
        {
            var occurrences = _context.Set<SourceDocumentOccurrence>()
                .AsNoTracking()
                .Where(x => x.BusinessUnitId == job.BusinessUnitId && x.ExtractionJobId == job.Id);
            if (_context.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
            {
                json = (await occurrences.Select(x => new { x.ReceivedOn, x.SourceMetadataJson })
                        .ToListAsync(ct))
                    .OrderBy(x => x.ReceivedOn)
                    .Select(x => x.SourceMetadataJson)
                    .FirstOrDefault();
            }
            else
            {
                json = await occurrences.OrderBy(x => x.ReceivedOn)
                    .Select(x => x.SourceMetadataJson)
                    .FirstOrDefaultAsync(ct);
            }
        }
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("metadata", out var element)
                    && element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    return element.Deserialize<ExtractionJobMetadata>();
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Stored ingestion metadata for job {JobId} is invalid.", job.Id);
            }
        }

        // Compatibility for jobs created before database-owned provenance was introduced.
        return ExtractionJobMetadata.TryLoad(job);
    }

    /// <summary>Builds one Lead (+ its LeadItems) for one extraction result/group.</summary>
    private static Lead BuildLead(
        ExtractionJob job,
        ExtractionJobMetadata? metadata,
        LeadExtractionResult ai,
        EmailIngest? ingest,
        DateTime now,
        string remarksPrefix)
    {
        var items = ai.Items ?? new List<LeadItemData>();
        var bidClosingDate = SanitizeDate(ParseDate(ai.BidClosingDate));
        var lead = new Lead
        {
            // Data hygiene at write time: junk RFQ numbers and placeholder buyer
            // identities persist as NULL (never "for" / "Unknown Buyer" / internal
            // pipeline addresses); sentinel dates (< year 2000, e.g. DateTime.MinValue
            // rendering as "01 Jan 1") are treated as unknown.
            Rfqno = IsPlausibleRfqNumber(ai.Rfqno) ? Truncate(ai.Rfqno!.Trim(), 100) : null,
            BuyersName = SanitizeBuyerName(Truncate(ai.BuyersName, 510)),
            RecDate = SanitizeDate(ParseDate(ai.RecDate)) ?? now,
            BidClosingDate = bidClosingDate,
            // FR-RFQ-04. Kept ALONGSIDE the Gregorian value, never instead of it: the Gregorian
            // date stays authoritative for every comparison and deadline calculation, and this
            // is the rendering a Saudi government buyer actually published against.
            BidClosingDateHijri = RfqDateParser.ToHijri(bidClosingDate),
            // FR-RFQ-04 / FR-RFQ-03. Read from the document where it states them; null where it
            // does not. Nothing here is inferred — an unstated delivery location is unknown, and
            // an unknown delivery location is not the buyer's head office.
            DeliveryLocation = Truncate(ai.DeliveryLocation, 500),
            RequiredDeliveryDate = SanitizeDate(ParseDate(ai.RequiredDeliveryDate)),
            AgreementReference = Truncate(ai.AgreementReference, 100),
            BiddingDecision = Truncate(ai.BiddingDecision, 100),
            AcknowledgmentDate = SanitizeDate(ParseDate(ai.AcknowledgmentDate)),
            SubDate = SanitizeDate(ParseDate(ai.SubDate)),
            HeaderRemarks = Truncate($"{remarksPrefix}{ai.HeaderRemarks}".Trim(), 8000),
            OpportunityNo = Truncate(ai.OpportunityNo, 100),
            NoOfLineItems = items.Count, // conservation: equals persisted count PER LEAD
            Rfqtype = Truncate(ai.Rfqtype, 50),
            DurationAgreement = Truncate(ai.DurationAgreement, 100),
            LeadSource = metadata?.LeadSource ?? job.SourceType.ToString(),
            EmailSource = Truncate(metadata?.EmailSource ?? job.FileType, 255),
            Clientemail = metadata?.FromEmail,
            Aiconfidence = ClampConfidence(ai.OverallConfidence),
            ReviewVersion = 1,
            RequiresCommercialReview = true,
            CommercialFactsVerified = false,
            // WP-BOQ foundation: document-level "product" | "service" | "mixed"
            // classification (partial property; column added by a lead migration).
            InquiryType = NormalizeInquiryType(ai.InquiryType),
            // ── Client organisation evidence (raw transcription, never a decision) ──
            // BuyersName above stays the PERSON ("3C2-AMER AL-DOSSARY"); these carry the
            // ORGANISATION. SupplierNameOnDocument is persisted precisely so the resolver
            // can EXCLUDE it: on the SEC corpus the only company name printed is our own.
            CustomerCompanyNameExtracted = SanitizeCustomerCompanyName(
                Truncate(ai.CustomerCompanyName, 320), ai.SupplierNameOnDocument),
            CustomerCompanyEvidence = Truncate(ai.CustomerCompanyEvidence, 200),
            CustomerCompanyRegistrationId = Truncate(ai.CustomerCompanyRegistrationId, 100),
            CustomerBuyerEmailExtracted = SanitizeExtractedEmail(Truncate(ai.CustomerBuyerEmail, 255)),
            CustomerPortalNameExtracted = Truncate(ai.CustomerPortalName, 120),
            SupplierNameOnDocument = SanitizeBuyerName(Truncate(ai.SupplierNameOnDocument, 320)),
            SupplierAccountRefOnDocument = Truncate(ai.SupplierAccountRefOnDocument, 100),
            CreatedBy = "System",
            CreatedDate = now,
            BusinessUnitId = job.BusinessUnitId,
            EmailIngests = ingest // navigation -> EF inserts/links ingest, fills EmailIngestsId
        };

        // DRIFT GUARD: the field-by-field mapping is shared with the email, folder and
        // manual-upload doors — see Services/LeadItemMapper.cs. It carries this door's
        // sentinel-date and confidence-clamp rules to all four, and it is where the single
        // unit-of-measure canonicalisation lives.
        foreach (var it in items)
            lead.LeadItems.Add(LeadItemMapper.Map(it, ParseDate));

        return lead;
    }

    /// <summary>
    /// Best-effort evidence linkage: one Attachment row per produced lead pointing at the
    /// immutable stored source document, mirroring what the legacy email/manual doors did.
    /// Never throws — persistence has already succeeded.
    /// </summary>
    private async Task TryAttachSourceDocumentAsync(ExtractionJob job, List<Lead> leads, DateTime now, CancellationToken ct)
    {
        try
        {
            long? size = null;
            try { size = new FileInfo(job.StoragePath).Length; } catch { /* stored file may be remote/purged */ }

            var fileName = job.FileName ?? Path.GetFileName(job.StoragePath);
            var filePath = ToPortablePath(job.StoragePath);
            var ext = (job.FileType ?? Path.GetExtension(job.StoragePath).TrimStart('.')).ToLowerInvariant();

            foreach (var lead in leads)
            {
                _context.Attachments.Add(new Attachment
                {
                    ParentType = "Lead",
                    ParentId = lead.Id,
                    FileName = fileName,
                    FilePath = filePath,
                    MimeType = MimeTypeFor(ext),
                    FileSize = size,
                    ContentType = MimeTypeFor(ext)?.Split('/')[0],
                    // FR-RFQ-08. The digest the governed intake computed when these bytes were
                    // captured — not a second one taken later, which would only prove the file
                    // matches itself now. Recording it here is what makes "immutable" checkable
                    // rather than merely intended.
                    ContentSha256 = job.ContentHash,
                    CreatedOn = now,
                    UploadedDate = now
                });
            }
            await _context.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to record source-document attachment(s) for job {JobId}; leads persisted.", job.Id);
        }
    }

    /// <summary>Stores the app-relative "Uploads/..." path when derivable (matches the
    /// legacy attachment convention); falls back to the absolute storage path.</summary>
    private static string ToPortablePath(string storagePath)
    {
        var idx = storagePath.LastIndexOf("Uploads", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? storagePath[idx..] : storagePath;
    }

    private static string? MimeTypeFor(string ext) => ext switch
    {
        "pdf" => "application/pdf",
        "doc" => "application/msword",
        "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "xlsx" or "xlsm" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "csv" => "text/csv",
        "txt" => "text/plain",
        "html" or "htm" => "text/html",
        "jpg" or "jpeg" => "image/jpeg",
        "png" => "image/png",
        "bmp" => "image/bmp",
        "tiff" or "tif" => "image/tiff",
        _ => "application/octet-stream"
    };

    /// <summary>Persist only the three known classifications; anything else is "unknown" (null).</summary>
    private static string? NormalizeInquiryType(string? value)
    {
        var t = value?.Trim().ToLowerInvariant();
        return t is "product" or "service" or "mixed" ? t : null;
    }

    private static DateTime? ParseDate(string? s) => RfqDateParser.Parse(s);

    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? null : (value.Length <= max ? value : value[..max]);

    // Sentinel/absurd dates (DateTime.MinValue, OCR noise like year 1) are "unknown",
    // not real values. Anything before 2000 is treated as missing.
    private static DateTime? SanitizeDate(DateTime? d)
        => d is { } v && v.Year >= 2000 ? v : null;

    // Internal placeholders must never surface as a buyer-facing identity; persist
    // NULL so the UI can style "unknown" explicitly.
    private static readonly HashSet<string> PlaceholderBuyerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "unknown", "unknown buyer", "n/a", "na", "none", "null", "tbd",
        "extraction@pipeline.local", "system@rfq.com"
    };

    private static string? SanitizeBuyerName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (PlaceholderBuyerNames.Contains(trimmed)) return null;
        if (trimmed.EndsWith("@pipeline.local", StringComparison.OrdinalIgnoreCase)) return null;
        return trimmed;
    }

    // Generic template words a model reaches for when the document names no buyer. Storing
    // any of these would turn "we do not know" into a fake fact the resolver then tries to
    // match.
    private static readonly HashSet<string> PlaceholderCompanyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "unknown", "unknown company", "unknown customer", "customer", "client", "buyer",
        "n/a", "na", "none", "null", "tbd", "not specified", "not stated", "not mentioned",
        "company", "the company", "organization", "organisation"
    };

    /// <summary>
    /// The write-time half of the direction-of-trade guard.
    ///
    /// Rejects placeholders, and rejects any "customer" name that is really the VENDOR block
    /// of the same document — the failure mode the whole feature exists to prevent, because
    /// on an SEC bid the only company name printed is the trading house receiving it.
    ///
    /// The tenant's own BusinessUnit name and mail domains are the OTHER half of that guard;
    /// they need a tenant-scoped database read, which BuildLead (static, no DbContext) cannot
    /// do, so they are enforced in <c>CustomerIdentityResolver.Guard</c> and
    /// <c>CustomerAliasLearner</c> — before anything is ever matched or learned. Persisting
    /// the raw string here is safe and deliberate: it is evidence a rep can read.
    /// </summary>
    private static string? SanitizeCustomerCompanyName(string? name, string? supplierNameOnDocument)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length < 2) return null;
        if (PlaceholderCompanyNames.Contains(trimmed)) return null;
        if (PlaceholderBuyerNames.Contains(trimmed)) return null;
        if (trimmed.EndsWith("@pipeline.local", StringComparison.OrdinalIgnoreCase)) return null;

        var supplierKey = ERP_RFQ_Automation.CustomerResolution.CustomerNameNormalizer.LooseKey(supplierNameOnDocument);
        if (supplierKey.Length > 0 &&
            string.Equals(ERP_RFQ_Automation.CustomerResolution.CustomerNameNormalizer.LooseKey(trimmed),
                supplierKey, StringComparison.Ordinal))
            return null;

        return trimmed;
    }

    /// <summary>
    /// Keeps only a single deliverable address, and never one of Nexora's own ingestion
    /// placeholders (extraction@pipeline.local, sec@system.com, manual@upload.com …).
    /// </summary>
    private static string? SanitizeExtractedEmail(string? email)
    {
        var parsed = ERP_RFQ_Automation.CustomerResolution.LeadCustomerResolutionService.ParseAddress(email);
        if (parsed is null) return null;
        return ERP_RFQ_Automation.CustomerResolution.SyntheticIdentityGuard.IsSyntheticAddress(parsed)
            ? null
            : parsed;
    }

    // Conservative junk filter for extracted RFQ numbers: reject only obvious
    // garbage (tiny fragments like "for", or bare generic words); keep anything
    // plausibly real — when in doubt, keep the value.
    private static readonly HashSet<string> GenericRfqWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "rfq", "quote", "quotation", "request", "na", "n/a", "none",
        "tbd", "null", "unknown", "for", "the"
    };

    private static bool IsPlausibleRfqNumber(string? rfqno)
    {
        var trimmed = rfqno?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;
        if (trimmed.Length < 3) return false;
        if (GenericRfqWords.Contains(trimmed)) return false;
        return true;
    }

    private static decimal? ClampConfidence(double? c)
    {
        if (c is null) return null;
        var v = c.Value;
        if (v < 0) v = 0;
        if (v > 1) v = 1;
        return (decimal)v;
    }
}
