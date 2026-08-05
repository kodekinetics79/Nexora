using System.Globalization;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Billing;

/// <summary>Requested billing target (tenant / rate card / statement) does not exist.</summary>
public sealed class BillingNotFoundException : Exception
{
    public BillingNotFoundException(string message) : base(message) { }
}

/// <summary>The request conflicts with billing state (e.g. no effective rate card).</summary>
public sealed class BillingConflictException : Exception
{
    public BillingConflictException(string message) : base(message) { }
}

/// <summary>Signal-coverage verdicts for a <see cref="MeterReading"/>.</summary>
public static class MeterSignalCoverage
{
    /// <summary>Every unit of the metered resource is recorded by the source ledger.</summary>
    public const string Complete = "complete";

    /// <summary>
    /// The source ledger records only PART of the resource, so the reading is a
    /// floor. Meters in this state are NOT billing-ready: the machinery works and
    /// a rate card carrying the meter will compute, but the quantity systematically
    /// under-reports real consumption until the instrumentation gap is closed.
    /// </summary>
    public const string Incomplete = "incomplete";
}

/// <summary>
/// One live meter readout derived from a source ledger.
/// <see cref="SignalCoverage"/> / <see cref="BillingReady"/> / <see cref="CoverageNote"/>
/// travel on the wire so no operator prices a meter whose signal is known to be
/// partial without seeing the caveat first.
/// </summary>
public sealed record MeterReading(string MeterKey, decimal Quantity, string Unit, string SourceNote)
{
    /// <summary>One of <see cref="MeterSignalCoverage"/>. Defaults to complete.</summary>
    public string SignalCoverage { get; init; } = MeterSignalCoverage.Complete;

    /// <summary>
    /// False when the meter's source signal is known to be partial. A false value
    /// does NOT stop a rate card from pricing the meter — it is the explicit
    /// warning that doing so under-bills, and <see cref="CoverageNote"/> is copied
    /// onto every statement line the meter produces.
    /// </summary>
    public bool BillingReady { get; init; } = true;

    /// <summary>
    /// Names the exact instrumentation gaps when coverage is incomplete. Copied
    /// verbatim onto <see cref="BillingStatementLine.CoverageNote"/> — its own
    /// column, never concatenated into the line's provenance note.
    /// </summary>
    public string? CoverageNote { get; init; }
}

/// <summary>Live usage for a tenant over one period, straight from the source ledgers.</summary>
public sealed record TenantUsageReadout(
    long TenantId,
    long? BusinessUnitId,
    string Period,
    DateTime PeriodStartUtc,
    DateTime PeriodEndUtc,
    IReadOnlyList<MeterReading> Meters)
{
    /// <summary>
    /// v1 metering invariant (P2-B7): usage is metered for the tenant's
    /// <c>PrimaryBusinessUnitId</c> ONLY. v1 assumes 1 tenant = 1 business unit;
    /// any secondary business units are NOT metered or billed. This field makes
    /// the scope explicit on the wire so consumers cannot mistake the readout
    /// for a whole-fleet or multi-BU aggregate.
    /// </summary>
    public string MeteringScope { get; init; } = "primary-business-unit";
}

/// <summary>
/// Cost vs revenue for a tenant-period. <see cref="AiCostTotal"/> and
/// <see cref="GrossMargin"/> are null (never fabricated) whenever any settled AI
/// request in the period is unpriceable (RateUnavailable / LocalUnpriced).
/// </summary>
public sealed record TenantCostReport(
    long TenantId,
    long? BusinessUnitId,
    string Period,
    decimal? StatementTotal,
    string? StatementStatus,
    string? StatementCurrency,
    int SettledAiRequestCount,
    int UnpricedAiRequestCount,
    decimal PricedAiCostSubtotal,
    decimal? AiCostTotal,
    decimal? GrossMargin,
    string Note)
{
    /// <summary>
    /// External AI requests in the period whose outcome was never reconciled
    /// (status Unknown) or that failed (status Failed) — they consumed provider
    /// spend that is NOT included in <see cref="AiCostTotal"/> (P2-B6). A
    /// non-zero value means <see cref="GrossMargin"/> overstates true margin.
    /// </summary>
    public int UnreconciledExternalRequestCount { get; init; }

    /// <summary>Recorded tokens (input + output) on those Unknown/Failed external requests.</summary>
    public long UnreconciledExternalTokens { get; init; }

    /// <summary>Same v1 scope invariant as usage: PrimaryBusinessUnitId only (1 tenant = 1 BU).</summary>
    public string MeteringScope { get; init; } = "primary-business-unit";
}

public interface IBillingStatementService
{
    /// <summary>Live metered quantities from the source ledgers (no writes).</summary>
    Task<TenantUsageReadout> GetUsageAsync(long tenantId, BillingPeriod period, CancellationToken ct = default);

