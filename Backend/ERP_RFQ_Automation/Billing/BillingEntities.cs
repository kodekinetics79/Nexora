using System.Globalization;
using ERP_RFQ_Automation.Extraction;

namespace ERP_RFQ_Automation.Billing;

/// <summary>
/// Well-known meter keys. A <see cref="RateCardLine.MeterKey"/> must use one of
/// these to be priced against real usage; unknown keys price at zero metered
/// quantity (the entitlement still shows on the statement).
/// </summary>
public static class BillingMeterKeys
{
    /// <summary>
    /// Documents processed: billable ExtractionJobs created in the period
    /// (excludes Duplicate / Failed / DeadLetter — see <see cref="BillableDocumentPolicy"/>).
    /// </summary>
    public const string Documents = "documents";

    /// <summary>Settled external AI tokens (input + output) in the period. Unit = 1K tokens.</summary>
    public const string AiTokensExternal = "ai.tokens.external";

    /// <summary>
    /// Pages processed: sum of evidence-ledger page counts (ExtractionRuns joined
    /// to billable ExtractionJobs created in the period). Retry attempts and
    /// dead-letter re-drives write one run per attempt for the SAME document, so
    /// the meter takes MAX(PageCount) per job — never SUM across attempts —
    /// which makes retries double-count safe. Jobs excluded by
    /// <see cref="BillableDocumentPolicy"/> contribute nothing; billable jobs
    /// with no run evidence contribute 0 pages.
    /// NOT BILLING-READY: the pipeline only partially records page counts
    /// (text-layer PDFs/DOCX/e-mail bodies record 0, the unstructured path
    /// records OCR pages as the page count, OCR is capped at 10 pages/document,
    /// spreadsheets count evidence-bearing worksheets, and supplier-quote /
    /// template-uploader doors write no run at all). The reading is a FLOOR;
    /// the readout marks it SignalCoverage=incomplete, BillingReady=false.
    /// </summary>
    public const string PagesProcessed = "pages.processed";

    /// <summary>
    /// OCR pages consumed: MAX(OcrPageCount) per billable job, summed. A strict
    /// subset of <see cref="PagesProcessed"/> work that costs more, metered
    /// separately so rate cards can price OCR pages independently. Zero for runs
    /// whose OcrStatus is NotRequired (guarded at write time by
    /// ExtractionRun.RecordProcessingEvidence). NOT BILLING-READY for the same
    /// reason as <see cref="PagesProcessed"/>, plus the hard 10-page OCR cap:
    /// a 100-page scan records 10 OCR pages and sets OcrTruncated.
    /// </summary>
    public const string PagesOcr = "pages.ocr";

    /// <summary>
    /// Retained document storage: sum of SourceDocuments.ByteSize for evidence
    /// documents received before period end (period-end snapshot; the evidence
    /// ledger is append-only, so every document received on or before the
    /// period end is retained). Metered in BYTES; priced per GiB via
    /// <see cref="BytesPerGigabyte"/> — the same raw-quantity/priced-unit split
    /// as <see cref="AiTokensExternal"/> (metered in tokens, priced per 1K).
    /// </summary>
    public const string StorageGb = "storage.gb";

    /// <summary>Priced-unit divisor for <see cref="AiTokensExternal"/>: price is per 1K tokens.</summary>
    public const decimal TokensPerPricedUnit = 1_000m;

    /// <summary>
    /// Priced-unit divisor for <see cref="StorageGb"/>: price is per GiB
    /// (binary gigabyte, 1024^3 bytes). Documented here so statements and rate
    /// cards agree on the exact conversion.
    /// </summary>
    public const decimal BytesPerGigabyte = 1_073_741_824m;

    /// <summary>
    /// Tenant seats, derived reproducibly from timestamps rather than a
    /// point-in-time flag: a user occupies a seat for a period when
    /// CreatedOn &lt; PeriodEnd AND (IsActive OR DeactivatedAtUtc &gt;= PeriodEnd).
    /// Legacy inactive rows with no DeactivatedAtUtc count as deactivated.
    /// </summary>
    public const string Seats = "seats";

    /// <summary>Synthetic line for the plan's monthly base subscription (not rate-card driven).</summary>
    public const string BaseSubscription = "base.subscription";
}

