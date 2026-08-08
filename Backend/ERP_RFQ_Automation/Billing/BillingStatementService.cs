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

    /// <summary>
    /// Lower bound actually applied to the FLOW meters (documents, pages, external tokens).
    /// Equal to <see cref="PeriodStartUtc"/> for a normal period; the tenant's billing start
    /// date when the period straddles it. Seats and storage are period-end snapshots and
    /// ignore it — each of those lines carries the caveat in its own coverage note.
    /// </summary>
    public DateTime MeteredFromUtc { get; init; }
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
        long tenantId, BillingPeriod period, long? rateCardId = null, CancellationToken ct = default,
        string computedBy = "system:billing-run");

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

    /// <summary>
    /// Fleet-wide revenue posture: per tenant, how it is charged, against which plan and
    /// pinned rate card, whether its most recent statement moved any money, its trial
    /// state, and every reason it may be running free. Archived tenants are offboarded
    /// and excluded unless <paramref name="includeArchived"/> says otherwise.
    /// </summary>
    Task<IReadOnlyList<TenantRevenueRisk>> GetRevenueRiskAsync(
        bool includeArchived = false, CancellationToken ct = default);
}

/// <summary>
/// What a statement is permitted to charge, decided once from the tenant's commercial
/// terms and then applied uniformly to every line.
///
/// <para>It exists because the charge decision is NOT per line and never was: a Trial
/// tenant's document line and its seat line are waived for the same single reason, and
/// deciding that once — instead of re-deriving it inside each branch of the line loop —
/// is what keeps "why is this zero?" answerable from one place.</para>
/// </summary>
public sealed record BillingChargePolicy(
    TenantBillingMode Mode,
    bool Charging,
    string? SuppressionMarker,
    DateTime? BillingStartsOn,
    DateTime? TrialEndsOn,
    bool TrialExpired,
    RateCardSource RateCardSource)
{
    /// <summary>
    /// First instant of the period that this tenant is charged for. Equal to the period
    /// start for a full period; the billing start date for a period that straddles it;
    /// null when nothing in the period is chargeable.
    /// </summary>
    public DateTime? ChargeableFromUtc { get; init; }

    /// <summary>Whole days of the period that are charged.</summary>
    public int BillableDays { get; init; }

    /// <summary>Whole days in the period (28–31).</summary>
    public int PeriodDays { get; init; }

    /// <summary>True when the period straddles <see cref="BillingStartsOn"/> and is charged pro rata.</summary>
    public bool IsProrated => Charging && BillableDays > 0 && BillableDays < PeriodDays;

    /// <summary>
    /// Fraction of the period that is charged, as an exact rational — NOT pre-rounded, so
    /// money is computed once from the true ratio instead of from a display value.
    /// </summary>
    public decimal BillableFraction
        => PeriodDays <= 0 ? 1m : (decimal)BillableDays / PeriodDays;

    /// <summary>
    /// Derives the policy for one tenant-period.
    ///
    /// <para><b>A period that straddles BillingStartsOn is PRORATED by days.</b> The two
    /// alternatives are both wrong in ways a customer notices. Charging the full month
    /// over-bills someone who signed on the 20th, and the first time they check it becomes a
    /// refund and a credibility problem. Charging nothing — which this did before — hands
    /// them up to a whole month free, which is the exact failure this billing work exists to
    /// close. Days are counted whole and inclusive of the start date: a billing start of the
    /// 20th in a 31-day month bills 12 days, the 20th through the 31st.</para>
    ///
    /// <para>The date is truncated to midnight UTC deliberately. Billing starts on a DAY,
    /// not at a signing timestamp, so two customers who signed on the same date pay the same
    /// amount regardless of what time the operator got to the form.</para>
    /// </summary>
    public static BillingChargePolicy For(
        Tenant tenant, RateCardSource rateCardSource, BillingPeriod period, DateTime nowUtc)
    {
        var trialExpired = RevenueLeakEvaluator.IsTrialExpired(tenant, nowUtc);
        var periodDays = (int)Math.Round((period.EndUtc - period.StartUtc).TotalDays);

        if (tenant.BillingMode != TenantBillingMode.Billable)
            return new BillingChargePolicy(
                tenant.BillingMode, Charging: false,
                BillingStatementMarkers.ExemptionFor(tenant.BillingMode),
                tenant.BillingStartsOn, tenant.TrialEndsOn, trialExpired, rateCardSource)
            {
                ChargeableFromUtc = null,
                BillableDays = 0,
                PeriodDays = periodDays
            };

        // No start date, or one already passed when the period opened: the whole period bills.
        var startsOn = tenant.BillingStartsOn?.Date;
        if (startsOn is null || startsOn <= period.StartUtc)
            return new BillingChargePolicy(
                TenantBillingMode.Billable, Charging: true, SuppressionMarker: null,
                tenant.BillingStartsOn, tenant.TrialEndsOn, trialExpired, rateCardSource)
            {
                ChargeableFromUtc = period.StartUtc,
                BillableDays = periodDays,
                PeriodDays = periodDays
            };

        // Billing begins at or after this period ends: nothing here is chargeable at all.
        if (startsOn >= period.EndUtc)
            return new BillingChargePolicy(
                TenantBillingMode.Billable, Charging: false,
                BillingStatementMarkers.ExemptionPreBillingStart,
                tenant.BillingStartsOn, tenant.TrialEndsOn, trialExpired, rateCardSource)
            {
                ChargeableFromUtc = null,
                BillableDays = 0,
                PeriodDays = periodDays
            };

        var chargeableFrom = DateTime.SpecifyKind(startsOn.Value, DateTimeKind.Utc);
        return new BillingChargePolicy(
            TenantBillingMode.Billable, Charging: true, SuppressionMarker: null,
            tenant.BillingStartsOn, tenant.TrialEndsOn, trialExpired, rateCardSource)
        {
            ChargeableFromUtc = chargeableFrom,
            BillableDays = (int)Math.Round((period.EndUtc - chargeableFrom).TotalDays),
            PeriodDays = periodDays
        };
    }
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

    /// <summary>
    /// Why the seats and storage meters do not shrink when a period is only partly billed.
    ///
    /// <para>Both are period-END SNAPSHOTS of a stock, not sums of a flow: "users holding a
    /// seat on the last day" and "bytes still retained on the last day". A stock has no
    /// sub-period to bound — a seat occupied on the final day was occupied on the final day
    /// whether billing began on the 1st or the 20th. So these two are charged for the whole
    /// period even when the base subscription is prorated, and the line says so rather than
    /// letting the reader assume the boundary was applied uniformly.</para>
    ///
    /// <para>The flow meters — documents, pages and external AI tokens — ARE bounded exactly,
    /// because every one of them is attributed to the period by a row timestamp that can take
    /// a lower bound.</para>
    /// </summary>
    internal const string PeriodEndSnapshotProrationNote =
        "NOT PRORATED — this meter is a period-end snapshot of a stock, not a sum over the billed days, " +
        "so it cannot be bounded to the part of the period that is charged. The quantity covers the whole period " +
        "even though the base subscription is charged pro rata from the billing start date. " +
        "The flow meters on this statement (documents, pages, external AI tokens) ARE counted from the billing start date only.";

    private readonly ErpRfqAutomationContext _context;
    private readonly ILogger<BillingStatementService> _logger;

    public BillingStatementService(ErpRfqAutomationContext context, ILogger<BillingStatementService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public Task<TenantUsageReadout> GetUsageAsync(
        long tenantId, BillingPeriod period, CancellationToken ct = default)
        => GetUsageAsync(tenantId, period, meterFromUtc: null, ct);

    /// <summary>
    /// The metering core, with an optional lower bound on the flow meters.
    ///
    /// <para><paramref name="meterFromUtc"/> is how a prorated period is metered: the
    /// documents, pages and external-token meters take it as their lower bound so a
    /// customer whose billing began on the 20th is not charged for work done on the 3rd.
    /// It is deliberately NOT applied to seats or storage — see
    /// <see cref="PeriodEndSnapshotProrationNote"/> — and those two lines say so.</para>
    ///
    /// <para>The public readout passes null, so the usage screen keeps showing what the
    /// tenant actually consumed over the whole period. That is the honest answer to "what
    /// did they use"; the bounded reading answers a different question, "what may we charge
    /// for", and conflating the two is how a usage screen starts disagreeing with an invoice
    /// for reasons nobody can explain.</para>
    /// </summary>
    internal async Task<TenantUsageReadout> GetUsageAsync(
        long tenantId, BillingPeriod period, DateTime? meterFromUtc, CancellationToken ct)
    {
        var meterFrom = meterFromUtc is DateTime bound && bound > period.StartUtc ? bound : period.StartUtc;
        var bounded = meterFrom > period.StartUtc;
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
            return new TenantUsageReadout(tenantId, null, period.Key, period.StartUtc, period.EndUtc, meters)
            {
                MeteredFromUtc = meterFrom
            };
        }

        // P0-B1: bill only delivered-or-in-flight work. Failed/DeadLetter jobs are
        // excluded here AND from the docs/month quota (BillableDocumentPolicy);
        // the Duplicate exclusion in that policy is defensive only — no code path
        // assigns that status today, because de-duplication happens at enqueue and
        // a duplicate submission never creates a second job.
        //
        // Flow meter #1, and the lower bound is meterFrom rather than the period start: a
        // job is attributed to a period by CreatedOn, so the same timestamp that places it
        // in the period also places it before or after the billing start date.
        var nonBillable = BillableDocumentPolicy.NonBillableStatuses;
        var billableJobs = _context.Set<ExtractionJob>().IgnoreQueryFilters().AsNoTracking()
            .Where(j => j.BusinessUnitId == businessUnitId
                        && !nonBillable.Contains(j.Status)
                        && j.CreatedOn >= meterFrom && j.CreatedOn < period.EndUtc);
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

        // Flow meter #3, bounded on the same CreatedOn that attributes it to the period.
        var externalTokens = await _context.Set<AiRequest>().IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.BusinessUnitId == businessUnitId
                        && r.ProviderClass == AiProviderClass.External
                        && r.Status == AiCallStatuses.Succeeded
                        && r.CreatedOn >= meterFrom && r.CreatedOn < period.EndUtc)
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

        // The billed-window suffix names the exact lower bound on every flow meter's
        // provenance, so "why is this month's document count lower than the usage screen's"
        // is answerable from the statement instead of from someone's memory of the contract.
        var window = bounded
            ? $" counted from {meterFrom:yyyy-MM-dd} (the tenant's billing start date), not from the start of the period"
            : "";

        meters.Add(new MeterReading(BillingMeterKeys.Documents, documents, "document",
            $"ExtractionJobs count {period.Key}, billable statuses only (excludes Duplicate/Failed/DeadLetter){window} (BU {businessUnitId})"));
        meters.Add(PageMeter(BillingMeterKeys.PagesProcessed, pagesProcessed,
            runLedgerMapped
                ? $"ExtractionRuns page evidence for billable ExtractionJobs {period.Key}: MAX(PageCount) per job across retry attempts, summed; {jobsWithPageEvidence} of {documents} billable job(s) carry run evidence and {jobsWithPages} report a non-zero page count — the remainder meter 0 pages{window} (BU {businessUnitId})"
                : $"Evidence ledger (ExtractionRuns) is not mapped in this database; pages meter reads zero for {period.Key} (BU {businessUnitId})"));
        meters.Add(PageMeter(BillingMeterKeys.PagesOcr, ocrPages,
            runLedgerMapped
                ? $"ExtractionRuns OCR evidence for billable ExtractionJobs {period.Key}: MAX(OcrPageCount) per job across retry attempts, summed (subset of pages.processed that consumed OCR)"
                  + (truncatedOcrJobs > 0
                      ? $"; {truncatedOcrJobs} job(s) hit the 10-page OCR cap (OcrTruncated) so their OCR pages are a floor"
                      : "")
                  + $"{window} (BU {businessUnitId})"
                : $"Evidence ledger (ExtractionRuns) is not mapped in this database; OCR pages meter reads zero for {period.Key} (BU {businessUnitId})"));
        meters.Add(new MeterReading(BillingMeterKeys.AiTokensExternal, externalTokens, "token",
            $"AiRequests settled external tokens (input+output, status Succeeded) {period.Key}{window} (BU {businessUnitId})"));
        // Seats and storage are period-END SNAPSHOTS of a stock. There is no sub-period to
        // bound, so when the period is only partly billed they are the two lines that do NOT
        // shrink — and they say so instead of leaving a reader to assume the boundary was
        // applied everywhere.
        meters.Add(new MeterReading(BillingMeterKeys.Seats, seats, "seat",
            $"Users with CreatedOn < {period.EndUtc:yyyy-MM-dd} AND (IsActive OR DeactivatedAtUtc >= {period.EndUtc:yyyy-MM-dd}) — reproducible timestamp derivation (BU {businessUnitId})")
        {
            CoverageNote = bounded ? PeriodEndSnapshotProrationNote : null
        });
        meters.Add(new MeterReading(BillingMeterKeys.StorageGb, storageBytes, "byte",
            storageLedgerMapped
                ? $"SourceDocuments.ByteSize sum for documents received before {period.EndUtc:yyyy-MM-dd} (period-end snapshot {period.Key}; append-only evidence ledger, received = retained); metered in bytes, priced per GiB = {BillingMeterKeys.BytesPerGigabyte:0} bytes (BU {businessUnitId})"
                : $"Evidence ledger (SourceDocuments) is not mapped in this database; storage meter reads zero for {period.Key} (BU {businessUnitId})")
        {
            CoverageNote = bounded
                ? StorageCoverageNote + " " + PeriodEndSnapshotProrationNote
                : StorageCoverageNote
        });

        return new TenantUsageReadout(tenantId, businessUnitId, period.Key, period.StartUtc, period.EndUtc, meters)
        {
            MeteredFromUtc = meterFrom
        };
    }

    public async Task<BillingStatement> ComputeStatementAsync(
        long tenantId, BillingPeriod period, long? rateCardId = null, CancellationToken ct = default,
        string computedBy = "system:billing-run")
    {
        computedBy = string.IsNullOrWhiteSpace(computedBy)
            ? throw new ArgumentException("A calculating actor is required.", nameof(computedBy))
            : computedBy.Trim();
        if (computedBy.Length > 256)
            throw new ArgumentException("The calculating actor cannot exceed 256 characters.", nameof(computedBy));
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

        var (rateCard, rateCardSource) = await ResolveRateCardAsync(rateCardId, tenant, period, ct);

        // P0-B3 (v1 constraint): billing is USD-only. The plan's base price is
        // MonthlyPriceUsd and statement math has no FX conversion, so a non-USD
        // rate card would silently mix currencies on one statement. The API
        // rejects non-USD cards at create/update; this guard (409) covers cards
        // that predate the rule or were seeded out-of-band.
        if (!string.Equals(rateCard.Currency, "USD", StringComparison.OrdinalIgnoreCase))
            throw new BillingConflictException(
                $"Rate card {rateCard.Id} ('{rateCard.Code}') is denominated in '{rateCard.Currency}', but v1 billing is USD-only; " +
                "statements cannot be computed against a non-USD rate card.");

        var policy = BillingChargePolicy.For(tenant, rateCardSource, period, now);
        // The flow meters are metered from the first chargeable instant, so a prorated
        // period never prices work the tenant did before its billing start date.
        var usage = await GetUsageAsync(tenantId, period, policy.ChargeableFromUtc, ct);
        var lines = BuildLines(rateCard, tenant.Plan, usage, policy);
        var total = BillingMath.Round2(lines.Sum(l => l.Amount));

        // A Billable tenant whose statement moves no money is the headline defect
        // this whole file exists to prevent, so it is stated at WARNING even though
        // the statement itself already carries the marker line. The console readout
        // is where an operator goes looking; the log is what tells them to look.
        if (policy.Mode == TenantBillingMode.Billable && total <= 0m)
            _logger.LogWarning(
                "REVENUE RISK: billable tenant {TenantId} computed a {Total} {Currency} statement for period {Period}. "
                + "Markers on the statement: {Markers}.",
                tenantId, total, rateCard.Currency, period.Key,
                string.Join(", ", lines
                    .Select(l => BillingStatementMarkers.RiskCodeOf(l.MeterKey, l.CoverageNote))
                    .Where(code => code is not null)
                    .DefaultIfEmpty("none — usage itself was zero")));

        if (policy.TrialExpired)
            _logger.LogWarning(
                "REVENUE RISK: tenant {TenantId} is still in Trial billing mode but its trial ended {TrialEndsOn:yyyy-MM-dd}; "
                + "period {Period} was metered and not charged. The account needs converting to a Billable mode.",
                tenantId, policy.TrialEndsOn, period.Key);

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
                        ComputedAtUtc = DateTime.UtcNow,
                        ComputedBy = computedBy
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
                    statement.ComputedBy = computedBy;
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

            // Revenue-control maker/checker: the operator who last calculated the
            // mutable Draft cannot also perform its irreversible final approval.
            // Legacy rows are backfilled as system:legacy by the migration, so an
            // Owner may governably approve them without being misidentified as maker.
            if (string.Equals(statement.ComputedBy, actor?.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new BillingConflictException(
                    $"Billing statement {statementId} was calculated by '{statement.ComputedBy}'. " +
                    "A different Platform Owner must review and finalize it.");

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

    public async Task<IReadOnlyList<TenantRevenueRisk>> GetRevenueRiskAsync(
        bool includeArchived = false, CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;

        // Platform plane: deliberate cross-tenant read, same as every other query here.
        var tenantQuery = _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking().Include(t => t.Plan);
        var tenants = includeArchived
            ? await tenantQuery.OrderBy(t => t.Id).ToListAsync(ct)
            : await tenantQuery.Where(t => t.Status != TenantStatus.Archived).OrderBy(t => t.Id).ToListAsync(ct);
        if (tenants.Count == 0)
            return Array.Empty<TenantRevenueRisk>();

        var tenantIds = tenants.Select(t => t.Id).ToList();
        // Statements are loaded WITHOUT their lines: the readout answers "did this move
        // money", which is the header, and pulling every line of every statement in the
        // fleet to answer it would make the console page the most expensive query we own.
        var statements = await _context.Set<BillingStatement>().AsNoTracking()
            .Where(s => tenantIds.Contains(s.TenantId))
            .ToListAsync(ct);
        var latestByTenant = statements
            .GroupBy(s => s.TenantId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.PeriodStartUtc).First());

        var pinnedCardIds = tenants.Where(t => t.RateCardId.HasValue).Select(t => t.RateCardId!.Value).Distinct().ToList();
        var pinnedCards = pinnedCardIds.Count == 0
            ? new Dictionary<long, RateCard>()
            : (await _context.Set<RateCard>().AsNoTracking()
                .Where(c => pinnedCardIds.Contains(c.Id))
                .ToListAsync(ct)).ToDictionary(c => c.Id);

        return tenants.Select(tenant =>
        {
            latestByTenant.TryGetValue(tenant.Id, out var latest);
            RateCard? pinned = null;
            if (tenant.RateCardId is long pinnedId)
                pinnedCards.TryGetValue(pinnedId, out pinned);
            return RevenueLeakEvaluator.Describe(tenant, tenant.Plan, pinned, latest, nowUtc);
        }).ToList();
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

    /// <summary>
    /// Which price list this tenant is charged on, in strict precedence: the card named
    /// explicitly for this run → the card PINNED to the tenant → "whichever active card
    /// is effective for the period".
    ///
    /// <para>The pin comes before the fallback because the fallback silently reprices
    /// customers: a tenant on negotiated terms was charged against whatever card happened
    /// to be active when its statement computed, so activating a new price list changed
    /// what every un-pinned customer paid, with no record that anything had changed. The
    /// pin is the only representation of "the price list this customer agreed to".</para>
    ///
    /// <para>A DANGLING pin — <c>RateCardId</c> pointing at a card that no longer exists —
    /// is refused rather than quietly resolved by the fallback. There is no foreign key
    /// behind the pin (the billing aggregate sits above the platform model), so a dangling
    /// pin is reachable, and the fallback would answer it by charging the customer on a
    /// price list nobody chose for them. Refusing produces no statement, which the revenue
    /// readout reports as a leak; repricing produces an invoice the customer disputes.
    /// A delayed bill is recoverable, a wrong one is a credit note and a phone call.</para>
    ///
    /// <para>The fallback survives for tenants provisioned before pinning existed, and is
    /// logged at WARNING for a Billable tenant precisely because it is not normal
    /// operation — it is a finding with an owner and a fix (pin the card).</para>
    /// </summary>
    private async Task<(RateCard Card, RateCardSource Source)> ResolveRateCardAsync(
        long? rateCardId, Tenant tenant, BillingPeriod period, CancellationToken ct)
    {
        if (rateCardId is long explicitId)
        {
            if (tenant.RateCardId is long agreedId && agreedId != explicitId)
                throw new BillingConflictException(
                    $"Tenant {tenant.Id} is pinned to agreed rate card {agreedId}. Statement compute "
                    + $"cannot substitute rate card {explicitId}; change the tenant's commercial "
                    + "assignment through its audited workflow first.");

            var explicitCard = await LoadRateCardAsync(explicitId, ct)
                ?? throw new BillingNotFoundException($"Rate card {explicitId} does not exist.");
            if (!explicitCard.IsActive
                || explicitCard.EffectiveFromUtc > period.StartUtc
                || (explicitCard.EffectiveToUtc is DateTime ends && ends <= period.StartUtc))
                throw new BillingConflictException(
                    $"Rate card {explicitId} is not active and effective for period {period.Key}. "
                    + "An explicit identifier cannot bypass commercial validity dates.");
            return (explicitCard, RateCardSource.Explicit);
        }

        if (tenant.RateCardId is long pinnedId)
        {
            var pinned = await LoadRateCardAsync(pinnedId, ct);
            if (pinned is not null)
            {
                if (!pinned.IsActive
                    || pinned.EffectiveFromUtc > period.StartUtc
                    || (pinned.EffectiveToUtc is DateTime ends && ends <= period.StartUtc))
                    throw new BillingConflictException(
                        $"Tenant {tenant.Id}'s pinned rate card {pinnedId} is not active and effective for "
                        + $"period {period.Key}. Correct the commercial assignment before computing.");
                return (pinned, RateCardSource.TenantPin);
            }

            _logger.LogError(
                "Tenant {TenantId} is pinned to rate card {RateCardId}, which does not exist. "
                + "Statement compute for period {Period} is refused rather than repricing the tenant onto the active card.",
                tenant.Id, pinnedId, period.Key);
            throw new BillingConflictException(
                $"Tenant {tenant.Id} is pinned to rate card {pinnedId}, which does not exist. " +
                "Billing will not fall back to the active card, because that would charge this tenant on a price list " +
                "nobody agreed to. Repair the tenant's RateCardId through the audited commercial workflow.");
        }

        var fallback = await _context.Set<RateCard>().AsNoTracking().Include(c => c.Lines)
                           .Where(c => c.IsActive
                                       && c.EffectiveFromUtc <= period.StartUtc
                                       && (c.EffectiveToUtc == null || c.EffectiveToUtc > period.StartUtc))
                           .OrderByDescending(c => c.EffectiveFromUtc).ThenByDescending(c => c.Id)
                           .FirstOrDefaultAsync(ct)
                       ?? throw new BillingConflictException(
                           $"No active rate card is effective for period {period.Key}; activate and assign one.");

        if (tenant.BillingMode == TenantBillingMode.Billable)
            _logger.LogWarning(
                "REVENUE RISK: billable tenant {TenantId} has no pinned rate card, so period {Period} was priced against "
                + "rate card {RateCardId} ('{RateCardCode}') purely because it is the active card. Activating a different "
                + "card reprices this tenant with no record of the change. Pin the tenant's agreed rate card.",
                tenant.Id, period.Key, fallback.Id, fallback.Code);

        return (fallback, RateCardSource.ActiveFallback);
    }

    private Task<RateCard?> LoadRateCardAsync(long id, CancellationToken ct)
        => _context.Set<RateCard>().AsNoTracking().Include(c => c.Lines)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

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
    ///
    /// <para><b>Metering and charging are separated.</b> Every line keeps its real
    /// MeteredQuantity, IncludedQuantity, BillableQuantity and UnitPrice regardless of
    /// billing mode; <paramref name="policy"/> decides only whether Amount follows them
    /// or is zeroed. That is what makes a Trial statement worth computing: at conversion
    /// the account team can read exactly what the tenant consumed AND what the list price
    /// would have been, from the statement itself, without re-running history. A waived
    /// line is therefore a line where UnitPrice × BillableQuantity ≠ Amount — visibly so —
    /// and the exemption marker line states the total that was given away.</para>
    /// </summary>
    internal static List<BillingStatementLine> BuildLines(
        RateCard rateCard, Plan? plan, TenantUsageReadout usage, BillingChargePolicy policy)
    {
        var lines = new List<BillingStatementLine>();

        // The base subscription is MANDATORY for a Billable tenant, including one with no
        // plan at all. It used to be emitted only when a plan existed, so a plan-less
        // tenant was metered and never charged a subscription, and the statement gave no
        // hint that a line was missing — you had to know it should have been there.
        if (policy.Mode == TenantBillingMode.Billable)
            lines.Add(BaseSubscriptionLine(plan, policy));

        var meterByKey = usage.Meters.ToDictionary(m => m.MeterKey, StringComparer.Ordinal);
        var waivedTotal = 0m;
        foreach (var cardLine in rateCard.Lines.OrderBy(l => l.MeterKey, StringComparer.Ordinal))
        {
            meterByKey.TryGetValue(cardLine.MeterKey, out var meter);
            var metered = meter?.Quantity ?? 0m;
            var included = cardLine.IncludedQuantity;
            var billable = Math.Max(0m, metered - included);
            var listAmount = BillingMath.Round2(billable / PricedUnitDivisor(cardLine.MeterKey) * cardLine.UnitPrice);
            if (!policy.Charging)
                waivedTotal += listAmount;

            lines.Add(new BillingStatementLine
            {
                MeterKey = cardLine.MeterKey,
                Description = $"{cardLine.MeterKey} — {cardLine.Unit}"
                              + (string.IsNullOrWhiteSpace(cardLine.TierNote) ? "" : $" ({cardLine.TierNote})"),
                MeteredQuantity = metered,
                IncludedQuantity = included,
                BillableQuantity = billable,
                UnitPrice = cardLine.UnitPrice,
                Amount = policy.Charging ? listAmount : 0m,
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

        if (!policy.Charging)
            lines.Add(SuppressionMarkerLine(policy, usage, BillingMath.Round2(waivedTotal)));

        // A partial month is arithmetic the customer is entitled to see spelled out. The
        // marker states the split once, in one place, so the invoice answers "why is this
        // less than my monthly price" without anyone having to reconstruct it.
        if (policy.IsProrated)
            lines.Add(MarkerLine(
                BillingStatementMarkers.ProrationBillingStart,
                $"Partial period — charged {policy.BillableDays} of {policy.PeriodDays} days",
                $"Billing for this tenant starts {policy.ChargeableFromUtc:yyyy-MM-dd}, inside period {usage.Period}. " +
                $"The base subscription is charged pro rata for {policy.BillableDays} of the period's {policy.PeriodDays} days. " +
                "Metered flow (documents, pages, external AI tokens) is counted from that date only. " +
                "Seats and storage are period-end snapshots and are NOT reduced — each of those lines states so itself."));

        // A fallback-priced Billable tenant is charged correctly TODAY and repriced the
        // day someone activates a different card. The marker puts that on the statement
        // so the exposure is discoverable from billing history, not only from a log line
        // that has since rolled off.
        if (policy.Mode == TenantBillingMode.Billable && policy.RateCardSource == RateCardSource.ActiveFallback)
            lines.Add(MarkerLine(
                BillingStatementMarkers.RiskUnpinnedRateCard,
                "REVENUE RISK — priced against the active rate card, not a pinned one",
                $"Rate card {rateCard.Id} ('{rateCard.Code}') was selected only because it is the active card effective for " +
                $"{usage.Period}. This tenant has no pinned RateCardId, so activating a different card silently changes what " +
                "it pays. Pin the rate card the customer actually agreed to."));

        // Trial expiry rides on the statement as well as the log: an expired trial that
        // keeps producing zero-charge Drafts is the account nobody converted.
        if (policy.TrialExpired)
            lines.Add(MarkerLine(
                BillingStatementMarkers.RiskTrialExpired,
                "REVENUE RISK — trial has ended and the tenant is still uncharged",
                $"Billing mode is Trial and the trial ended {policy.TrialEndsOn:yyyy-MM-dd}, yet {usage.Period} was metered " +
                "and charged nothing. Convert the account to a Billable mode with a plan and a pinned rate card, or record " +
                "an explicit Internal/Partner exemption with a reason. Suspension is a commercial decision and is never automatic."));

        return lines;
    }

    /// <summary>
    /// The base subscription line. Always present for a Billable tenant; carries the
    /// revenue-risk code at the FRONT of its CoverageNote when there is no plan, or a plan
    /// with no price, so a zero base charge can never read as a priced-at-zero plan.
    /// </summary>
    private static BillingStatementLine BaseSubscriptionLine(Plan? plan, BillingChargePolicy policy)
    {
        // P2-B11: direct property access — Plan.MonthlyPriceUsd landed with WS-B.
        var monthlyPrice = plan?.MonthlyPriceUsd;

        // Money is computed from the EXACT day ratio, never from the rounded fraction the
        // line displays: 12/31 is 0.387096..., and pricing 0.387 instead would quietly lose
        // a fraction of a cent on every prorated invoice the platform ever issues.
        var listAmount = BillingMath.Round2((monthlyPrice ?? 0m) * policy.BillableFraction);

        var riskNote = plan is null
            ? BillingStatementMarkers.RiskNoPlan
              + " REVENUE RISK: this tenant's billing mode is Billable but no plan is assigned, so there is no subscription "
              + "price to charge and only metered usage is billed. Assign a priced plan, or record an explicit non-Billable "
              + "billing mode with a reason."
            : monthlyPrice is null
                ? BillingStatementMarkers.RiskPlanNotPriced
                  + $" REVENUE RISK: plan '{plan.Code}' carries no MonthlyPriceUsd, so the base subscription charges 0.00 for a "
                  + "Billable tenant. Price the plan; a null price is not a free plan, it is an unfinished one."
                : null;

        var suppressionNote = policy.Charging
            ? null
            : $" Charging is suppressed for this period ({policy.SuppressionMarker}), so the amount is 0.00 regardless.";

        // The line carries the billable FRACTION as its quantity, and the SourceNote carries
        // the day counts the fraction came from. The fraction is stored at the column's 3dp,
        // so it is a display value and cannot be multiplied back out to the exact amount —
        // which is why the note states the arithmetic in full. A customer query about a
        // partial month is answerable from this one line.
        var prorationNote = policy.IsProrated
            ? $" Prorated: {policy.BillableDays} of {policy.PeriodDays} days billable, from {policy.ChargeableFromUtc:yyyy-MM-dd} " +
              $"(the tenant's billing start date) to the end of the period. " +
              $"{policy.BillableDays}/{policy.PeriodDays} x {monthlyPrice ?? 0m:0.00} USD = {listAmount:0.00} USD."
            : "";

        return new BillingStatementLine
        {
            MeterKey = BillingMeterKeys.BaseSubscription,
            Description = plan is null
                ? "Base subscription — NO PLAN ASSIGNED"
                : $"Base subscription — plan '{plan.Code}'"
                  + (policy.IsProrated ? $" ({policy.BillableDays}/{policy.PeriodDays} days)" : ""),
            MeteredQuantity = policy.BillableFraction,
            IncludedQuantity = 0m,
            BillableQuantity = policy.BillableFraction,
            UnitPrice = monthlyPrice ?? 0m,
            Amount = policy.Charging ? listAmount : 0m,
            SourceNote = (plan is null
                ? "Tenant is Billable but carries no plan, so no monthly price exists to charge."
                : monthlyPrice is null
                    ? $"Plan '{plan.Code}' has no monthly price configured; base charge 0."
                    : $"Plan '{plan.Code}' monthly subscription price.") + prorationNote,
            CoverageNote = riskNote is null && suppressionNote is null ? null : (riskNote ?? "") + (suppressionNote ?? "")
        };
    }

    /// <summary>Names the decision that zeroed every charge on this statement, and what it cost.</summary>
    private static BillingStatementLine SuppressionMarkerLine(
        BillingChargePolicy policy, TenantUsageReadout usage, decimal waivedTotal)
    {
        var marker = policy.SuppressionMarker!;
        var explanation = marker == BillingStatementMarkers.ExemptionPreBillingStart
            ? $"Period {usage.Period} ends on or before this tenant's BillingStartsOn ({policy.BillingStartsOn:yyyy-MM-dd}), so the " +
              "whole period precedes billing: usage is metered and nothing is charged. A period that STRADDLES the billing start " +
              "date is not treated this way — it is charged pro rata by days (billing.proration.billing-start)."
            : policy.Mode == TenantBillingMode.Trial
                ? $"Billing mode is Trial{(policy.TrialEndsOn is null ? " with no end date" : $" until {policy.TrialEndsOn:yyyy-MM-dd}")}. " +
                  "Usage is fully metered so conversion has a real baseline; the charge is a deliberate zero."
                : $"Billing mode is {policy.Mode}: this tenant is never charged through this system. Usage is still metered so " +
                  "the cost of serving it stays visible.";

        return MarkerLine(marker,
            $"Not charged — {policy.Mode} ({waivedTotal:0.00} USD of metered usage waived)",
            $"{explanation} List value of the metered usage waived this period: {waivedTotal:0.00} USD (the base subscription " +
            "is excluded from that figure — only rate-card meters are counted).");
    }

    /// <summary>
    /// A zero-amount statement line whose whole purpose is to be read. Quantities are zero
    /// so it can never perturb a total; the MeterKey is the machine-readable code.
    /// </summary>
    private static BillingStatementLine MarkerLine(string markerKey, string description, string note) => new()
    {
        MeterKey = markerKey,
        Description = Truncate(description, 256),
        MeteredQuantity = 0m,
        IncludedQuantity = 0m,
        BillableQuantity = 0m,
        UnitPrice = 0m,
        Amount = 0m,
        SourceNote = "Statement marker emitted by billing-mode evaluation; carries no charge.",
        CoverageNote = note
    };

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

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
