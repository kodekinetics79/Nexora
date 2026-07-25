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
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ERP_RFQ_Automation.HealthChecks;

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

    public ExtractionWorker(
        IServiceScopeFactory scopeFactory,
        ExtractionWorkerOptions options,
        ILogger<ExtractionWorker> log,
        ITenantScopeAccessor tenantScope,
        IExtractionWorkerHeartbeat? workerHeartbeat = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _log = log;
        _tenantScope = tenantScope;
        _workerHeartbeat = workerHeartbeat;
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

        using var tenantScope = _tenantScope.Push(job.BusinessUnitId);
        using var scope = _scopeFactory.CreateScope();
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
            var input = await reader.ReadAsync(job, workToken);

            ChunkedExtractionOutcome outcome;
            if (input.IsStructured && input.StructuredRows is { Count: > 0 })
            {
                // Deterministic path bypasses the LLM entirely — no gate needed.
                outcome = await extractor.ExtractStructuredAsync(
                    input.StructuredRows, job.BusinessUnitId, input.SourceDocumentName, workToken);
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
                if (!await queue.FailAsync(
                        job.Id, workerId, job.Attempts,
                        outcome.ReviewReason ?? "Extraction produced no usable result.", workToken))
                    LogLeaseLost(job.Id, workerId, "recording extraction failure");
                else
                    await MarkIntakeFailureAsync(job, "extraction_failed", workToken);
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
                    await MarkIntakeFailureAsync(job, "evidence_integrity_failure", CancellationToken.None);
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
                    await MarkIntakeFailureAsync(job, "unexpected_extraction_failure", CancellationToken.None);
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

    private async Task MarkIntakeFailureAsync(ExtractionJob job, string errorCode, CancellationToken ct)
    {
        if (!job.SourceDocumentOccurrenceId.HasValue) return;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        if (db.Database.IsNpgsql()) return;
        var occurrence = await db.Set<SourceDocumentOccurrence>()
            .SingleAsync(x => x.BusinessUnitId == job.BusinessUnitId && x.Id == job.SourceDocumentOccurrenceId, ct);
        if (occurrence.IntakeStatus == IntakeOccurrenceStatus.Processing)
        {
            if (job.Attempts >= job.MaxAttempts) occurrence.MarkDeadLetter(errorCode);
            else occurrence.MarkRetryable(errorCode);
            await db.SaveChangesAsync(ct);
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
        => _log.LogWarning(
            "Worker {Worker} lost or expired its lease for job {JobId} while {Operation}; stale transition was fenced.",
            workerId, jobId, operation);
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
                    SourceId = $"{job.Id}:claim:{job.Attempts}",
                    ExtractionJobId = job.Id,
                    SourceDocumentOccurrenceId = job.SourceDocumentOccurrenceId,
                    SourceDocumentName = name,
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
            SourceId = $"{job.Id}:claim:{job.Attempts}",
            ExtractionJobId = job.Id,
            SourceDocumentOccurrenceId = job.SourceDocumentOccurrenceId,
            SourceDocumentName = name,
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

    // The detector is optional so persistence keeps working before (and without)
    // the Deduplication DI registration (see Deduplication/DEDUP-WIRING.md).
    public LeadPersister(
        ErpRfqAutomationContext context,
        ILogger<LeadPersister> log,
        ERP_RFQ_Automation.Deduplication.ILeadDuplicateDetector? duplicateDetector = null,
        ERP_RFQ_Automation.CommercialRouting.ICommercialRoutingApplicationService? routing = null,
        ERP_RFQ_Automation.LeadIdentity.ILeadIdentityApplicationService? leadIdentity = null)
    {
        _context = context;
        _log = log;
        _duplicateDetector = duplicateDetector;
        _routing = routing;
        _leadIdentity = leadIdentity;
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
            if (_context.Model.FindEntityType(typeof(SourceDocument)) is not null)
            {
                sourceDocumentId = await _context.Set<SourceDocument>()
                    .Where(x => x.BusinessUnitId == job.BusinessUnitId && x.ContentHash == job.ContentHash)
                    .Select(x => (long?)x.Id).SingleOrDefaultAsync(ct);
                if (job.SourceDocumentOccurrenceId.HasValue)
                    logicalGroupKey = await _context.Set<SourceDocumentOccurrence>()
                        .Where(x => x.BusinessUnitId == job.BusinessUnitId && x.Id == job.SourceDocumentOccurrenceId)
                        .Select(x => x.LogicalGroupKey).SingleOrDefaultAsync(ct);
            }
            var attributedExternalCost = await _context.Set<ERP_RFQ_Automation.AI.AiRequest>()
                .Where(x => x.BusinessUnitId == job.BusinessUnitId && x.ExtractionJobId == job.Id
                    && x.ProviderClass == ERP_RFQ_Automation.AI.AiProviderClass.External)
                .SumAsync(x => x.EstimatedCost ?? 0m, ct);
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
                        null, job.SourceType.ToString(), metadata?.FromEmail,
                        metadata?.Subject, job.FileName, null, null, job.ContentHash, sourceDocumentId, job.Id,
                        metadata?.SourceReceivedAtUtc, DateTimeOffset.UtcNow, path, path == ERP_RFQ_Automation.LeadIdentity.LeadProcessingPath.ExternalModel,
                        attributedExternalCost, "Service", "extraction-worker", $"extraction:{job.Id}")
                    {
                        SourceDocumentOccurrenceId = job.SourceDocumentOccurrenceId,
                        LogicalGroupKey = logicalGroupKey ?? metadata?.LogicalGroupKey
                    }, ct));
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
        run.RecordCostStatus(
            outcome.AiProviderClass == ERP_RFQ_Automation.AI.AiProviderClass.External ? "RateUnavailable" : "LocalNoCharge",
            "LocalNoCharge",
            outcome.AiProviderClass == ERP_RFQ_Automation.AI.AiProviderClass.External ? null : 0m,
            outcome.AiProviderClass == ERP_RFQ_Automation.AI.AiProviderClass.External ? null : "USD");
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
        run.Complete(0, 0, 0, 0, 0, 0);
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
            await TryRouteLeadsAsync(job, persisted.Leads, ct);
        else
        {
            var newLeadIds = await _context.Set<ERP_RFQ_Automation.LeadIdentity.LeadIngestionOccurrence>()
                .AsNoTracking()
                .Where(x => x.BusinessUnitId == job.BusinessUnitId && x.ExtractionJobId == job.Id
                    && x.Classification == ERP_RFQ_Automation.LeadIdentity.LeadOccurrenceClassification.New
                    && x.LeadId != null)
                .Select(x => x.LeadId!.Value).ToListAsync(ct);
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
    /// Resolves the EmailIngest all leads of this job link to: the PRE-CREATED row named
    /// by the sidecar (email door — its ParseStatus/ParsedAt are updated to the outcome),
    /// or a synthetic per-job row (manual/folder/queue uploads), exactly as before.
    /// </summary>
    private async Task<EmailIngest> ResolveIngestAsync(
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
                "Job {JobId} referenced an EmailIngest unavailable in its tenant; using a synthetic ingest.",
                job.Id);
        }

        var config = await _context.EmailConfigurations.AsNoTracking()
                         .FirstOrDefaultAsync(e => e.IsActive && e.BusinessUnitId == job.BusinessUnitId, ct)
                     ?? throw new InvalidOperationException(
                         "No active EmailConfiguration is available for this tenant's lead persistence.");

        return new EmailIngest
        {
            // BU-scoped so identical content ingested by two tenants (allowed by the
            // per-BU idempotency index) can never collide on the unique MessageId.
            MessageId = $"Extraction_{job.SourceType}_{job.BusinessUnitId}_{job.ContentHash[..Math.Min(24, job.ContentHash.Length)]}",
            EmailSubject = metadata?.Subject ?? job.FileName ?? "Extraction job",
            FromEmail = metadata?.FromEmail ?? "extraction@pipeline.local",
            ToEmail = "system@rfq.com",
            EmailConfigurationId = config.Id,
            CreatedOn = now,
            ParseStatus = parseStatus,
            ParsedAt = now
        };
    }

    private async Task<ExtractionJobMetadata?> ResolveMetadataAsync(
        ExtractionJob job,
        CancellationToken ct)
    {
        string? json = null;
        if (_context.Model.FindEntityType(typeof(SourceDocumentOccurrence)) is not null)
        {
            json = await _context.Set<SourceDocumentOccurrence>()
                .AsNoTracking()
                .Where(x => x.BusinessUnitId == job.BusinessUnitId && x.ExtractionJobId == job.Id)
                .OrderBy(x => x.ReceivedOn)
                .Select(x => x.SourceMetadataJson)
                .FirstOrDefaultAsync(ct);
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
        EmailIngest ingest,
        DateTime now,
        string remarksPrefix)
    {
        var items = ai.Items ?? new List<LeadItemData>();
        var lead = new Lead
        {
            // Data hygiene at write time: junk RFQ numbers and placeholder buyer
            // identities persist as NULL (never "for" / "Unknown Buyer" / internal
            // pipeline addresses); sentinel dates (< year 2000, e.g. DateTime.MinValue
            // rendering as "01 Jan 1") are treated as unknown.
            Rfqno = IsPlausibleRfqNumber(ai.Rfqno) ? Truncate(ai.Rfqno!.Trim(), 100) : null,
            BuyersName = SanitizeBuyerName(Truncate(ai.BuyersName, 510)),
            RecDate = SanitizeDate(ParseDate(ai.RecDate)) ?? now,
            BidClosingDate = SanitizeDate(ParseDate(ai.BidClosingDate)),
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
            CreatedBy = "System",
            CreatedDate = now,
            BusinessUnitId = job.BusinessUnitId,
            EmailIngests = ingest // navigation -> EF inserts/links ingest, fills EmailIngestsId
        };

        foreach (var it in items)
        {
            lead.LeadItems.Add(new LeadItem
            {
                CompanyRef = Truncate(it.CompanyRef, 100),
                CustomerAccountPortalId = Truncate(it.CustomerAccountPortalId, 100),
                CustomerRfqno = Truncate(it.CustomerRfqno, 100),
                ItemMaterialCode = Truncate(it.ItemMaterialCode, 100),
                CommodityProduct = Truncate(it.CommodityProduct, 200),
                BuyerName = Truncate(it.BuyerName, 200),
                LineItemNo = Truncate(it.LineItemNo, 50),
                ProductShortName = Truncate(it.ProductShortName, 1000),
                Alternative = Truncate(it.Alternative, 100),
                ProductShortDescription = Truncate(it.ProductShortDescription, 1000),
                Currency = Truncate(it.Currency, 10),
                UnitOfMeasure = Truncate(it.UnitOfMeasure, 100),
                UnitPrice = it.UnitPrice,
                Quantity = it.Quantity ?? 0,
                StorageLocation = Truncate(it.StorageLocation, 100),
                ManufacturerName = Truncate(it.ManufacturerName, 200),
                ManufacturerPartNumber = Truncate(it.ManufacturerPartNumber, 100),
                AlternateProductName = Truncate(it.AlternateProductName, 200),
                AlternatePartNumber = Truncate(it.AlternatePartNumber, 100),
                ItemText = Truncate(it.ItemText, 2000),
                MaterialPotext = Truncate(it.MaterialPotext, 2000),
                LeadTime = int.TryParse(it.LeadTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lt) ? lt : null,
                ReceivedDate = SanitizeDate(ParseDate(it.ReceivedDate)),
                BidClosingDateLine = SanitizeDate(ParseDate(it.BidClosingDateLine)),
                Aiconfidence = ClampConfidence(it.ItemConfidence ?? 0),
                // Unrecognized customer-document columns, capped + serialized to jsonb
                // (null when the model returned none — the common case).
                ExtraFields = ExtraFieldsJson.Serialize(it.ExtraFields)
            });
        }

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

    private static readonly string[] DateFormats =
        { "yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "dd-MM-yyyy", "d/M/yyyy", "yyyy/MM/dd" };

    private static DateTime? ParseDate(string? s)
        => string.IsNullOrWhiteSpace(s)
            ? null
            : DateTime.TryParseExact(s.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d
                : null;

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