/// <summary>
/// Zero-amount marker lines that state, ON the statement itself, why a tenant was
/// charged less than its consumption.
///
/// <para><b>Why the MeterKey column.</b> These are not meters, but MeterKey is the
/// statement's only machine-readable discriminator, it is indexed alongside the
/// statement id, and a marker written there renders in every surface that already
/// renders lines — the console, the DTO, an operator's ad-hoc SQL. The alternative
/// (a statement-level boolean) would need a schema change AND a matching change in
/// every renderer, and a flag nobody renders is exactly how a billable tenant ends
/// up charged nothing without anyone noticing. Query the whole risk population with
/// <c>MeterKey LIKE 'billing.revenue-risk.%'</c>.</para>
///
/// <para>Two families, deliberately distinct: <c>billing.revenue-risk.*</c> means
/// money that SHOULD be flowing is not, and someone must act; <c>billing.exemption.*</c>
/// means the platform is knowingly giving service away under a named, audited
/// decision. Conflating them would bury the first family inside the second.</para>
/// </summary>
public static class BillingStatementMarkers
{
    /// <summary>Prefix shared by every revenue-risk marker. A statement carrying one is a finding.</summary>
    public const string RevenueRiskPrefix = "billing.revenue-risk.";

    /// <summary>Prefix shared by every deliberate non-charge marker.</summary>
    public const string ExemptionPrefix = "billing.exemption.";

    /// <summary>
    /// Prefix for markers that explain a PARTIAL charge. Deliberately its own family: money
    /// did move, so it is not an exemption, and nothing is wrong, so it is not a revenue
    /// risk — it is arithmetic a customer is entitled to have explained on the invoice.
    /// </summary>
    public const string ProrationPrefix = "billing.proration.";

    /// <summary>The period straddles <c>BillingStartsOn</c> and is charged pro rata by days.</summary>
    public const string ProrationBillingStart = ProrationPrefix + "billing-start";

    /// <summary>Billable tenant with no <c>PlanId</c>: metered usage is charged, the subscription is not.</summary>
    public const string RiskNoPlan = RevenueRiskPrefix + "no-plan";

    /// <summary>Billable tenant whose plan carries no <c>MonthlyPriceUsd</c>: the base charge is a real zero.</summary>
    public const string RiskPlanNotPriced = RevenueRiskPrefix + "plan-not-priced";

    /// <summary>
    /// Billable tenant priced by the "whichever card is active" fallback rather than by a
    /// pinned <see cref="ERP_RFQ_Automation.Platform.Models.Tenant.RateCardId"/> — the amounts
    /// on this statement change the day someone activates a new card.
    /// </summary>
    public const string RiskUnpinnedRateCard = RevenueRiskPrefix + "unpinned-rate-card";

    /// <summary>Trial tenant past <c>TrialEndsOn</c> and still not charged: the account needs converting.</summary>
    public const string RiskTrialExpired = RevenueRiskPrefix + "trial-expired";

    /// <summary>Usage in a period that starts before <c>BillingStartsOn</c> — metered, not billed.</summary>
    public const string ExemptionPreBillingStart = ExemptionPrefix + "pre-billing-start";

    /// <summary>The exemption marker for a non-<c>Billable</c> billing mode ("billing.exemption.trial", …).</summary>
    public static string ExemptionFor(ERP_RFQ_Automation.Platform.Models.TenantBillingMode mode)
        => ExemptionPrefix + mode.ToString().ToLowerInvariant();

    public static bool IsRevenueRisk(string meterKey)
        => meterKey.StartsWith(RevenueRiskPrefix, StringComparison.Ordinal);

    public static bool IsExemption(string meterKey)
        => meterKey.StartsWith(ExemptionPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The revenue-risk code a line carries, read from EITHER channel.
    ///
    /// <para>Stand-alone markers put the code in <c>MeterKey</c>. The base-subscription
    /// line cannot: it has to keep MeterKey <c>base.subscription</c> or statements stop
    /// being comparable to one another and every consumer that looks up the base charge
    /// by key breaks. So when the base charge is the thing at risk, the code is written
    /// at the FRONT of <c>CoverageNote</c> instead. One reader for both channels, so no
    /// caller has to know which line put it where.</para>
    /// </summary>
    public static string? RiskCodeOf(string meterKey, string? coverageNote)
    {
        if (IsRevenueRisk(meterKey))
            return meterKey;
        if (coverageNote is null || !coverageNote.StartsWith(RevenueRiskPrefix, StringComparison.Ordinal))
            return null;
        var separator = coverageNote.IndexOf(' ');
        return separator < 0 ? coverageNote : coverageNote[..separator];
    }
}

/// <summary>
/// Where the rate card a statement was computed against came from. Recorded because
/// "which price list did we bill this customer on, and did anybody choose it?" is
/// the difference between an invoice a customer will pay and one they will dispute.
/// </summary>
public enum RateCardSource
{
    /// <summary>Passed explicitly to compute — an operator named this card for this run.</summary>
    Explicit,