    /// <summary>
    /// Idempotently upserts the Draft statement for (tenant, period): recompute
    /// replaces Draft lines atomically; a Final statement is returned unchanged;
    /// a duplicate insert lost to a race returns the winning row. The CURRENT
    /// period computes freely (that Draft is the live preview); a period that has
    /// not started yet throws <see cref="BillingConflictException"/> (409).
    /// </summary>
    Task<BillingStatement> ComputeStatementAsync(
        long tenantId, BillingPeriod period, long? rateCardId = null, CancellationToken ct = default);

    /// <summary>
    /// Draft → Final (immutable). Already-Final statements return unchanged.
    /// Throws <see cref="BillingConflictException"/> (409) when the statement's
    /// period has not yet closed AND cleared
    /// <see cref="BillingStatementService.FinalizeSettleLag"/> — finalizing an
    /// open period permanently freezes partial usage.
    /// <paramref name="onFinalized"/> (when supplied) runs INSIDE the finalize
    /// transaction, after the status flip is saved and before commit — the
    /// caller's audit write commits atomically with the finalize or not at all
    /// (Sec3). It is invoked only on an actual Draft→Final transition, never on
    /// an idempotent re-finalize.
    /// </summary>
    Task<BillingStatement> FinalizeAsync(
        long statementId, string actor,
        Func<BillingStatement, CancellationToken, Task>? onFinalized = null,
        CancellationToken ct = default);

    /// <summary>AI cost vs statement total → gross margin (honest nulls when cost is unpriceable).</summary>
    Task<TenantCostReport> GetCostAsync(long tenantId, BillingPeriod period, CancellationToken ct = default);
}

/// <summary>
/// Server-side trace Usage (source ledgers) → meter aggregation → rate card →
/// charge lines → billing statement. Reads the EXISTING ledgers (ExtractionJobs,
/// AiRequests, Users, and the evidence ledger's ExtractionRuns / SourceDocuments
/// for pages and storage) — it never double-writes usage events. Platform plane:
/// cross-tenant reads use IgnoreQueryFilters deliberately.
/// </summary>
public class BillingStatementService : IBillingStatementService
{
    /// <summary>
    /// The exact instrumentation gaps behind <see cref="BillingMeterKeys.PagesProcessed"/>
    /// and <see cref="BillingMeterKeys.PagesOcr"/>, as established by the WS-C
    /// signal audit of every ingestion door. Echoed onto the usage readout AND
    /// onto every page statement line so the caveat is impossible to miss.
    /// </summary>
    internal const string PageSignalCoverageNote =
        "NOT BILLING-READY — page counts are only partially instrumented. " +
        "Text-layer PDFs, DOCX and e-mail bodies record 0 pages; the unstructured path records the OCR page count AS the page count, " +
        "and OCR itself is capped at 10 pages per document (truncation flagged, remainder never counted); " +
        "spreadsheet/CSV runs count evidence-bearing worksheets rather than physical pages; " +
        "supplier-quote intake and the RFQ/customer/quotation/product/supplier template uploaders create no run evidence at all. " +
        "The reading is therefore a FLOOR: it under-reports and never over-reports. " +
        "Do not price per page until the ingestion pipeline records a native page count per document.";

    /// <summary>
    /// Storage is exact for what it claims to measure (every SourceDocument
    /// carries a constructor-required ByteSize), but doors that bypass the
    /// evidence ledger contribute nothing to it.
    /// </summary>
    internal const string StorageCoverageNote =
        "ByteSize is recorded for every document in the evidence ledger (required at construction, taken from the ingested byte length). " +
        "Doors that bypass the evidence ledger — supplier-quote capture (Email/Portal/Api), the template uploaders and the legacy " +
        "direct-extract e-mail path (Ingestion:UseUnifiedQueue=false) — store no SourceDocument and so contribute no bytes.";

    /// <summary>
    /// How long after a period ENDS a statement for that period must wait before
    /// it can be finalized (P0: an open or future period must never be frozen).
    ///
    /// 48 hours, because usage for a period keeps landing after the calendar
    /// month closes: external AI requests are reconciled asynchronously (an
    /// Unknown request settles when the provider outcome is read back, which is
    /// what moves tokens onto the ai.tokens.external meter), and dead-letter
    /// re-drives replay failed documents — a re-driven job that finally succeeds
    /// changes its billable status and writes new run evidence. Finalizing before
    /// those land bakes a short bill in permanently: UX_BillingStatements_Tenant_
    /// PeriodStart blocks a second statement and the database immutability
    /// trigger blocks correcting the first one, so the under-charge is
    /// unfixable, not merely wrong.
    ///
    /// Applies to FINALIZE ONLY. Computing (and recomputing) the Draft for a live
    /// period is the whole point of the Draft preview and stays unrestricted.
    /// </summary>
    internal static readonly TimeSpan FinalizeSettleLag = TimeSpan.FromHours(48);

