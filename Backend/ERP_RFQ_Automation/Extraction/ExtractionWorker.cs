using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    private readonly SemaphoreSlim _llmGate; // process-wide LLM concurrency cap

    public ExtractionWorker(
        IServiceScopeFactory scopeFactory,
        ExtractionWorkerOptions options,
        ILogger<ExtractionWorker> log)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _log = log;
        _llmGate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentLlmCalls));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var count = Math.Max(1, _options.WorkerCount);
        _log.LogInformation("ExtractionWorker starting {Count} loop(s); LLM cap {Llm}, per-tenant cap {Cap}.",
            count, _options.MaxConcurrentLlmCalls, _options.PerTenantConcurrencyCap);

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
        using var scope = _scopeFactory.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IExtractionQueue>();

        var job = await queue.ClaimAsync(workerId, _options.LeaseDuration, _options.PerTenantConcurrencyCap, ct);
        if (job is null)
            return false;

        try
        {
            var reader = scope.ServiceProvider.GetRequiredService<IExtractionDocumentReader>();
            var extractor = scope.ServiceProvider.GetRequiredService<IChunkedExtractionService>();
            var persister = scope.ServiceProvider.GetRequiredService<ILeadPersister>();

            var input = await reader.ReadAsync(job, ct);
            await queue.SetStatusAsync(job.Id, ExtractionStatus.Extracting, ct);

            ChunkedExtractionOutcome outcome;
            if (input.IsStructured && input.StructuredRows is { Count: > 0 })
            {
                // Deterministic path bypasses the LLM entirely — no gate needed.
                outcome = await extractor.ExtractStructuredAsync(input.StructuredRows, job.BusinessUnitId, input.SourceDocumentName, ct);
            }
            else
            {
                // Bound total in-flight LLM calls across the whole process.
                await _llmGate.WaitAsync(ct);
                try
                {
                    outcome = await extractor.ExtractUnstructuredAsync(input, ct);
                }
                finally
                {
                    _llmGate.Release();
                }
            }

            if (outcome.Status == ExtractionOutcomeStatus.Failed || outcome.Result is null)
            {
                await queue.FailAsync(job.Id, outcome.ReviewReason ?? "Extraction produced no usable result.", ct);
                return true;
            }

            // Renew before the (potentially large) persist so a slow write isn't reclaimed.
            await queue.RenewLeaseAsync(job.Id, workerId, _options.LeaseDuration, ct);
            await queue.SetStatusAsync(job.Id, ExtractionStatus.Persisting, ct);

            var leadId = await persister.PersistAsync(job, outcome, ct);
            await queue.CompleteAsync(job.Id, leadId, ct);

            _log.LogInformation(
                "Job {JobId} succeeded: lead {LeadId}, {Extracted}/{Expected} items, status {Status}.",
                job.Id, leadId, outcome.ExtractedItemCount, outcome.ExpectedItemCount, outcome.Status);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Leave the lease to expire; another worker reclaims it after shutdown.
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Job {JobId} failed; recording for retry/dead-letter.", job.Id);
            try { await queue.FailAsync(job.Id, ex.Message, CancellationToken.None); }
            catch (Exception failEx) { _log.LogError(failEx, "Also failed to record failure for job {JobId}.", job.Id); }
            return true;
        }
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

    public LeadPersister(ErpRfqAutomationContext context, ILogger<LeadPersister> log)
    {
        _context = context;
        _log = log;
    }

    public async Task<long> PersistAsync(ExtractionJob job, ChunkedExtractionOutcome outcome, CancellationToken ct = default)
    {
        var ai = outcome.Result ?? throw new InvalidOperationException("Cannot persist a null extraction result.");

        var config = await _context.EmailConfigurations.AsNoTracking()
                         .FirstOrDefaultAsync(e => e.IsActive && e.BusinessUnitId == job.BusinessUnitId, ct)
                     ?? await _context.EmailConfigurations.AsNoTracking()
                         .FirstOrDefaultAsync(e => e.IsActive, ct)
                     ?? throw new InvalidOperationException("No active EmailConfiguration available for lead persistence.");

        var now = DateTime.UtcNow;
        var reviewNote = outcome.Status == ExtractionOutcomeStatus.NeedsReview
            ? $"[NEEDS REVIEW] {outcome.ReviewReason} "
            : string.Empty;

        var ingest = new EmailIngest
        {
            MessageId = $"Extraction_{job.SourceType}_{job.ContentHash[..Math.Min(24, job.ContentHash.Length)]}",
            EmailSubject = job.FileName ?? "Extraction job",
            FromEmail = "extraction@pipeline.local",
            ToEmail = "system@rfq.com",
            EmailConfigurationId = config.Id,
            CreatedOn = now,
            ParseStatus = outcome.Status == ExtractionOutcomeStatus.NeedsReview ? "NeedsReview" : "Success",
            ParsedAt = now
        };

        var items = ai.Items ?? new List<LeadItemData>();
        var lead = new Lead
        {
            Rfqno = Truncate(ai.Rfqno, 100),
            BuyersName = Truncate(ai.BuyersName, 510),
            RecDate = ParseDate(ai.RecDate) ?? now,
            BidClosingDate = ParseDate(ai.BidClosingDate),
            BiddingDecision = Truncate(ai.BiddingDecision, 100),
            AcknowledgmentDate = ParseDate(ai.AcknowledgmentDate),
            SubDate = ParseDate(ai.SubDate),
            HeaderRemarks = Truncate($"{reviewNote}{ai.HeaderRemarks}".Trim(), 8000),
            OpportunityNo = Truncate(ai.OpportunityNo, 100),
            NoOfLineItems = items.Count, // conservation: equals persisted count
            Rfqtype = Truncate(ai.Rfqtype, 50),
            DurationAgreement = Truncate(ai.DurationAgreement, 100),
            LeadSource = job.SourceType.ToString(),
            EmailSource = Truncate(job.FileType, 255),
            Clientemail = "extraction@pipeline.local",
            Aiconfidence = ClampConfidence(ai.OverallConfidence),
            CreatedBy = "System",
            CreatedDate = now,
            BusinessUnitId = job.BusinessUnitId,
            EmailIngests = ingest // navigation -> EF inserts ingest first, fills EmailIngestsId
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
                ReceivedDate = ParseDate(it.ReceivedDate),
                BidClosingDateLine = ParseDate(it.BidClosingDateLine),
                Aiconfidence = ClampConfidence(it.ItemConfidence ?? 0)
            });
        }

        var autoDetect = _context.ChangeTracker.AutoDetectChangesEnabled;
        _context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            _context.Add(lead); // traverses the graph: ingest + lead + all items marked Added
            await _context.SaveChangesAsync(ct);
        }
        finally
        {
            _context.ChangeTracker.AutoDetectChangesEnabled = autoDetect;
        }

        _log.LogInformation("Persisted lead {LeadId} with {Count} item(s) from job {JobId}.", lead.Id, items.Count, job.Id);
        return lead.Id;
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

    private static decimal? ClampConfidence(double? c)
    {
        if (c is null) return null;
        var v = c.Value;
        if (v < 0) v = 0;
        if (v > 1) v = 1;
        return (decimal)v;
    }
}