    /// <summary>The tenant's own pinned <c>RateCardId</c>: the price list they signed.</summary>
    TenantPin,

    /// <summary>
    /// "Whichever active card is effective for the period." Correct for tenants that predate
    /// pinning, a finding for anybody else: activating a new card silently reprices them.
    /// </summary>
    ActiveFallback
}

/// <summary>
/// v1 job-status billing/quota policy (fix P0-B1): only delivered-or-in-flight
/// work is billable. EXCLUDED from both the billing "documents" meter and the
/// docs/month quota:
///   - <see cref="ExtractionStatus.Failed"/> / <see cref="ExtractionStatus.DeadLetter"/>
///     — the platform never delivered the document; charging for our own
///     failures (or counting them against the tenant's quota) is wrong. These
///     are the LIVE exclusions: both statuses are really assigned by the worker.
///   - <see cref="ExtractionStatus.Duplicate"/> — DEFENSIVE ONLY. As audited,
///     no code path ever assigns this status: de-duplication happens at enqueue
///     time (an identical-content re-submission never creates a second job), so
///     there is nothing to exclude in practice. The entry is kept so that a
///     future dedupe design which DOES persist duplicate jobs cannot silently
///     start billing them; it must not be read as evidence that duplicates are
///     currently being filtered out here.
/// Pending/Leased/Extracting/Persisting/Succeeded remain billable so an
/// in-flight month can be metered without waiting for terminal states.
/// The docs/month quota (EntitlementService.CheckDocumentQuotaAsync) must apply
/// this SAME filter — consume <see cref="NonBillableStatuses"/> there.
/// </summary>
public static class BillableDocumentPolicy
{
    /// <summary>Statuses excluded from billing and quota counting. EF-translatable via Contains.</summary>
    public static readonly ExtractionStatus[] NonBillableStatuses =
    {
        ExtractionStatus.Duplicate,
        ExtractionStatus.Failed,
        ExtractionStatus.DeadLetter
    };

    public static bool IsBillable(ExtractionStatus status)
        => Array.IndexOf(NonBillableStatuses, status) < 0;
}

/// <summary>
/// A calendar-month billing period in UTC. Parsed from the wire format
/// <c>YYYY-MM</c>; <see cref="StartUtc"/> is inclusive, <see cref="EndUtc"/>
/// exclusive (first instant of the next month).
/// </summary>
public readonly record struct BillingPeriod(DateTime StartUtc, DateTime EndUtc)
{
    public string Key => StartUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>
    /// The calendar month containing <paramref name="utcMoment"/>. The scheduled billing
    /// run derives its periods from the clock rather than from a wire string, and going
    /// through <c>ToString("yyyy-MM")</c> and back to parse them would make a formatting
    /// change able to break billing.
    /// </summary>
    public static BillingPeriod Containing(DateTime utcMoment)
    {
        var start = DateTime.SpecifyKind(new DateTime(utcMoment.Year, utcMoment.Month, 1), DateTimeKind.Utc);
        return new BillingPeriod(start, start.AddMonths(1));
    }

    public static bool TryParse(string? period, out BillingPeriod value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(period))
            return false;
        if (!DateTime.TryParseExact(period.Trim(), "yyyy-MM", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start))
            return false;
        start = DateTime.SpecifyKind(new DateTime(start.Year, start.Month, 1), DateTimeKind.Utc);
        value = new BillingPeriod(start, start.AddMonths(1));
        return true;
    }
}

/// <summary>
/// Platform-plane price list (schema "platform", NOT tenant-scoped). One card is
/// effective for a period; <see cref="BillingStatement"/> pins the card it was
/// computed against so finalized statements stay reproducible.
/// </summary>
public class RateCard
{
    public long Id { get; set; }

    /// <summary>Stable machine code, e.g. "standard-2026". Unique.</summary>
    public string Code { get; set; } = null!;

    /// <summary>ISO-4217 currency of every line on this card.</summary>
    public string Currency { get; set; } = "USD";

    public DateTime EffectiveFromUtc { get; set; }

    public DateTime? EffectiveToUtc { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

    public string? CreatedBy { get; set; }

    /// <summary>Optimistic-concurrency token (manually incremented on update).</summary>
    public long Version { get; set; } = 1;

    public ICollection<RateCardLine> Lines { get; set; } = new List<RateCardLine>();
}

/// <summary>Per-meter price + included allowance on a <see cref="RateCard"/>.</summary>
public class RateCardLine
{
    public long Id { get; set; }

