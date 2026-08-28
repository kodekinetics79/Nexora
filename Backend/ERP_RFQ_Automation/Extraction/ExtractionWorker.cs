using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
using Microsoft.EntityFrameworkCore.Storage;
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
    /// <summary>
    /// Persists the ONE Lead for a fully-assembled email message.
    ///
    /// <para>Separate from <c>PersistAsync</c> because it is the single caller allowed past the
    /// assembly fence. The fence refuses a per-component Lead; this is the message-level write
    /// the fence exists to wait for, and it arrives with every component's lines already
    /// merged. A default implementation is provided so existing test doubles keep compiling and
    /// fail loudly if this path is reached unexpectedly.</para>
    /// </summary>
    Task<long> PersistAssembledMessageAsync(
        ExtractionJob job, ChunkedExtractionOutcome outcome, CancellationToken ct = default)
        => throw new NotSupportedException(
            "This ILeadPersister does not support message-level assembly.");

    /// <summary>
    /// Duplicate detection, customer resolution and routing for an assembled message's Lead.
    ///
    /// <para>Split out so the caller can run it AFTER its transaction commits. Inside, it would
    /// run while the assembly row's claim lock is still held, and a live worker finishing the
    /// last component of that same message would block on that lock WHILE HOLDING its queue
    /// lease — long enough, on a slow reconciliation, for the lease to expire, the job to be
    /// reclaimed, an attempt to be burnt and the extraction to be re-run. Best-effort by
    /// contract: a failure here must never undo a Lead that already exists.</para>
    /// </summary>
    Task EnrichAssembledMessageAsync(
        ExtractionJob job, long leadId, CancellationToken ct = default)
        => Task.CompletedTask;

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
                // AND THE MESSAGE'S BARRIER IS SETTLED, NOW, TRUTHFULLY.
                //
                // This branch is the one success path that produces no Lead and therefore no
                // component result, so nothing else ever closed the component: it stayed
                // Extracting until the stranded sweep found it thirty minutes later, saw a
                // Succeeded job with no result row, and closed it Skipped —
                // "No part of this message could be read". Every part WAS read; it was
                // deliberately routed away from Lead creation. The message spent half an hour
                // claiming to be in progress and then landed in a human's tray under a reason
                // that was not true.
                await IgnoreAssemblyComponentAsync(
                    job, CommercialNonInquiryReason, CommercialNonInquiryDetail, ct);
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
                    await CloseAssemblyComponentAsync(job, errorCode, permanent, workToken);
                    // CancellationToken.None deliberately: the queue row is already durably
                    // dead-lettered, so abandoning this small visibility write halfway through
                    // (lease-expiry or shutdown cancellation) would leave the triage screen
                    // claiming "Queued" over a dead letter — the exact lie being fixed. The
                    // catch-block failure sites below already record under None for the same
                    // reason.
                    await MarkIngestDeadLetterVisibleAsync(job, permanent, CancellationToken.None);
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

            // THE BARRIER'S TRIGGER.
            //
            // An email component's job creates no Lead of its own — the persister recorded its
            // result and completed it. If that was the last part the message was waiting for,
            // the message becomes one Lead now. The assembler decides: it is a no-op unless the
            // assembly is genuinely ReadyForAssembly, and idempotent when two workers finish
            // the final two components at the same moment.
            //
            // Deliberately AFTER the queue transition. Assembling first and completing second
            // would mean a crash in between leaves a Lead whose job still looks unfinished, and
            // the retry would build the Lead twice.
            // GetRequiredService, deliberately. GetService here meant a botched registration
            // silently skipped the barrier for EVERY message with no error anywhere — the same
            // class of invisible total stall this increment exists to remove. Resolved only
            // inside the branch, so a container without it (the lease/heartbeat harnesses) is
            // unaffected.
            if (job.EmailInquiryComponentId is not null
                && scope.ServiceProvider.GetService<ErpRfqAutomationContext>() is { } assemblyContext)
            {
                var assembler = scope.ServiceProvider.GetRequiredService<
                    ERP_RFQ_Automation.Ingestion.Assembly.IEmailInquiryLeadAssembler>();
                var assemblyId = await assemblyContext
                    .Set<ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponent>()
                    .AsNoTracking()
                    .Where(x => x.BusinessUnitId == job.BusinessUnitId
                                && x.Id == job.EmailInquiryComponentId!.Value)
                    .Select(x => (long?)x.AssemblyId)
                    .FirstOrDefaultAsync(ct);

                if (assemblyId is { } id)
                {
                    var assembledLeadId = await assembler.AssembleAsync(job.BusinessUnitId, id, ct);
                    if (assembledLeadId is not null)
                        _log.LogInformation(
                            "Job {JobId} completed email assembly {AssemblyId} as lead {LeadId}.",
                            job.Id, id, assembledLeadId.Value);
                }
            }

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
                    await CloseAssemblyComponentAsync(job, parseReason, permanent: true, CancellationToken.None);
                    await MarkIngestDeadLetterVisibleAsync(job, permanent: true, CancellationToken.None);
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
                    // Evidence faults are this deployment's plumbing, not the document's fault:
                    // the component HOLDS the message rather than finalizing a lead that is
                    // missing content which still exists and will be readable once fixed.
                    await CloseAssemblyComponentAsync(
                        job, EvidenceErrorCodeFor(ex), permanent: false, CancellationToken.None);
                    await MarkIngestDeadLetterVisibleAsync(job, permanent: false, CancellationToken.None);
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
                    // THE ONE FAILURE PATH THAT DID NOT CLOSE ITS COMPONENT.
                    //
                    // Its three siblings above — extraction failure, parse failure, evidence
                    // integrity — all do. An UNEXPECTED exception that exhausted MaxAttempts
                    // therefore dead-lettered the job and left the component Extracting with
                    // nothing in flight: the message waited on a part no worker would ever pick
                    // up again until the thirty-minute sweep noticed. The closure is best-effort
                    // and idempotent, and the sweep remains the backstop for the dead letters the
                    // queue's own claim statement writes with no worker in the loop.
                    await CloseAssemblyComponentAsync(
                        job, "unexpected_extraction_failure", permanent: false, CancellationToken.None);
                    await MarkIngestDeadLetterVisibleAsync(job, permanent: false, CancellationToken.None);
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
    /// <summary>
    /// Closes the email-assembly barrier for a job that has stopped trying.
    ///
    /// <para><b>Why this exists.</b> One message is fanned out into components — the body and
    /// each attachment — and the assembly state machine will not finalize the message until
    /// EVERY component is terminal. Nothing on the failure path used to tell a component its
    /// job had died, so a component whose extraction dead-lettered stayed at
    /// <c>Extracting</c> forever. The message then produced NOTHING: no lead, no review item,
    /// no error anyone could see — only "1 of 4 parts assembled" in perpetuity. A single
    /// unreadable Terms &amp; Conditions PDF silently swallowed an entire RFQ whose body and
    /// other attachments had extracted perfectly.</para>
    ///
    /// <para>The distinction below is the whole point, and it is the one the state machine
    /// already knows how to act on. An INFRASTRUCTURE fault (storage unreachable, the object
    /// missing, the bucket misconfigured, the scanner down) is
    /// <see cref="ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponentStatus.FailedRecoverable"/>: the message is HELD, not
    /// finalized, because the content is presumed readable once the fault is fixed and a lead
    /// built without it would be priced against a document that still exists. A CONTENT fault
    /// (unsupported format, unreadable file, a refusal that retrying cannot change) is
    /// <see cref="ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponentStatus.Skipped"/>: terminal and commercially
    /// significant, which finalizes the message into review with a lead built from what WAS
    /// captured, and says plainly that one part could not be read.</para>
    ///
    /// <para>Either way the message becomes visible to a human. What it must never do again is
    /// disappear.</para>
    /// </summary>
    private async Task CloseAssemblyComponentAsync(
        ExtractionJob job,
        string errorCode,
        bool permanent,
        CancellationToken ct)
    {
        // Only a job that has stopped trying closes a barrier. A retryable attempt with budget
        // left is still in flight, and the component is correctly still Extracting.
        if (!permanent && job.Attempts < job.MaxAttempts) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            if (db.Model.FindEntityType(typeof(ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponent)) is null) return;

            var component = await db.Set<ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponent>()
                .SingleOrDefaultAsync(
                    x => x.BusinessUnitId == job.BusinessUnitId && x.ExtractionJobId == job.Id, ct);
            if (component is null) return;

            // Never overwrite a decision another layer already made — a security refusal or a
            // completed extraction outranks anything decided here.
            if (!ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponentClosure.IsClosable(
                    component.Status))
                return;

            // The infrastructure-vs-content rule lives in ONE place, shared with the
            // stranded-component sweep that closes the barriers this path never reached. Two
            // copies would drift, and the direction of the drift decides whether a customer's
            // RFQ is held forever or quoted against a document nobody read.
            component.Status = ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponentClosure
                .StatusFor(errorCode);
            component.UpdatedAtUtc = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(ct);
            _log.LogWarning(
                "Assembly component {ComponentId} closed as {Status} because extraction job {JobId} "
                + "stopped trying ({ErrorCode}); its message can now finalize instead of waiting "
                + "on a part that will never arrive.",
                component.Id, component.Status, job.Id, errorCode);

            // AND THE MESSAGE IS RE-EVALUATED, in the same best-effort block.
            //
            // Closing the component alone is only half the fix. The barrier's verdict is
            // recomputed by the coordinator, never inferred by whoever wrote a component last, so
            // a component that turns terminal without a re-evaluation leaves its message at
            // Extracting with every part settled and nothing that would ever look again — the
            // same perpetual "still being assembled" this whole path exists to end, moved one
            // level up. Resolved with GetService because the lease and heartbeat harnesses
            // compose a worker without the assembly capability at all.
            if (scope.ServiceProvider.GetService<
                    ERP_RFQ_Automation.Ingestion.Assembly.IEmailInquiryAssemblyCoordinator>()
                is { } coordinator)
            {
                await coordinator.ReevaluateAsync(component.AssemblyId, job.BusinessUnitId, ct);
            }
        }
        catch (Exception exception)
        {
            // Best-effort: never fail a job for being unable to annotate its component. The
            // stranded-component sweep is the backstop.
            _log.LogError(exception,
                "Could not close the assembly component for extraction job {JobId}.", job.Id);
        }
    }

    /// <summary>Reason recorded when a message's part was read and routed away from Lead creation.</summary>
    internal const string CommercialNonInquiryReason = "commercial_non_inquiry";

    /// <summary>
    /// What a person is told about a part that was read and deliberately not turned into an
    /// inquiry. It says what happened rather than reporting a failure, because nothing failed.
    /// </summary>
    internal const string CommercialNonInquiryDetail =
        "This part of the message was read and identified as a supplier document rather than a "
        + "request to quote, so no inquiry was created from it. Nothing has been lost.";

    /// <summary>
    /// Settles a component that was READ and deliberately produced no Lead.
    ///
    /// <para>Deliberately not <see cref="CloseAssemblyComponentAsync"/>. That method exists for a
    /// job that STOPPED TRYING and splits its outcome between an infrastructure hold and a
    /// content refusal — both of which are statements that something went wrong. This one settles
    /// a success: the part was read, a rule routed it away from Lead creation, and
    /// <see cref="ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponentStatus.Ignored"/> is
    /// the one terminal status the state machine already treats as "accounted for, and not a part
    /// the sender attached that went unread".</para>
    ///
    /// <para>Written through the coordinator so the barrier is recomputed in the same unit of
    /// work, and best-effort for the same reason as its sibling: a job must never fail because
    /// its component could not be annotated.</para>
    /// </summary>
    private async Task IgnoreAssemblyComponentAsync(
        ExtractionJob job, string reasonCode, string reasonDetail, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<ErpRfqAutomationContext>();
            if (db is null
                || db.Model.FindEntityType(
                    typeof(ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponent)) is null)
                return;

            var component = await db.Set<ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponent>()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    x => x.BusinessUnitId == job.BusinessUnitId && x.ExtractionJobId == job.Id, ct);
            if (component is null) return;

            // Never overwrite a decision reached with more information — a security refusal, a
            // completed extraction, an already recorded hold.
            if (!ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponentClosure.IsClosable(
                    component.Status))
                return;

            if (scope.ServiceProvider.GetService<
                    ERP_RFQ_Automation.Ingestion.Assembly.IEmailInquiryAssemblyCoordinator>()
                is not { } coordinator)
                return;

            await coordinator.RecordComponentOutcomeAsync(
                job.BusinessUnitId, component.AssemblyId, component.ComponentKey,
                ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponentStatus.Ignored,
                reasonCode, reasonDetail, sourceDocumentOccurrenceId: null, ct);

            _log.LogInformation(
                "Assembly component {ComponentId} settled as Ignored ({ReasonCode}) because job "
                + "{JobId} read it and routed it away from lead creation; its message finalizes "
                + "now instead of waiting for the stranded sweep.",
                component.Id, reasonCode, job.Id);
        }
        catch (Exception exception)
        {
            _log.LogError(exception,
                "Could not settle the assembly component for non-Lead extraction job {JobId}; the "
                + "stranded-component sweep remains the backstop.", job.Id);
        }
    }

    /// <summary>Maps an evidence fault to the error code its component disposition keys on.</summary>
    private static string EvidenceErrorCodeFor(EvidenceIntegrityException exception) =>
        exception.Code switch
        {
            EvidenceIntegrityException.ObjectMissingCode => "evidence_missing",
            EvidenceIntegrityException.BucketMismatchCode => "evidence_bucket_mismatch",
            // A genuine digest mismatch is the document being WRONG, not the plumbing: it is
            // terminal, and the message finalizes into review rather than waiting forever.
            _ => "evidence_integrity_failure",
        };

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

    /// <summary>
    /// EmailIngest.ParseStatus value for an email whose extraction reached the dead-letter
    /// queue. &lt;= 50 chars (the column's limit), and in the existing "Failed - reason"
    /// vocabulary ("Failed - nothing to extract", "Failed - N attachment(s) skipped") so the
    /// triage screen and the LeadRepository/Dashboard readers — which key only on
    /// "NeedsReview" — treat it exactly like every other failed state.
    /// </summary>
    internal const string DeadLetterParseStatus = "Failed - extraction dead-lettered";

    /// <summary>
    /// ING-09: makes a dead-lettered email job VISIBLE on its EmailIngest row.
    ///
    /// <para>
    /// The only other writer that resolves ParseStatus downstream of "Queued" is
    /// <c>LeadPersister.ResolveIngestAsync</c>, which runs on the persist/success path. When
    /// every job of a message dead-letters, that path never runs, so the triage screen said
    /// "Queued" forever while the DLQ held the truth. This is the failure-path counterpart.
    /// </para>
    /// <para>
    /// DELIBERATELY in worker C#, on both database engines — unlike the occurrence intake
    /// status, which <c>trg_release01c_sync_intake_from_job</c> owns on PostgreSQL and which
    /// <see cref="MarkIntakeFailureAsync"/> therefore only writes when
    /// <c>!db.Database.IsNpgsql()</c>. No trigger touches EmailIngests on either engine, so
    /// there is nothing here to race or rewind; guarding this write the same way would
    /// reintroduce on PostgreSQL exactly the invisibility being fixed.
    /// </para>
    /// <para>
    /// Only "Queued"/"Pending" are overwritten: an email fans out to SEVERAL jobs (body +
    /// attachments), and a sibling that already persisted a lead has set Success/NeedsReview
    /// — a later dead-letter among the siblings must not un-say that. In the other order the
    /// same rule self-heals: a successful sibling (or a dead-letter recovery replay) flips
    /// this state back through ResolveIngestAsync, which writes unconditionally.
    /// </para>
    /// <para>
    /// Best-effort like every intake annotation here: the queue row is already durably
    /// dead-lettered, and a failure to annotate must not turn into a second failure recording.
    /// Not covered: jobs dead-lettered by the claim statement itself (the exhausted-lease and
    /// lineage-quarantine CTEs in ExtractionQueue, PostgreSQL-only) — no worker owns those
    /// transitions; they remain visible through the operator dead-letter queue.
    /// </para>
    /// </summary>
    private async Task MarkIngestDeadLetterVisibleAsync(
        ExtractionJob job, bool permanent, CancellationToken ct)
    {
        if (job.SourceType != ExtractionSourceType.Email) return;
        // Same terminality condition FailAsync/FailPermanentlyAsync apply: Attempts was
        // incremented at claim, so this attempt was the last one iff it reached MaxAttempts.
        if (!permanent && job.Attempts < job.MaxAttempts) return;
        try
        {
            var metadata = await ReadJobMetadataAsync(job, ct);
            if (metadata?.EmailIngestId is not > 0) return;
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            var ingest = await db.EmailIngests
                .FirstOrDefaultAsync(e => e.Id == metadata.EmailIngestId.Value
                    && e.EmailConfiguration.BusinessUnitId == job.BusinessUnitId, ct);
            if (ingest is null || ingest.ParseStatus is not ("Queued" or "Pending")) return;
            ingest.ParseStatus = DeadLetterParseStatus;
            ingest.ParsedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            _log.LogWarning(
                "Job {JobId} dead-lettered; EmailIngest {IngestId} marked '{Status}' so the "
                + "triage screen stops claiming the message is still queued.",
                job.Id, ingest.Id, DeadLetterParseStatus);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Job {JobId} was dead-lettered but its EmailIngest could not be marked failed. "
                + "The queue row is authoritative; the triage screen overstates progress until "
                + "a retry resolves it.", job.Id);
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

        // Unstructured baseline: top slice as header context, the rest GROUPED INTO ITEMS.
        //
        // Grouping is not cosmetic. `lines.Skip(n)` used to be handed straight through as the
        // item regions, so one LINE was one "item": chunking then cut every 23 lines, straight
        // through the middle of each item's specification block, and the model was asked to
        // find whole line items in fragments carrying no code and no quantity. See
        // LineItemRegionGrouper for the measurements.
        var headerLineCount = Math.Min(20, lines.Count);
        var header = string.Join('\n', lines.Take(headerLineCount));
        var regions = LineItemRegionGrouper.Group(lines.Skip(headerLineCount).ToList()).ToList();
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
    private readonly ERP_RFQ_Automation.CommercialRouting.ICommercialRoutingApplicationService? _routing;
    private readonly ERP_RFQ_Automation.LeadIdentity.ILeadIdentityApplicationService? _leadIdentity;
    /// <summary>Present once email assembly is wired; absent leaves non-email ingestion untouched.</summary>
    private readonly ERP_RFQ_Automation.Ingestion.Assembly.IEmailInquiryAssemblyCoordinator? _emailAssemblies;
    private readonly ERP_RFQ_Automation.CustomerResolution.ILeadCustomerResolutionService? _customerResolution;
    private readonly UsageMeteringService? _usageMetering;
    /// <summary>Null disables auto-verification entirely (every lead goes to review).</summary>
    private readonly decimal? _autoVerifyMinConfidence;

    /// <summary>Config key for <see cref="_autoVerifyMinConfidence"/>; default 0.85.</summary>
    internal const string AutoVerifyMinConfidenceKey = "Extraction:Review:AutoVerifyMinConfidence";

    /// <summary>Recorded as the approver so an auto-verified lead is never mistaken in the
    /// audit trail for one a named person looked at.</summary>
    internal const string AutoVerifyActor = "system:auto-verified-high-confidence";

    public LeadPersister(
        ErpRfqAutomationContext context,
        ILogger<LeadPersister> log,
        ERP_RFQ_Automation.CommercialRouting.ICommercialRoutingApplicationService? routing = null,
        ERP_RFQ_Automation.LeadIdentity.ILeadIdentityApplicationService? leadIdentity = null,
        ERP_RFQ_Automation.Ingestion.Assembly.IEmailInquiryAssemblyCoordinator? emailAssemblies = null,
        ERP_RFQ_Automation.CustomerResolution.ILeadCustomerResolutionService? customerResolution = null,
        UsageMeteringService? usageMetering = null,
        IConfiguration? configuration = null)
    {
        // A CLEAN extraction whose model confidence clears the bar is verified without a human.
        // Holding every lead regardless of confidence meant the queue never drained: confidence
        // was computed, stored, and then not consulted for the one decision it exists to inform,
        // so a perfect three-line RFQ and an unreadable scan arrived in the same tray. Anything
        // not clean — low confidence, unverified quotes, truncated input, OCR gaps, a split
        // document — still goes to a person. Off wherever no configuration exists (direct/test
        // construction), so the tests that assert unconditional review still assert it.
        _autoVerifyMinConfidence = configuration is null
            ? null
            : configuration.GetValue<decimal?>(AutoVerifyMinConfidenceKey) ?? 0.85m;
        _context = context;
        _log = log;
        _routing = routing;
        _leadIdentity = leadIdentity;
        _emailAssemblies = emailAssemblies;
        _customerResolution = customerResolution;
        _usageMetering = usageMetering;
    }

    /// <summary>
    /// Turns the extractor's outcome into the versioned shape the result store holds.
    ///
    /// <para>The conversion lives at this boundary on purpose: the store is versioned and the
    /// extractor's type is not, so exactly one place has to know how today's outcome maps onto
    /// today's contract version.</para>
    /// </summary>
    private static ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponentResultPayload
        BuildComponentResultPayload(ExtractionJob job, ChunkedExtractionOutcome outcome)
        => new(
            System.Text.Json.JsonSerializer.Serialize(outcome.Result is null
                ? null
                : outcome.Result with
                {
                    Items = outcome.Result.Items.Select(item => item with
                    {
                        // Never accept a provider-authored job id. The worker that owns this
                        // component is the provenance authority.
                        SourceExtractionJobId = job.Id
                    }).ToList()
                }),
            outcome.ProcessingPath.ToString(),
            outcome.AiProviderClass?.ToString(),
            null,
            outcome.Result?.OverallConfidence is { } confidence ? (decimal)confidence : null,
            outcome.ExpectedItemCount,
            outcome.ExtractedItemCount,
            outcome.ReviewReason,
            outcome.Diagnostics.Count > 0
                ? System.Text.Json.JsonSerializer.Serialize(outcome.Diagnostics)
                : null);

    public Task<long> PersistAsync(
        ExtractionJob job, ChunkedExtractionOutcome outcome, CancellationToken ct = default)
        => PersistInternalAsync(job, outcome, enrichAfterPersistence: true, bypassAssemblyFence: false, ct);

    public Task<long> PersistAssembledMessageAsync(
        ExtractionJob job, ChunkedExtractionOutcome outcome, CancellationToken ct = default)
        // enrichAfterPersistence: FALSE — see EnrichAssembledMessageAsync. Enrichment runs after
        // the caller commits, so the assembly claim lock is not held across it.
        => PersistInternalAsync(job, outcome, enrichAfterPersistence: false, bypassAssemblyFence: true, ct);

    public async Task EnrichAssembledMessageAsync(
        ExtractionJob job, long leadId, CancellationToken ct = default)
    {
        var lead = await _context.Leads.FirstOrDefaultAsync(
            x => x.BusinessUnitId == job.BusinessUnitId && x.Id == leadId, ct);
        if (lead is null) return;

        Lead[] leads = [lead];
        // No duplicate-detector call here: reconciliation (ILeadIdentityApplicationService) owns
        // duplicate classification and is always registered, so the old `if (_leadIdentity is
        // null)` gate never opened. LeadDuplicateDetector was deleted with its other two dead
        // call sites rather than left as a path nothing takes.
        await TryResolveCustomersAsync(job, leads, ct);
        await TryRouteLeadsAsync(job, leads, ct);
    }

    private async Task<long> PersistInternalAsync(
        ExtractionJob job,
        ChunkedExtractionOutcome outcome,
        bool enrichAfterPersistence,
        bool bypassAssemblyFence,
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
        if (!bypassAssemblyFence
            && _emailAssemblies is not null
            && job.EmailInquiryComponentId is { } ownedComponentId)
        {
            // ONE ownership authority, read straight off the job row.
            //
            // This used to join EmailInquiryComponents on ExtractionJobId — a nullable
            // back-reference written in a second statement after the insert, which a worker
            // could claim the job ahead of. It is now a column on the job, written with the
            // job, backed by a composite foreign key carrying the tenant. The component's own
            // ExtractionJobId and SourceDocumentOccurrenceId remain as diagnostics; neither
            // decides anything.
            var owner = await _context
                .Set<ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponent>()
                .AsNoTracking()
                .Where(x => x.BusinessUnitId == job.BusinessUnitId && x.Id == ownedComponentId)
                .Select(x => new { x.AssemblyId, x.ComponentKey })
                .FirstOrDefaultAsync(ct);

            if (owner is null)
            {
                // The foreign key makes this unreachable through the database. If it happens
                // anyway the job is genuinely ownerless, and inventing a per-document Lead
                // from it is the one thing that must not happen.
                throw new InvalidOperationException(
                    $"Extraction job {job.Id} names email inquiry component {ownedComponentId}, "
                    + $"which does not exist for business unit {job.BusinessUnitId}.");
            }

            // The result becomes DURABLE, the component completes, and the message is
            // re-evaluated — one transaction, in the coordinator. No Lead is created here:
            // that is the barrier's job once every sibling has finished.
            var payload = BuildComponentResultPayload(job, outcome);
            var evaluation = await _emailAssemblies.RecordComponentResultAsync(
                job.BusinessUnitId, ownedComponentId, job.Id, payload, ct);

            _log.LogInformation(
                "Component {ComponentKey} of assembly {AssemblyId} recorded a durable result for "
                + "business unit {BusinessUnitId}; the message is now {Status} "
                + "({Completed} of {Captured} captured).",
                owner.ComponentKey, owner.AssemblyId, job.BusinessUnitId, evaluation.Status,
                evaluation.CompletedComponentCount, evaluation.CapturedComponentCount);

            // No Lead is built here, and not merely because the message may be incomplete.
            // Assembly is ORCHESTRATION and belongs to the worker: an assembler injected into
            // the persister is a dependency cycle (the assembler must persist the Lead it
            // builds), and the container rejects it — which is how this was found, with three
            // jobs dead-lettered on a circular-dependency message.
            return 0;
        }

        // ---- CUTOVER FENCE: AN EMAIL JOB THAT OWNS NO COMPONENT --------------------------
        //
        // An Email job with a NULL EmailInquiryComponentId used to fall through to the
        // per-job Lead reconciliation below. That was correct for exactly one reason and the
        // reason is gone: capture was not wired, so NO email job carried a component, and a
        // blanket refusal here stopped every email becoming an RFQ while 4911 tests stayed
        // green. The outage was found by a product owner looking at a screen.
        //
        // Both producers are now canonical. EmailInquiryIntakeService captures the message
        // and EmailIngestEnqueuer.ScheduleAsync writes the component id WITH the job row, and
        // it is the only scheduler. So a component-less email job today means the scheduler
        // and the worker disagree about the same message — and the per-document Lead it would
        // otherwise mint is the precise defect the barrier exists to remove: a covering note
        // priced without the schedule that was attached to it.
        //
        // Gated on the coordinator for the same reason the fence above is: a container without
        // the assembly capability (the lease/heartbeat harnesses) has no assembly to hold and
        // no state to be inconsistent with, and an unregistered capability must degrade to the
        // pre-fence behaviour rather than silently stop ingestion. The production graph always
        // registers it, and EmailInquiryAssemblyRegistrationTests is what proves that.
        if (!bypassAssemblyFence
            && _emailAssemblies is not null
            && job.SourceType == ExtractionSourceType.Email
            && job.EmailInquiryComponentId is null)
        {
            // Resolve the MESSAGE, not the component: the job names no component, so the only
            // honest question left is "is there an assembly this job's message belongs to?".
            // The sidecar is a best-effort HINT and cannot authorize anything, so its ingest id
            // is used only to look the row up, and the lookup is bound by the job's own tenant.
            var strandedMetadata = await ResolveMetadataAsync(job, ct);
            long? strandedAssemblyId = null;
            if (strandedMetadata?.EmailIngestId is > 0
                && _context.Model.FindEntityType(
                    typeof(ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryAssembly)) is not null)
            {
                strandedAssemblyId = await _context
                    .Set<ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryAssembly>()
                    .AsNoTracking()
                    .Where(x => x.BusinessUnitId == job.BusinessUnitId
                                && x.EmailIngestId == strandedMetadata.EmailIngestId!.Value)
                    .Select(x => (long?)x.Id)
                    .FirstOrDefaultAsync(ct);
            }

            if (strandedAssemblyId is { } holdAssemblyId)
            {
                // The message exists as an aggregate, so the operator gets a message-level
                // hold they can see and act on rather than a queue row nobody reads. Held, not
                // lost: the raw evidence and every sibling component are untouched.
                await _emailAssemblies.HoldForReviewAsync(
                    job.BusinessUnitId, holdAssemblyId,
                    ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryHoldReasons.OwnershipUnresolved,
                    ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryHoldReasons
                        .OwnershipUnresolvedDetail,
                    ct);

                _log.LogError(
                    "Extraction job {JobId} (business unit {BusinessUnitId}) is an email job that "
                    + "owns no inquiry component; assembly {AssemblyId} is held for review and NO "
                    + "per-document lead was created.",
                    job.Id, job.BusinessUnitId, holdAssemblyId);

                // Deliberately not a throw. The message is visibly held and re-schedulable, and
                // retrying THIS job cannot help — a job never gains a component id.
                return 0;
            }

            // No assembly: this is legacy in-flight work from before the cutover. It is failed
            // with a reason an operator can act on. The drain boundary — how many of these
            // remain and what to do with them — is /api/operations/readiness and
            // docs/EMAIL-TO-LEAD-EXECUTION-LEDGER.md; permanent dual routing is not the answer.
            //
            // The reason names ids only. No file name, no sender, no path.
            throw new InvalidOperationException(
                $"Extraction job {job.Id} (business unit {job.BusinessUnitId}) is an email job with "
                + "no inquiry component and no inquiry assembly, so it predates the email intake "
                + "cutover. It cannot produce a lead on its own; reprocess the message from the "
                + "inbound mail triage surface, which enters the canonical intake.");
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
        // A split document is never auto-verified: deciding that one message was really several
        // separate enquiries is exactly the judgement a person should confirm.
        var mayAutoVerify = _autoVerifyMinConfidence is not null
            && outcome.Status == ExtractionOutcomeStatus.Ok
            && results.Count == 1
            // Confidence cannot make an unusable demand quantity true. A clean document-level
            // extraction may legitimately preserve 0, negative or TBD as a null line value;
            // those lines require a person and may never turn the ingest green through the
            // confidence shortcut.
            && results[0].Items is { Count: > 0 }
            && results[0].Items.All(item => item.Quantity is > 0);
        for (var g = 0; g < results.Count; g++)
        {
            var splitNote = results.Count > 1
                ? $"Split from a multi-inquiry document (group {g + 1} of {results.Count}). "
                : string.Empty;
            var lead = BuildLead(job, metadata, results[g], ingest, now,
                $"{reviewNote}{splitNote}{sourceNote}");
            if (mayAutoVerify && lead.Aiconfidence >= _autoVerifyMinConfidence)
            {
                lead.RequiresCommercialReview = false;
                lead.CommercialFactsVerified = true;
                lead.ReviewApprovedBy = AutoVerifyActor;
                lead.ReviewApprovedOn = now;
            }
            leads.Add(lead);
        }

        // Keep the message's own status honest about what happened to it. The ingest status was
        // decided before the leads existed, so a lead that needed nobody would still have left
        // its message sitting in the review tray.
        if (ingest is not null && leads.Count > 0 && leads.All(l => !l.RequiresCommercialReview))
            ingest.ParseStatus = "Success";

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
            // invent one. A reply carries its own Message-Id and will NOT match on it — which is
            // why the ANCESTOR chain below exists: the mail door persists the reply's In-Reply-To
            // and References headers on the ingest row, and reconciliation treats "this message
            // replies to a message an existing lead came from" as strong (but never solitary)
            // evidence for that lead.
            // Manual upload and watched folders have no mail message at all, so both stay null
            // rather than carrying a manufactured identity that the scorer would read as evidence.
            var emailThreadId = job.SourceType != ExtractionSourceType.Email
                ? null
                : metadata?.LogicalGroupKey is { Length: > 0 } messageGroupKey
                    && messageGroupKey.StartsWith("email:", StringComparison.Ordinal)
                    ? messageGroupKey
                    : ingest?.MessageId is { Length: > 0 } messageId ? $"email:{messageId}" : null;
            var threadAncestorKeys = job.SourceType == ExtractionSourceType.Email && ingest is not null
                ? ERP_RFQ_Automation.Services.EmailService.ThreadAncestorKeys(
                    ingest.InReplyToMessageId, ingest.ReferencesJson)
                : Array.Empty<string>();
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
                        LogicalGroupKey = logicalGroupKey ?? metadata?.LogicalGroupKey,
                        ThreadReferencedMessageIds = threadAncestorKeys
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
            var loadedLeads = await _context.Leads.Include(x => x.LeadItems)
                .Where(x => canonicalIds.Contains(x.Id)).ToListAsync(ct);
            // SQL does not preserve the order of an IN predicate. Evidence persistence is
            // positional (result group N belongs to reconciled Lead N), so re-establish the
            // reconciliation order explicitly instead of binding a source line to whichever
            // Lead PostgreSQL happened to return first.
            leads = reconciliation.Where(x => x.LeadId > 0)
                .Select(result => loadedLeads.Single(x => x.Id == result.LeadId))
                .ToList();
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
                await PersistUnstructuredRunAsync(job, outcome, leads, reconciliation, ct);
        }

        _log.LogInformation(
            "Persisted {LeadCount} lead(s) ({LeadIds}) with {Count} item(s) total from job {JobId}.",
            leads.Count, string.Join(",", leads.Select(l => l.Id)),
            leads.Sum(l => l.LeadItems.Count), job.Id);

        // Evidence linkage: every produced lead gets an Attachment row pointing at the
        // immutable source document (shared across split leads). Best-effort — an
        // attachment failure must never fail the persistence.
        await TryAttachSourceDocumentAsync(job, leads, now, ct);

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
        ExtractionJob job, ChunkedExtractionOutcome outcome, IReadOnlyList<Lead> leads,
        IReadOnlyList<ERP_RFQ_Automation.LeadIdentity.LeadReconciliationResult> reconciliation,
        CancellationToken ct)
    {
        const string parserVersion = "llm-unstructured/v2";
        const string schemaVersion = "lead-extraction/v2";

        var groups = outcome.SplitResults is { Count: > 1 }
            ? outcome.SplitResults
            : outcome.Result is null ? [] : [outcome.Result];
        // Reconciliation may deliberately produce no Lead when a commercially similar inquiry
        // requires human identity review.  That is a valid zero-output run: retain the extraction
        // and source-document lifecycle, but do not invent a canonical inquiry/line graph.  Any
        // non-zero mismatch is still corruption because positional evidence could bind to the
        // wrong Lead.
        var hasCanonicalOutput = groups.Count == leads.Count;
        if (!hasCanonicalOutput && leads.Count != 0)
            throw new InvalidOperationException(
                "Unstructured evidence persistence requires one reconciled Lead per extraction group.");

        // The current job is always part of the ledger, even when no line earned exact source
        // evidence. A message-level assembly may also carry verified lines from sibling
        // components; only jobs in the same tenant and intake batch are accepted.
        var requestedJobIds = groups.SelectMany(x => x.Items)
            .Where(x => (x.SourceSpanVerified && !string.IsNullOrWhiteSpace(x.SourceSpan))
                        || x.VerifiedEvidence is { Count: > 0 })
            .Select(x => x.SourceExtractionJobId ?? job.Id)
            .Append(job.Id)
            .Distinct()
            .ToArray();
        var sourceJobs = await _context.Set<ExtractionJob>().AsNoTracking()
            .Where(x => x.BusinessUnitId == job.BusinessUnitId
                        && x.BatchId == job.BatchId
                        && requestedJobIds.Contains(x.Id))
            .ToListAsync(ct);
        var sourceJobById = sourceJobs.ToDictionary(x => x.Id);
        if (!sourceJobById.ContainsKey(job.Id))
            sourceJobById[job.Id] = job;

        var contentHashes = sourceJobById.Values.Select(x => x.ContentHash).Distinct().ToArray();
        var sources = await _context.Set<SourceDocument>()
            .Include(x => x.Corpus)
            .Where(x => x.BusinessUnitId == job.BusinessUnitId
                        && contentHashes.Contains(x.ContentHash))
            .ToListAsync(ct);
        var sourceByHash = sources.ToDictionary(x => x.ContentHash, StringComparer.OrdinalIgnoreCase);
        if (!sourceByHash.TryGetValue(job.ContentHash, out var anchorSource))
            throw new InvalidOperationException(
                $"Extraction job {job.Id} has no authoritative source-document record.");
        if (sources.Any(x => x.SecurityStatus != DocumentSecurityStatus.Cleared))
            throw new InvalidOperationException("Every source document must pass security inspection before evidence is persisted.");

        if (_context.Database.IsNpgsql())
        {
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({"unstructured-evidence:" + anchorSource.Id}, 0))", ct);
        }

        // The anchor run is the replay fence for the whole graph. The Lead reconciliation and
        // this method execute in one transaction, so an existing run means the immutable
        // inquiry/line/field graph already committed too.
        if (await _context.Set<ExtractionRun>().AnyAsync(x =>
                x.BusinessUnitId == job.BusinessUnitId && x.ExtractionJobId == job.Id
                && x.AttemptNumber == Math.Max(1, job.Attempts), ct))
            return;

        var validSourceJobs = sourceJobById.Values
            .Where(x => sourceByHash.ContainsKey(x.ContentHash))
            .DistinctBy(x => x.Id)
            .ToArray();
        var externalRequests = await _context.Set<ERP_RFQ_Automation.AI.AiRequest>()
            .AsNoTracking()
            .Where(x => x.BusinessUnitId == job.BusinessUnitId
                        && x.ExtractionJobId.HasValue
                        && requestedJobIds.Contains(x.ExtractionJobId.Value)
                        && x.ProviderClass == ERP_RFQ_Automation.AI.AiProviderClass.External)
            .ToListAsync(ct);

        var runs = new Dictionary<long, ExtractionRun>();
        var ownsLifecycle = new HashSet<long>();
        foreach (var sourceJob in validSourceJobs)
        {
            var source = sourceByHash[sourceJob.ContentHash];
            if (source.ProcessingStatus == DocumentProcessingStatus.Received)
            {
                source.StartExtraction();
                ownsLifecycle.Add(source.Id);
            }

            var runId = DeterministicUnstructuredRunId(sourceJob, parserVersion);
            var run = ExtractionRun.Create(job.BusinessUnitId, source.Id, runId, sourceJob.Id,
                Math.Max(1, sourceJob.Attempts), parserVersion, schemaVersion);
            run.RecordProcessingEvidence(outcome.ProcessingPath, outcome.OcrStatus,
                outcome.OcrPageCount, outcome.OcrTruncated);
            var cost = ProcessingCostAttribution.Summarize(
                externalRequests.Where(x => x.ExtractionJobId == sourceJob.Id).ToList());
            run.RecordCostStatus(cost.Status,
                outcome.OcrStatus == ExtractionOcrStatus.NotRequired ? "NotRequired" : "LocalUnpriced",
                cost.Amount, cost.Currency);
            run.Start();
            runs[sourceJob.Id] = run;
            _context.Add(run);
        }
        await _context.SaveChangesAsync(ct);

        if (_context.Database.IsNpgsql())
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({anchorSource.CorpusId})", ct);
        var nextInquiryNumber = (await _context.Set<CanonicalInquiry>()
            .Where(x => x.CorpusId == anchorSource.CorpusId)
            .Select(x => (int?)x.InquiryNumber)
            .MaxAsync(ct) ?? 0) + 1;

        var pending = new List<(LeadItemData Item, CanonicalLineItem Line, int Ordinal)>();
        for (var groupIndex = 0; hasCanonicalOutput && groupIndex < groups.Count; groupIndex++)
        {
            var result = groups[groupIndex];
            var lead = leads[groupIndex];
            var inquiry = CanonicalInquiry.Create(job.BusinessUnitId, anchorSource.CorpusId,
                nextInquiryNumber++);
            inquiry.PopulateHeader(result.Rfqno, result.BuyersName, null, null);
            inquiry.BindLead(lead.Id);
            // Model-derived commercial facts always require a person, even when the quoted
            // source span is exact.
            inquiry.RequireReview();
            _context.Add(inquiry);
            await _context.SaveChangesAsync(ct);

            var leadItemIds = await ResolveEvidenceLeadItemIdsAsync(
                job, lead, reconciliation.Count == groups.Count ? reconciliation[groupIndex] : null,
                result.Items.Count, ct);
            for (var lineIndex = 0; lineIndex < result.Items.Count; lineIndex++)
            {
                var item = result.Items[lineIndex];
                var description = item.ProductShortName
                                  ?? item.ProductShortDescription
                                  ?? item.ItemText
                                  ?? "[description requires review]";
                var line = CanonicalLineItem.Create(job.BusinessUnitId, inquiry.Id, lineIndex + 1,
                    description, item.Quantity is > 0 ? item.Quantity : null, item.UnitOfMeasure);
                line.Enrich(item.ManufacturerName, item.ManufacturerPartNumber, item.Currency,
                    item.UnitPrice, ParseNonNegativeInt(item.LeadTime), JsonSerializer.Serialize(item),
                    CanonicalValidationStatus.Warning);
                line.BindLeadItem(leadItemIds[lineIndex]);
                _context.Add(line);
                pending.Add((item, line, lineIndex + 1));
            }
            await _context.SaveChangesAsync(ct);
        }

        var pagesByJob = new Dictionary<long, DocumentPage>();
        foreach (var sourceJob in validSourceJobs)
        {
            var source = sourceByHash[sourceJob.ContentHash];
            var page = await _context.Set<DocumentPage>()
                .SingleOrDefaultAsync(x => x.BusinessUnitId == job.BusinessUnitId
                                           && x.DocumentId == source.Id && x.PageNumber == 1, ct);
            if (page is null)
            {
                page = DocumentPage.Create(job.BusinessUnitId, source.Id, 1, 1, 1);
                if (outcome.OcrStatus == ExtractionOcrStatus.NotRequired)
                    page.MarkOcrNotRequired();
                _context.Add(page);
                await _context.SaveChangesAsync(ct);
            }
            pagesByJob[sourceJob.Id] = page;
        }

        var regionCounts = runs.Keys.ToDictionary(x => x, _ => 0);
        var evidenceCounts = runs.Keys.ToDictionary(x => x, _ => 0);
        foreach (var (item, line, ordinal) in pending)
        {
            var sourceJobId = item.SourceExtractionJobId ?? job.Id;
            if (!runs.TryGetValue(sourceJobId, out var run)
                || !pagesByJob.TryGetValue(sourceJobId, out var page))
                continue; // foreign/stale provenance can never satisfy the evidence gate.

            if (item.SourceSpanVerified && !string.IsNullOrWhiteSpace(item.SourceSpan))
            {
                var raw = item.SourceSpan.Trim();
                var address = $"message-body:verified-span:{ordinal}";
                var region = DocumentRegion.Create(job.BusinessUnitId, page.Id,
                    DocumentRegionType.Text, 0, ordinal - 1, 1, 1, raw, 1m,
                    sourceAddress: address);
                _context.Add(region);
                await _context.SaveChangesAsync(ct);
                _context.Add(FieldEvidence.ForLineItem(job.BusinessUnitId, region.Id, line.Id,
                    "SourceSpan", raw, item.ProductShortName ?? item.ProductShortDescription,
                    1m, parserVersion, run.RunId,
                    valueKind: FieldValueKind.Text,
                    validationStatus: FieldValidationStatus.Valid,
                    transformationsJson: "[]"));
                regionCounts[sourceJobId]++;
                evidenceCounts[sourceJobId]++;
            }

            foreach (var exact in item.VerifiedEvidence ?? [])
            {
                if (string.IsNullOrWhiteSpace(exact.FieldName)
                    || string.IsNullOrWhiteSpace(exact.RawValue)
                    || string.IsNullOrWhiteSpace(exact.SourceAddress))
                    continue;
                var region = DocumentRegion.Create(job.BusinessUnitId, page.Id,
                    DocumentRegionType.TableCell, 0, ordinal - 1, 1, 1, exact.RawValue.Trim(),
                    Math.Clamp(exact.Confidence, 0m, 1m),
                    sourceAddress: exact.SourceAddress.Trim());
                _context.Add(region);
                await _context.SaveChangesAsync(ct);
                _context.Add(FieldEvidence.ForLineItem(job.BusinessUnitId, region.Id, line.Id,
                    exact.FieldName.Trim(), exact.RawValue.Trim(), exact.NormalizedValue,
                    Math.Clamp(exact.Confidence, 0m, 1m), parserVersion, run.RunId,
                    valueKind: FieldValueKind.Text,
                    validationStatus: FieldValidationStatus.Valid,
                    transformationsJson: "[]"));
                regionCounts[sourceJobId]++;
                evidenceCounts[sourceJobId]++;
            }
        }
        await _context.SaveChangesAsync(ct);

        foreach (var sourceJob in validSourceJobs)
        {
            var source = sourceByHash[sourceJob.ContentHash];
            if (ownsLifecycle.Contains(source.Id))
            {
                source.StartNormalization();
                source.RequireReview(1);
            }
            if (source.Corpus.Status == CorpusStatus.Processing)
                source.Corpus.RequireReview();
            var isAnchor = sourceJob.Id == job.Id;
            runs[sourceJob.Id].Complete(1, regionCounts[sourceJob.Id],
                isAnchor && hasCanonicalOutput ? groups.Count : 0,
                isAnchor && hasCanonicalOutput ? groups.Sum(x => x.Items.Count) : 0,
                evidenceCounts[sourceJob.Id], 0);
        }
        await _context.SaveChangesAsync(ct);
    }

    private async Task<long[]> ResolveEvidenceLeadItemIdsAsync(
        ExtractionJob job,
        Lead lead,
        ERP_RFQ_Automation.LeadIdentity.LeadReconciliationResult? reconciliation,
        int expectedLineCount,
        CancellationToken ct)
    {
        if (reconciliation?.RevisionId is { } revisionId)
        {
            if (reconciliation.LeadId != lead.Id)
                throw new InvalidOperationException(
                    $"Evidence reconciliation for extraction job {job.Id} does not match Lead {lead.Id}.");

            var occurrenceIsBound = await _context.Set<ERP_RFQ_Automation.LeadIdentity.LeadIngestionOccurrence>()
                .AsNoTracking()
                .AnyAsync(x => x.BusinessUnitId == job.BusinessUnitId
                    && x.Id == reconciliation.OccurrenceId
                    && x.BatchId == job.BatchId
                    && x.ExtractionJobId == job.Id
                    && x.LeadId == lead.Id
                    && x.LeadRevisionId == revisionId, ct);
            if (!occurrenceIsBound)
                throw new InvalidOperationException(
                    $"Evidence reconciliation for extraction job {job.Id} is not bound to its occurrence, batch, Lead, and immutable revision.");

            var revisionLines = await _context.Set<ERP_RFQ_Automation.LeadIdentity.LeadItemRevision>()
                .AsNoTracking()
                .Where(x => x.BusinessUnitId == job.BusinessUnitId
                    && x.LeadId == lead.Id
                    && x.LeadRevisionId == revisionId)
                .OrderBy(x => x.LineNumber)
                .Select(x => new { x.LineNumber, x.LeadItemId })
                .ToListAsync(ct);
            if (revisionLines.Count != expectedLineCount || revisionLines.Any(x => !x.LeadItemId.HasValue))
                throw new InvalidOperationException(
                    $"Lead {lead.Id} revision {revisionId} has {revisionLines.Count} immutable lines but its extraction group has {expectedLineCount}.");

            return revisionLines.Select(x => x.LeadItemId!.Value).ToArray();
        }

        // Legacy/test graphs without identity reconciliation have no immutable revision result.
        // They retain the pre-versioning behavior and bind to the one current projection.
        var currentLeadItemIds = lead.LeadItems
            .Where(x => x.IsCurrentRevisionProjection)
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToArray();
        if (currentLeadItemIds.Length != expectedLineCount)
            throw new InvalidOperationException(
                $"Lead {lead.Id} has {currentLeadItemIds.Length} current lines but its extraction group has {expectedLineCount}.");
        return currentLeadItemIds;
    }

    private static Guid DeterministicUnstructuredRunId(ExtractionJob job, string parserVersion)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{job.BusinessUnitId}|{job.Id}|{Math.Max(1, job.Attempts)}|{job.ContentHash}|{parserVersion}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static int? ParseNonNegativeInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : null;

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
        var leadId = await PersistInternalAsync(
            job, outcome, enrichAfterPersistence: false, bypassAssemblyFence: false, ct);
        var persistedLeads = _context.ChangeTracker.Entries<Lead>()
            .Where(entry => entry.Entity.Id > 0 && !previouslyTrackedLeadIds.Contains(entry.Entity.Id))
            .Select(entry => entry.Entity)
            .DistinctBy(lead => lead.Id)
            .ToArray();

        // The canonical document meter is committed in the same transaction as both the
        // business result and the fenced queue completion. A retry therefore cannot charge
        // twice, and a rollback leaves neither a successful job nor a usage occurrence.
        //
        // ---- WHY THIS BLOCK CHANGES EXECUTION ROLE -----------------------------------------
        //
        // This transaction runs as nexora_tenant_app, by TWO independent routes: ProcessOnceAsync
        // pushes the tenant scope so TenantRlsCommandInterceptor switches the connection, AND
        // ExtractionQueue.PrepareExecutionScopeAsync issues its own SET LOCAL ROLE when
        // RenewLeaseAsync opens this transaction a few lines above. The second route needs no
        // interceptor and no RLS configuration at all, so this is not a production-topology-only
        // problem — it is every extraction, everywhere, and a graph that registers no interceptor
        // reproduces it exactly.
        //
        // Everything below is PLATFORM plane: it reads platform."Tenants" and
        // platform."RateCards" and writes platform."UsageEvents", "UsageEventRatings" and
        // "UsageMinuteAggregates". nexora_tenant_app holds column-level SELECT on six Tenants
        // columns and NOTHING on the rest of that list — not even Tenants."RateCardId", which the
        // projection below reads. The first statement therefore failed with
        // `42501: permission denied for table Tenants`, the job failed, and the queue re-leased it
        // until it dead-lettered. It went unseen because no test graph registered
        // UsageMeteringService, so LeadPersister's optional dependency was null and this block
        // never ran.
        //
        // TWO WAYS TO FIX IT, and why this one:
        //
        //  (a) Grant nexora_tenant_app what it lacks, with an RLS policy pinning it to its own
        //      row. Defensible for Tenants alone — a tenant reading its own tenant row is not a
        //      privilege escalation, and that is exactly why the six column grants above already
        //      exist. It does NOT extend to the rest of this block. Making metering work under
        //      the tenant role means granting INSERT on platform."UsageEvents" and
        //      "UsageEventRatings" and INSERT/UPDATE on "UsageMinuteAggregates" — the billing
        //      ledger, owned by the platform, written by the platform, and the one plane a tenant
        //      role must never be able to reach. A tenant-writable meter is a tenant-editable
        //      invoice. That is a far wider hole than the one being closed.
        //
        //  (b) Run the platform-plane statements as the platform role. nexora_pipeline_app
        //      already holds every grant this block needs, so no schema change, no new grant, and
        //      no widening of any role's reach. The switch is SET LOCAL on the SAME connection in
        //      the SAME transaction — exactly what ExtractionQueue already does to reach the
        //      tenant role — so the meter still commits or rolls back with the business result and
        //      the fenced completion, which is the property the comment above depends on.
        //
        // (b), for the reason the plane split exists at all: the meter is not the tenant's data.
        //
        // NOT wrapped in a try/catch, deliberately. Unbilled usage is a silent revenue loss that
        // nobody discovers; a failed extraction job is loud, retried and visible. If metering
        // cannot record, this transaction must roll back.
        if (_usageMetering is not null)
        {
            // Fail closed on the one way the block below could be misused. Inside it the
            // connection is nexora_pipeline_app, which is BYPASSRLS: a tenant-plane entity left
            // dirty in the change tracker would be flushed by the metering service's own
            // SaveChanges and written without a policy check. Persistence has already saved, so
            // this is an assertion about a future edit, not a condition seen today.
            if (_context.ChangeTracker.HasChanges())
                throw new InvalidOperationException(
                    $"Extraction job {job.Id} reached usage metering with unsaved tenant-plane "
                    + "changes. They would be written under the platform role, bypassing row-level "
                    + "security. Save (or discard) them before entering the platform plane.");

            // The role is restored to whatever this transaction was already using, read rather
            // than assumed. It is nexora_tenant_app on every production path — ExtractionQueue's
            // PrepareExecutionScopeAsync set it when RenewLeaseAsync opened this transaction — but
            // a harness with a substituted queue leaves the connection on its login role, and
            // forcing such a caller onto the tenant role afterwards would be a second defect.
            var restoreRole = await CurrentRoleAsync(ct);

            using (PlatformPlaneExecution.Enter())
            {
                // TWO mechanisms, because there are two ways this connection acquires a role and
                // only one of them is the interceptor.
                //
                // PlatformPlaneExecution alone is not enough: ExtractionQueue issues its own
                // SET LOCAL ROLE directly on the connection when it opens the lease renewal, with
                // no interceptor involved, and SET LOCAL persists to the end of the transaction.
                // So the persist transaction sits on nexora_tenant_app even in a graph that
                // registers no interceptor at all — which is why this defect reaches every
                // extraction, not only the deployments running the RLS interceptor.
                //
                // The explicit statement below moves the connection to the platform role the same
                // way ExtractionQueue moves it to the tenant role. PlatformPlaneExecution then
                // stops the interceptor, where one IS registered, from clobbering it again before
                // every command inside the block.
                await SetLocalRoleAsync(TenantRlsCommandInterceptor.PipelineRole, ct);

                await MeterExtractionAsync(job, outcome, workerId, ct);
            }

            if (restoreRole is not null)
                await SetLocalRoleAsync(restoreRole, ct);
        }
        if (!await queue.CompleteAsync(job.Id, workerId, leaseAttempt, leadId > 0 ? leadId : null, ct))
            throw new InvalidOperationException($"Fenced completion failed for extraction job {job.Id}.");

        await transaction.CommitAsync(ct);
        return new PersistedExtraction(leadId, persistedLeads);
    }

    /// <summary>
    /// The role this transaction is currently executing as, or null off PostgreSQL.
    /// </summary>
    private async Task<string?> CurrentRoleAsync(CancellationToken ct)
    {
        if (!_context.Database.IsNpgsql())
            return null;

        await using var command = CreateTransactionCommand("SELECT current_user;");
        return await command.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>
    /// Moves the CURRENT transaction to <paramref name="role"/>. Transaction-local, so the commit
    /// or rollback that ends this transaction discards it either way.
    /// </summary>
    private async Task SetLocalRoleAsync(string role, CancellationToken ct)
    {
        // Roles are a PostgreSQL concept and SET LOCAL ROLE is a syntax error on SQLite, which is
        // the same branch ExtractionQueue.PrepareExecutionScopeAsync takes. Nothing is skipped:
        // there is no role to switch on that provider.
        if (!_context.Database.IsNpgsql())
            return;

        // The role name is never user input — it is either a constant from
        // TenantRlsCommandInterceptor or a value PostgreSQL itself just returned from
        // current_user — and it is quoted as an identifier because SET ROLE takes no parameter.
        await using var command = CreateTransactionCommand(
            $"SET LOCAL ROLE \"{role.Replace("\"", "\"\"")}\";");
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// A raw command on the context's own connection and transaction, exactly as
    /// <c>ExtractionQueue.PrepareExecutionScopeAsync</c> issues its role switch.
    ///
    /// <para>Raw rather than <c>ExecuteSqlRawAsync</c> on purpose: these two statements ARE the
    /// role mechanism, so routing them through the EF pipeline — where an interceptor prepends its
    /// own <c>SET LOCAL ROLE</c> to every command — would mean the statement that sets the role is
    /// itself preceded by a statement that sets the role. Going straight at the connection keeps
    /// the switch a single, ordered, observable fact.</para>
    /// </summary>
    private System.Data.Common.DbCommand CreateTransactionCommand(string sql)
    {
        var command = _context.Database.GetDbConnection().CreateCommand();
        command.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        return command;
    }

    /// <summary>
    /// The platform-plane half of the persist transaction: reads the tenant's billing identity and
    /// records the meters. Extracted so the role switch above wraps exactly this and nothing else.
    /// </summary>
    private async Task MeterExtractionAsync(
        ExtractionJob job, ChunkedExtractionOutcome outcome, string workerId, CancellationToken ct)
    {
        if (_usageMetering is null)
            return;

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
    }

    private sealed record PersistedExtraction(long LeadId, Lead[] Leads);

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