    private readonly ErpRfqAutomationContext _context;
    private readonly ILogger<BillingStatementService> _logger;

    public BillingStatementService(ErpRfqAutomationContext context, ILogger<BillingStatementService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<TenantUsageReadout> GetUsageAsync(
        long tenantId, BillingPeriod period, CancellationToken ct = default)
    {
        var tenant = await RequireTenantAsync(tenantId, ct);
        // v1 invariant (P2-B7): 1 tenant = 1 business unit. Metering deliberately
        // covers tenant.PrimaryBusinessUnitId ONLY (surfaced on the wire as
        // TenantUsageReadout.MeteringScope); secondary BUs are out of scope for v1.
        var bu = tenant.PrimaryBusinessUnitId;
        var meters = new List<MeterReading>();

        if (bu is not long businessUnitId)
        {
            const string noBuNote = "No primary business unit mapped to this tenant; all meters read zero.";
            meters.Add(new MeterReading(BillingMeterKeys.Documents, 0m, "document", noBuNote));
            meters.Add(PageMeter(BillingMeterKeys.PagesProcessed, 0m, noBuNote));
            meters.Add(PageMeter(BillingMeterKeys.PagesOcr, 0m, noBuNote));
            meters.Add(new MeterReading(BillingMeterKeys.AiTokensExternal, 0m, "token", noBuNote));
            meters.Add(new MeterReading(BillingMeterKeys.Seats, 0m, "seat", noBuNote));
            meters.Add(new MeterReading(BillingMeterKeys.StorageGb, 0m, "byte", noBuNote)
            {
                CoverageNote = StorageCoverageNote
            });
            return new TenantUsageReadout(tenantId, null, period.Key, period.StartUtc, period.EndUtc, meters);
        }

        // P0-B1: bill only delivered-or-in-flight work. Failed/DeadLetter jobs are
        // excluded here AND from the docs/month quota (BillableDocumentPolicy);
        // the Duplicate exclusion in that policy is defensive only — no code path
        // assigns that status today, because de-duplication happens at enqueue and
        // a duplicate submission never creates a second job.
        var nonBillable = BillableDocumentPolicy.NonBillableStatuses;
        var billableJobs = _context.Set<ExtractionJob>().IgnoreQueryFilters().AsNoTracking()
            .Where(j => j.BusinessUnitId == businessUnitId
                        && !nonBillable.Contains(j.Status)
                        && j.CreatedOn >= period.StartUtc && j.CreatedOn < period.EndUtc);
        var documents = await billableJobs.LongCountAsync(ct);

        // pages.processed / pages.ocr: evidence-ledger ExtractionRuns joined to the
        // SAME billable-job set (period attribution by job.CreatedOn, identical to
        // the documents meter, so pages and documents always cover the same work).
        // Double-count risk on retries: each attempt writes its own ExtractionRun
        // row (AttemptNumber 1..n) for the same document, so the aggregation takes
        // MAX(PageCount) / MAX(OcrPageCount) PER JOB — page count is a property of
        // the document, not of the attempt — and never sums across attempts.
        // Billable jobs with no run evidence contribute 0 pages (honest
        // undercount; coverage is surfaced in the SourceNote). The evidence ledger
        // is mapped on production PostgreSQL contexts only, so its absence reads
        // as an explicit zero, never a crash.
        long pagesProcessed = 0, ocrPages = 0;
        int jobsWithPageEvidence = 0, jobsWithPages = 0, truncatedOcrJobs = 0;
        var runLedgerMapped = _context.Model.FindEntityType(typeof(ExtractionRun)) is not null;
        if (runLedgerMapped)
        {
            var pagesPerJob = await _context.Set<ExtractionRun>().IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.BusinessUnitId == businessUnitId
                            && billableJobs.Any(j => j.Id == r.ExtractionJobId))
                .GroupBy(r => r.ExtractionJobId)
                .Select(g => new
                {
                    Pages = g.Max(r => r.PageCount),
                    OcrPages = g.Max(r => r.OcrPageCount),
                    Truncated = g.Max(r => r.OcrTruncated ? 1 : 0)
                })
                .ToListAsync(ct);
            pagesProcessed = pagesPerJob.Sum(x => (long)x.Pages);
            ocrPages = pagesPerJob.Sum(x => (long)x.OcrPages);
            jobsWithPageEvidence = pagesPerJob.Count;
            jobsWithPages = pagesPerJob.Count(x => x.Pages > 0);
            truncatedOcrJobs = pagesPerJob.Count(x => x.Truncated > 0);
        }

        // storage.gb: period-end snapshot of retained evidence bytes. ByteSize is
        // recorded at intake for every SourceDocument (constructor-required), and
        // the evidence ledger is append-only (no delete path), so "retained at
        // period end" = received before period end. Metered in raw BYTES; priced
        // per GiB (BillingMeterKeys.BytesPerGigabyte) exactly like tokens are
        // metered raw and priced per 1K.
        long storageBytes = 0;
        var storageLedgerMapped = _context.Model.FindEntityType(typeof(SourceDocument)) is not null;
        if (storageLedgerMapped)
        {
            DateTimeOffset periodEnd = period.EndUtc;
            storageBytes = await _context.Set<SourceDocument>().IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.BusinessUnitId == businessUnitId && d.CreatedOn < periodEnd)
                .SumAsync(d => (long?)d.ByteSize, ct) ?? 0L;
        }