    public long RateCardId { get; set; }

    /// <summary>
    /// One of <see cref="BillingMeterKeys"/> ("documents", "pages.processed",
    /// "pages.ocr", "ai.tokens.external", "seats", "storage.gb"). Meters are
    /// ADDITIVE: a rate card that carries no line for a meter simply does not
    /// charge that meter (usage still shows on the readout).
    /// </summary>
    public string MeterKey { get; set; } = null!;

    /// <summary>Allowance included in the base subscription, in metered units (tokens for AI).</summary>
    public decimal IncludedQuantity { get; set; }

    /// <summary>Price per <see cref="Unit"/> ("document", "1K tokens", "seat").</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>Human unit label: "document", "1K tokens", "seat".</summary>
    public string Unit { get; set; } = null!;

    public string? TierNote { get; set; }

    public RateCard RateCard { get; set; } = null!;
}

public enum BillingStatementStatus
{
    Draft,
    Final
}

public enum BillingReadinessStatus { Blocked, Ready }

/// <summary>
/// A tenant's charge summary for one billing period. UNIQUE (TenantId,
/// PeriodStartUtc) is the duplicate-charge guard; Draft statements are
/// recomputed in place, Final statements are immutable (service-enforced).
/// </summary>
public class BillingStatement
{
    public long Id { get; set; }

    /// <summary>FK to platform.Tenants.</summary>
    public long TenantId { get; set; }

    public DateTime PeriodStartUtc { get; set; }

    public DateTime PeriodEndUtc { get; set; }

    /// <summary>The rate card the amounts were computed against (pinned).</summary>
    public long RateCardId { get; set; }

    public string Currency { get; set; } = "USD";

    public BillingStatementStatus Status { get; set; } = BillingStatementStatus.Draft;

    public decimal TotalAmount { get; set; }

    /// <summary>Frozen source-coverage and rating verdict from the last Draft computation.</summary>
    public BillingReadinessStatus ReadinessStatus { get; set; } = BillingReadinessStatus.Blocked;
    public string ReadinessManifestJson { get; set; } = "{}";
    public string ReadinessManifestSha256 { get; set; } = new string('0', 64);

    public DateTime ComputedAtUtc { get; set; }

    /// <summary>The operator or governed worker that last calculated this Draft.</summary>
    public string ComputedBy { get; set; } = "system:billing-run";

    public DateTime? FinalizedAtUtc { get; set; }

    public string? FinalizedBy { get; set; }

    /// <summary>Optimistic-concurrency token (manually incremented on recompute/finalize).</summary>
    public long Version { get; set; } = 1;

    public ICollection<BillingStatementLine> Lines { get; set; } = new List<BillingStatementLine>();
}

/// <summary>One charge line on a <see cref="BillingStatement"/>.</summary>
public class BillingStatementLine
{
    public long Id { get; set; }

    public long BillingStatementId { get; set; }

    public string MeterKey { get; set; } = null!;

    public string Description { get; set; } = null!;

    /// <summary>Raw metered quantity from the source ledger (tokens for AI, not 1K units).</summary>
    public decimal MeteredQuantity { get; set; }

    public decimal IncludedQuantity { get; set; }

    /// <summary>max(0, metered - included), in metered units.</summary>
    public decimal BillableQuantity { get; set; }

    /// <summary>Price per rate-card unit (per 1K tokens for "ai.tokens.external").</summary>
    public decimal UnitPrice { get; set; }

    public decimal Amount { get; set; }

    /// <summary>
    /// Provenance ONLY, e.g. "ExtractionJobs count 2026-08 (BU 12)" — the source
    /// ledger, period and business unit the quantity came from. Mapped as
    /// unbounded <c>text</c>: provenance notes name every contributing ledger and
    /// routinely run past any fixed width, and truncating a statement's audit
    /// trail (or hard-failing the compute on PostgreSQL's 22001) is never the
    /// right trade.
    /// </summary>
    public string? SourceNote { get; set; }

    /// <summary>
    /// The meter's signal-coverage caveat (<see cref="ERP_RFQ_Automation.Billing.MeterReading.CoverageNote"/>),
    /// kept as its OWN column rather than concatenated onto
    /// <see cref="SourceNote"/>: the caveat is structured data — machine-readable,
    /// independently renderable, and independently queryable — not prose glued
    /// onto provenance. Null when the meter's signal is complete and unqualified.
    /// Also mapped unbounded <c>text</c>.
    /// </summary>
    public string? CoverageNote { get; set; }

    public BillingStatement Statement { get; set; } = null!;
}