        var externalTokens = await _context.Set<AiRequest>().IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.BusinessUnitId == businessUnitId
                        && r.ProviderClass == AiProviderClass.External
                        && r.Status == AiCallStatuses.Succeeded
                        && r.CreatedOn >= period.StartUtc && r.CreatedOn < period.EndUtc)
            .SumAsync(r => (long?)(r.InputTokens + r.OutputTokens), ct) ?? 0L;

        // P0-B2: seats are derived reproducibly from timestamps, not from the live
        // IsActive flag alone — recomputing a past period after later user churn
        // yields the same count. A user occupies a seat for the period when they
        // existed before the period ended AND were still active at period end
        // (deactivated at-or-after PeriodEnd, or never deactivated). Legacy
        // inactive rows (IsActive false, DeactivatedAtUtc null) count as
        // deactivated: IsActive != true and null >= PeriodEnd is false.
        var seats = await _context.Set<User>().IgnoreQueryFilters().AsNoTracking()
            .LongCountAsync(u => u.Buid == businessUnitId
                                 && u.CreatedOn < period.EndUtc
                                 && (u.IsActive == true || u.DeactivatedAtUtc >= period.EndUtc), ct);

        meters.Add(new MeterReading(BillingMeterKeys.Documents, documents, "document",
            $"ExtractionJobs count {period.Key}, billable statuses only (excludes Duplicate/Failed/DeadLetter) (BU {businessUnitId})"));
        meters.Add(PageMeter(BillingMeterKeys.PagesProcessed, pagesProcessed,
            runLedgerMapped
                ? $"ExtractionRuns page evidence for billable ExtractionJobs {period.Key}: MAX(PageCount) per job across retry attempts, summed; {jobsWithPageEvidence} of {documents} billable job(s) carry run evidence and {jobsWithPages} report a non-zero page count — the remainder meter 0 pages (BU {businessUnitId})"
                : $"Evidence ledger (ExtractionRuns) is not mapped in this database; pages meter reads zero for {period.Key} (BU {businessUnitId})"));
        meters.Add(PageMeter(BillingMeterKeys.PagesOcr, ocrPages,
            runLedgerMapped
                ? $"ExtractionRuns OCR evidence for billable ExtractionJobs {period.Key}: MAX(OcrPageCount) per job across retry attempts, summed (subset of pages.processed that consumed OCR)"
                  + (truncatedOcrJobs > 0
                      ? $"; {truncatedOcrJobs} job(s) hit the 10-page OCR cap (OcrTruncated) so their OCR pages are a floor"
                      : "")
                  + $" (BU {businessUnitId})"
                : $"Evidence ledger (ExtractionRuns) is not mapped in this database; OCR pages meter reads zero for {period.Key} (BU {businessUnitId})"));
        meters.Add(new MeterReading(BillingMeterKeys.AiTokensExternal, externalTokens, "token",
            $"AiRequests settled external tokens (input+output, status Succeeded) {period.Key} (BU {businessUnitId})"));
        meters.Add(new MeterReading(BillingMeterKeys.Seats, seats, "seat",
            $"Users with CreatedOn < {period.EndUtc:yyyy-MM-dd} AND (IsActive OR DeactivatedAtUtc >= {period.EndUtc:yyyy-MM-dd}) — reproducible timestamp derivation (BU {businessUnitId})"));
        meters.Add(new MeterReading(BillingMeterKeys.StorageGb, storageBytes, "byte",
            storageLedgerMapped
                ? $"SourceDocuments.ByteSize sum for documents received before {period.EndUtc:yyyy-MM-dd} (period-end snapshot {period.Key}; append-only evidence ledger, received = retained); metered in bytes, priced per GiB = {BillingMeterKeys.BytesPerGigabyte:0} bytes (BU {businessUnitId})"
                : $"Evidence ledger (SourceDocuments) is not mapped in this database; storage meter reads zero for {period.Key} (BU {businessUnitId})")
        {
            CoverageNote = StorageCoverageNote
        });

        return new TenantUsageReadout(tenantId, businessUnitId, period.Key, period.StartUtc, period.EndUtc, meters);
    }

    public async Task<BillingStatement> ComputeStatementAsync(
        long tenantId, BillingPeriod period, long? rateCardId = null, CancellationToken ct = default)
    {
        // P0: a period that has not STARTED has no usage to meter — computing it
        // would persist a fabricated all-zero Draft that the unique index then
        // makes the one statement for that period. The CURRENT period is
        // deliberately allowed: its Draft is a live preview (see FinalizeSettleLag
        // — only finalize is time-gated).
        var now = DateTime.UtcNow;
        if (period.StartUtc > now)
            throw new BillingConflictException(
                $"Billing period {period.Key} starts {period.StartUtc:yyyy-MM-dd HH:mm}Z, which is in the future; " +
                "a statement cannot be computed for a period that has not begun. " +
                $"The current period is {now:yyyy-MM}.");

        var tenant = await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
                         .Include(t => t.Plan)
                         .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
                     ?? throw new BillingNotFoundException($"Tenant {tenantId} does not exist.");

        // Fast path: a Final statement for the period is immutable — return it
        // unchanged before touching anything else.
        var finalStatement = await FindStatementAsync(tenantId, period, tracking: false, ct);
        if (finalStatement is { Status: BillingStatementStatus.Final })
            return finalStatement;

        var rateCard = await ResolveRateCardAsync(rateCardId, period, ct);

        // P0-B3 (v1 constraint): billing is USD-only. The plan's base price is
        // MonthlyPriceUsd and statement math has no FX conversion, so a non-USD
        // rate card would silently mix currencies on one statement. The API
        // rejects non-USD cards at create/update; this guard (409) covers cards
        // that predate the rule or were seeded out-of-band.
        if (!string.Equals(rateCard.Currency, "USD", StringComparison.OrdinalIgnoreCase))
            throw new BillingConflictException(
                $"Rate card {rateCard.Id} ('{rateCard.Code}') is denominated in '{rateCard.Currency}', but v1 billing is USD-only; " +
                "statements cannot be computed against a non-USD rate card.");

        var usage = await GetUsageAsync(tenantId, period, ct);
        var lines = BuildLines(rateCard, tenant.Plan, usage);
        var total = BillingMath.Round2(lines.Sum(l => l.Amount));

        var strategy = _context.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                // A failed attempt can leave generated keys tracked; every attempt
                // starts from a clean tracker (same pattern as tenant provisioning).
                _context.ChangeTracker.Clear();
                await using var tx = await _context.Database.BeginTransactionAsync(ct);

                var existing = await FindStatementAsync(tenantId, period, tracking: true, ct);
                if (existing is { Status: BillingStatementStatus.Final })
                    return existing; // finalized while we were computing — immutable.

                BillingStatement statement;
                if (existing is null)
                {
                    statement = new BillingStatement
                    {
                        TenantId = tenantId,
                        PeriodStartUtc = period.StartUtc,
                        PeriodEndUtc = period.EndUtc,
                        RateCardId = rateCard.Id,
                        Currency = rateCard.Currency,
                        Status = BillingStatementStatus.Draft,
                        TotalAmount = total,
                        ComputedAtUtc = DateTime.UtcNow
                    };
                    foreach (var line in lines)
                        statement.Lines.Add(line);
                    _context.Set<BillingStatement>().Add(statement);
                }
                else
                {
                    // Recompute replaces the Draft's lines atomically inside this
                    // transaction; totals stay stable for identical inputs.
                    statement = existing;
                    _context.Set<BillingStatementLine>().RemoveRange(statement.Lines);
                    statement.Lines.Clear();
                    foreach (var line in lines)
                        statement.Lines.Add(line);
                    statement.RateCardId = rateCard.Id;
                    statement.Currency = rateCard.Currency;
                    statement.PeriodEndUtc = period.EndUtc;
                    statement.TotalAmount = total;
                    statement.ComputedAtUtc = DateTime.UtcNow;
                    statement.Version++;
                }

                await _context.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return statement;
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            // P2-B8/A5: a rival finalize (or recompute) bumped Version between our
            // tracked read and SaveChanges. Mirror the insert-race contract:
            // reload and return the current row — if it is now Final it is
            // immutable; if it is a rival Draft, compute is idempotent and the
            // caller can simply recompute.
            _logger.LogInformation(
                "Statement compute for tenant {TenantId} period {Period} lost a concurrent version race; returning the current statement.",
                tenantId, period.Key);
            _context.ChangeTracker.Clear();
            return await FindStatementAsync(tenantId, period, tracking: false, ct)
                   ?? throw new BillingConflictException(
                       $"Statement compute for tenant {tenantId} period {period.Key} hit a concurrency conflict but no statement row exists.");
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost a concurrent-insert race on UX_BillingStatements_Tenant_PeriodStart:
            // the unique index is the duplicate-charge guard — return the winning row.
            _logger.LogInformation(
                "Statement compute for tenant {TenantId} period {Period} lost a concurrent insert race; returning the existing statement.",
                tenantId, period.Key);
            _context.ChangeTracker.Clear();
            return await FindStatementAsync(tenantId, period, tracking: false, ct)
                   ?? throw new BillingConflictException(
                       $"Statement upsert for tenant {tenantId} period {period.Key} hit a unique violation but no statement row exists.");
        }
    }

    public async Task<BillingStatement> FinalizeAsync(
        long statementId, string actor,
        Func<BillingStatement, CancellationToken, Task>? onFinalized = null,
        CancellationToken ct = default)
    {
        // Two attempts: attempt 1 races freely; if a concurrent compute bumps
        // Version mid-finalize (P2-B8/A5) we catch DbUpdateConcurrencyException,
        // reload the current row and finalize THAT. A rival finalize is absorbed
        // by the idempotent already-Final fast path on the retry.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            _context.ChangeTracker.Clear();
            var statement = await _context.Set<BillingStatement>()
                                .Include(s => s.Lines)
                                .FirstOrDefaultAsync(s => s.Id == statementId, ct)
                            ?? throw new BillingNotFoundException($"Billing statement {statementId} does not exist.");

            if (statement.Status == BillingStatementStatus.Final)
                return statement; // idempotent — a Final statement is immutable (no audit callback: nothing changed).

            // P0: refuse to freeze a period that is still open (or still settling).
            // Checked AFTER the already-Final fast path so re-finalizing an
            // existing Final statement stays idempotent rather than 409-ing.
            var earliestFinalizeUtc = statement.PeriodEndUtc + FinalizeSettleLag;
            if (earliestFinalizeUtc > DateTime.UtcNow)
                throw new BillingConflictException(
                    $"Billing statement {statementId} covers period " +
                    $"{statement.PeriodStartUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture)}, which ends " +
                    $"{statement.PeriodEndUtc:yyyy-MM-dd HH:mm}Z. Finalizing is permanent — the unique index blocks a " +
                    "second statement for the period and the immutability trigger blocks correcting this one — so a " +
                    $"period is only finalizable once it has closed AND cleared the {FinalizeSettleLag.TotalHours:0}h " +
                    "settle lag that lets late AI reconciliation and dead-letter re-drives land. " +
                    $"Earliest finalize time: {earliestFinalizeUtc:yyyy-MM-dd HH:mm}Z. " +
                    "The Draft stays recomputable until then.");

            statement.Status = BillingStatementStatus.Final;
            statement.FinalizedAtUtc = DateTime.UtcNow;
            statement.FinalizedBy = actor;
            statement.Version++;

            try
            {
                var strategy = _context.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                    await using var tx = await _context.Database.BeginTransactionAsync(ct);
                    await _context.SaveChangesAsync(ct);
                    // Sec3: the finalize audit runs INSIDE this transaction so the
                    // Draft→Final flip and its audit row commit atomically — an
                    // audit failure rolls the finalize back. (The COMPUTE audit
                    // stays post-commit in the controller: compute is idempotent,
                    // so a lost audit there is safely re-emitted by a retry.)
                    if (onFinalized is not null)
                        await onFinalized(statement, ct);
                    await tx.CommitAsync(ct);
                    return statement;
                });
            }
            catch (DbUpdateConcurrencyException)
            {
                _logger.LogInformation(
                    "Finalize of statement {StatementId} lost a concurrent version race (attempt {Attempt}); reloading the current row.",
                    statementId, attempt + 1);
            }
        }

        // Both attempts lost the version race: return the current row (Final
        // rows are immutable; anything else means pathological contention).
        _context.ChangeTracker.Clear();
        var current = await _context.Set<BillingStatement>().AsNoTracking()
                          .Include(s => s.Lines)
                          .FirstOrDefaultAsync(s => s.Id == statementId, ct)
                      ?? throw new BillingNotFoundException($"Billing statement {statementId} does not exist.");
        if (current.Status == BillingStatementStatus.Final)
            return current;
        throw new BillingConflictException(
            $"Billing statement {statementId} could not be finalized: concurrent updates kept changing it. Retry the finalize.");
    }

    public async Task<TenantCostReport> GetCostAsync(
        long tenantId, BillingPeriod period, CancellationToken ct = default)
    {
        var tenant = await RequireTenantAsync(tenantId, ct);
        var bu = tenant.PrimaryBusinessUnitId;

        var statement = await FindStatementAsync(tenantId, period, tracking: false, ct);

        var settledCount = 0;
        var unpricedCount = 0;
        var pricedSubtotal = 0m;
        var unreconciledCount = 0;
        var unreconciledTokens = 0L;

        if (bu is long businessUnitId)
        {
            var settled = await _context.Set<AiRequest>().IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.BusinessUnitId == businessUnitId
                            && r.Status == AiCallStatuses.Succeeded
                            && r.CreatedOn >= period.StartUtc && r.CreatedOn < period.EndUtc)
                .Select(r => new { r.EstimatedCost, r.CostStatus })
                .ToListAsync(ct);

            settledCount = settled.Count;
            foreach (var request in settled)
            {
                var priceable = request.EstimatedCost is not null
                                && request.CostStatus is AiCostStatuses.Priced or AiCostStatuses.EstimatedConfiguredRate;
                if (priceable)
                    pricedSubtotal += request.EstimatedCost!.Value;
                else
                    unpricedCount++;
            }

            // P2-B6: Unknown/Failed EXTERNAL requests still burned provider spend
            // (the provider bills us regardless of our outcome bookkeeping), but
            // they are never settled so they are invisible to the cost side above.
            // Surface them separately so margin is not silently overstated.
            var unreconciledQuery = _context.Set<AiRequest>().IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.BusinessUnitId == businessUnitId
                            && r.ProviderClass == AiProviderClass.External
                            && (r.Status == AiCallStatuses.Unknown || r.Status == AiCallStatuses.Failed)
                            && r.CreatedOn >= period.StartUtc && r.CreatedOn < period.EndUtc);
            unreconciledCount = await unreconciledQuery.CountAsync(ct);
            unreconciledTokens = await unreconciledQuery
                .SumAsync(r => (long?)(r.InputTokens + r.OutputTokens), ct) ?? 0L;
        }

        var costComplete = unpricedCount == 0;
        decimal? aiCostTotal = costComplete ? BillingMath.Round2(pricedSubtotal) : null;
        decimal? statementTotal = statement?.TotalAmount;
        decimal? grossMargin = aiCostTotal is not null && statementTotal is not null
            ? BillingMath.Round2(statementTotal.Value - aiCostTotal.Value)
            : null;

        var note = !costComplete
            ? $"AI cost is incomplete: {unpricedCount} settled request(s) carry cost status RateUnavailable/LocalUnpriced or no recorded cost. Cost total and margin are withheld, not fabricated."
            : statement is null
                ? "No billing statement exists for this period yet; margin requires a computed statement."
                : "AI cost fully priced from AiRequests.EstimatedCost (Priced/EstimatedConfiguredRate).";
        if (unreconciledCount > 0)
            note += $" {unreconciledCount} external request(s) ({unreconciledTokens} recorded tokens) are Unknown/Failed and carry unpriced provider spend excluded from the cost total; true margin is lower than reported.";

        return new TenantCostReport(
            tenantId, bu, period.Key,
            statementTotal, statement?.Status.ToString(), statement?.Currency,
            settledCount, unpricedCount,
            BillingMath.Round2(pricedSubtotal), aiCostTotal, grossMargin, note)
        {
            UnreconciledExternalRequestCount = unreconciledCount,
            UnreconciledExternalTokens = unreconciledTokens
        };
    }

    /// <summary>
    /// Page meters carry the audited signal-coverage verdict: the aggregation is
    /// exact, the underlying pipeline instrumentation is not (see
    /// <see cref="PageSignalCoverageNote"/>), so they read as a floor and are
    /// flagged not-billing-ready on the wire.
    /// </summary>
    private static MeterReading PageMeter(string meterKey, decimal quantity, string sourceNote)
        => new(meterKey, quantity, "page", sourceNote)
        {
            SignalCoverage = MeterSignalCoverage.Incomplete,
            BillingReady = false,
            CoverageNote = PageSignalCoverageNote
        };

    private async Task<Tenant> RequireTenantAsync(long tenantId, CancellationToken ct)
        => await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
               .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
           ?? throw new BillingNotFoundException($"Tenant {tenantId} does not exist.");

    private async Task<BillingStatement?> FindStatementAsync(
        long tenantId, BillingPeriod period, bool tracking, CancellationToken ct)
    {
        var query = _context.Set<BillingStatement>().Include(s => s.Lines).AsQueryable();
        if (!tracking)
            query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(
            s => s.TenantId == tenantId && s.PeriodStartUtc == period.StartUtc, ct);
    }

    private async Task<RateCard> ResolveRateCardAsync(
        long? rateCardId, BillingPeriod period, CancellationToken ct)
    {
        if (rateCardId is long id)
            return await _context.Set<RateCard>().AsNoTracking().Include(c => c.Lines)
                       .FirstOrDefaultAsync(c => c.Id == id, ct)
                   ?? throw new BillingNotFoundException($"Rate card {id} does not exist.");

        return await _context.Set<RateCard>().AsNoTracking().Include(c => c.Lines)
                   .Where(c => c.IsActive
                               && c.EffectiveFromUtc <= period.StartUtc
                               && (c.EffectiveToUtc == null || c.EffectiveToUtc > period.StartUtc))
                   .OrderByDescending(c => c.EffectiveFromUtc).ThenByDescending(c => c.Id)
                   .FirstOrDefaultAsync(ct)
               ?? throw new BillingConflictException(
                   $"No active rate card is effective for period {period.Key}; pass rateCardId explicitly or activate one.");
    }

    /// <summary>
    /// Priced-unit divisor per meter: some meters are metered in raw units but
    /// priced per bulk unit — "ai.tokens.external" meters tokens and prices per
    /// 1K tokens; "storage.gb" meters bytes and prices per GiB (1024^3 bytes).
    /// Every other meter prices 1:1.
    /// </summary>
    internal static decimal PricedUnitDivisor(string meterKey) => meterKey switch
    {
        BillingMeterKeys.AiTokensExternal => BillingMeterKeys.TokensPerPricedUnit,
        BillingMeterKeys.StorageGb => BillingMeterKeys.BytesPerGigabyte,
        _ => 1m
    };

    /// <summary>
    /// Pure charge math: billable = max(0, metered - included); amount =
    /// billable ÷ <see cref="PricedUnitDivisor"/> × unitPrice (divisor 1 for
    /// most meters; 1K for external tokens; 1 GiB for storage bytes). All
    /// amounts round to 2dp away-from-zero. Meters are ADDITIVE: only meters
    /// with a rate-card line produce a charge line — usage on a meter the card
    /// does not price is simply not charged.
    /// </summary>
    internal static List<BillingStatementLine> BuildLines(
        RateCard rateCard, Plan? plan, TenantUsageReadout usage)
    {
        var lines = new List<BillingStatementLine>();

        // Base subscription from the plan's monthly price when available.
        if (plan is not null)
        {
            // P2-B11: direct property access — Plan.MonthlyPriceUsd landed with WS-B.
            var monthlyPrice = plan.MonthlyPriceUsd;
            lines.Add(new BillingStatementLine
            {
                MeterKey = BillingMeterKeys.BaseSubscription,
                Description = $"Base subscription — plan '{plan.Code}'",
                MeteredQuantity = 1m,
                IncludedQuantity = 0m,
                BillableQuantity = 1m,
                UnitPrice = monthlyPrice ?? 0m,
                Amount = BillingMath.Round2(monthlyPrice ?? 0m),
                SourceNote = monthlyPrice is null
                    ? $"Plan '{plan.Code}' has no monthly price configured; base charge 0."
                    : $"Plan '{plan.Code}' monthly subscription price."
            });
        }

        var meterByKey = usage.Meters.ToDictionary(m => m.MeterKey, StringComparer.Ordinal);
        foreach (var cardLine in rateCard.Lines.OrderBy(l => l.MeterKey, StringComparer.Ordinal))
        {
            meterByKey.TryGetValue(cardLine.MeterKey, out var meter);
            var metered = meter?.Quantity ?? 0m;
            var included = cardLine.IncludedQuantity;
            var billable = Math.Max(0m, metered - included);
            var amount = BillingMath.Round2(billable / PricedUnitDivisor(cardLine.MeterKey) * cardLine.UnitPrice);

            lines.Add(new BillingStatementLine
            {
                MeterKey = cardLine.MeterKey,
                Description = $"{cardLine.MeterKey} — {cardLine.Unit}"
                              + (string.IsNullOrWhiteSpace(cardLine.TierNote) ? "" : $" ({cardLine.TierNote})"),
                MeteredQuantity = metered,
                IncludedQuantity = included,
                BillableQuantity = billable,
                UnitPrice = cardLine.UnitPrice,
                Amount = amount,
                // Traceability: SourceNote carries provenance (source ledger +
                // period + BU) and nothing else. The meter's coverage caveat
                // travels in its OWN field — a priced line must never look more
                // trustworthy than its signal, but the caveat is structured data,
                // not prose glued onto provenance, so it stays separately
                // readable (and separately queryable) on the line and on the wire.
                SourceNote = meter is null
                    ? $"No source ledger backs meter '{cardLine.MeterKey}'; metered 0 for {usage.Period}."
                    : meter.SourceNote,
                CoverageNote = string.IsNullOrEmpty(meter?.CoverageNote) ? null : meter.CoverageNote
            });
        }

        return lines;
    }

    internal static bool IsUniqueViolation(DbUpdateException exception)
    {
        for (Exception? inner = exception; inner is not null; inner = inner.InnerException)
        {
            var message = inner.Message;
            if (message.Contains("23505", StringComparison.Ordinal) // PostgreSQL unique_violation
                || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
                || message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

internal static class BillingMath
{
    /// <summary>Money rounding used everywhere in billing: 2dp, away-from-zero.</summary>
    public static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

