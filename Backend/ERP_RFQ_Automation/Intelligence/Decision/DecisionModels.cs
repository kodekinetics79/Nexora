namespace ERP_RFQ_Automation.Intelligence.Decision;

// Wire-contract DTOs for the Lead Decision Brief. Serialized camelCase by the
// app-wide System.Text.Json defaults (same as every other controller).

/// <summary>
/// Everything a sales executive needs to decide Bid / Review / Skip on a lead,
/// in one payload: catalog coverage, estimated value, margin potential,
/// customer history, deadline feasibility and a transparent recommendation.
/// </summary>
public sealed class LeadDecisionBrief
{
    public string PolicyVersion { get; set; } = LeadDecisionPolicy.Version;
    public string Completeness { get; set; } = LeadDecisionCompleteness.Complete;
    public bool IsActionable { get; set; } = true;
    public long LeadId { get; set; }
    public string? Rfqno { get; set; }
    public string? BuyersName { get; set; }

    /// <summary>Lead-level extraction confidence (Lead.Aiconfidence), when stamped.</summary>
    public decimal? ExtractionConfidence { get; set; }

    public CatalogCoverage Coverage { get; set; } = new();

    /// <summary>Sum of priced lines when all priced lines share one known currency; otherwise null.</summary>
    public decimal? EstimatedValue { get; set; }

    /// <summary>"high" when most lines were priced from real numbers, else "low".</summary>
    public string ValueConfidence { get; set; } = "low";

    /// <summary>Single currency across the lead's lines; null when mixed or unknown.</summary>
    public string? Currency { get; set; }

    /// <summary>
    /// Value-weighted (revenue - cost) / revenue, as a percentage (0-100), over the priced lines of
    /// this lead's commercial case. Produced by <c>IGrossMarginService.GetForCommercialCaseAsync</c>
    /// from <c>CustomerQuoteSourcingDecision</c> — the same records and the same formula as the
    /// board-facing gross margin, so the two can never disagree. Null, with the reason stated in
    /// <see cref="Reasons"/>, until a line has been priced from an approved supplier award.
    /// </summary>
    public decimal? MarginPotentialPct { get; set; }

    /// <summary>
    /// Demand lines carrying a currency-qualified landed cost and the customer price built from it.
    /// This counts <b>quote</b> lines, not lead lines: it is how many lines could be costed at all.
    /// </summary>
    public int MarginCostedItems { get; set; }

    /// <summary>
    /// True only when the costed lines cover every Lead line. Consumers that gate on a whole-lead
    /// margin read this; a partial figure still appears in <see cref="MarginPotentialPct"/> and is
    /// described in <see cref="Reasons"/> as covering only the items we can cost.
    /// </summary>
    public bool IsMarginComplete { get; set; }

    public CustomerHistory Customer { get; set; } = new();

    public DeadlineFeasibility Deadline { get; set; } = new();

    /// <summary>"bid" | "review" | "skip".</summary>
    public string Recommendation { get; set; } = LeadDecisionRecommendations.Review;

    /// <summary>Plain-language reasons a sales exec reads at a glance.</summary>
    public List<string> Reasons { get; set; } = new();
}

/// <summary>How much of the lead has an exact, decision-grade catalog identity.</summary>
public sealed class CatalogCoverage
{
    public int TotalItems { get; set; }
    public int CoveredItems { get; set; }

    /// <summary>coveredItems / totalItems as a percentage 0–100 (0 for empty leads).</summary>
    public decimal CoveragePct { get; set; }

    /// <summary>
    /// Exact matches with positive available-to-promise. Same number as
    /// <see cref="CatalogOnHandItems"/>; kept as a compatibility alias for existing consumers.
    /// </summary>
    public int InStockItems { get; set; }

    /// <summary>Exact matches with positive available-to-promise across the tenant's warehouses.</summary>
    public int CatalogOnHandItems { get; set; }

    /// <summary>
    /// Total available-to-promise across exact matches. Sourced from the Inventory rows through
    /// <c>InventoryQuantityMath.AvailableToPromise</c> — it used to be the raw product-master
    /// QtyOnHand column, a number the availability engine could not see.
    /// </summary>
    public decimal CatalogOnHandQuantity { get; set; }

    public List<CoverageItem> Items { get; set; } = new();
}

/// <summary>Per-line matching + pricing detail behind the coverage numbers.</summary>
public sealed class CoverageItem
{
    public long LeadItemId { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// Null when the source document stated no readable quantity. Rendered as an explicit gap,
    /// never as 0 — a coverage line reading "0" tells a rep the buyer wants nothing.
    /// </summary>
    public decimal? Quantity { get; set; }
    public bool Matched { get; set; }

    /// <summary>"code" | "mpn" - how the exact catalog match was made (null when unmatched).</summary>
    public string? MatchType { get; set; }

    public long? ProductId { get; set; }

    /// <summary>Compatibility alias for <see cref="HasCatalogOnHand"/>.</summary>
    public bool InStock { get; set; }

    /// <summary>Whether the exact match has positive available-to-promise.</summary>
    public bool HasCatalogOnHand { get; set; }

    /// <summary>
    /// Available-to-promise for the exact match, summed across the tenant's warehouses: on-hand
    /// net of reserved, allocated, quarantined, damaged, expired and safety stock. Null when the
    /// line has no exact catalog match.
    /// </summary>
    public decimal? CatalogQtyOnHand { get; set; }

    /// <summary>The unit price used for the value estimate (null when unpriceable).</summary>
    public decimal? UnitPrice { get; set; }

    /// <summary>"lead" (the RFQ's own price) | "catalog" (product master) | null.</summary>
    public string? PriceSource { get; set; }
}

/// <summary>Do we already know this buyer, and what are they worth to us?</summary>
public sealed class CustomerHistory
{
    public long? CustomerId { get; set; }
    public bool IsExistingCustomer { get; set; }
    public string? CustomerName { get; set; }

    /// <summary>"canonical" | "heuristic-email" | "heuristic-name" | "heuristic-ambiguous" | "unresolved".</summary>
    public string IdentityEvidence { get; set; } = CustomerIdentityEvidence.Unresolved;

    /// <summary>Only a tenant-qualified Lead.CustomerId is decision-grade.</summary>
    public bool IsDecisionGradeIdentity { get; set; }

    /// <summary>Past leads from the same buyer name in this business unit (excluding this one).</summary>
    public int PastLeads { get; set; }

    /// <summary>Quotes issued to the resolved customer.</summary>
    public int Quotes { get; set; }

    /// <summary>Orders from the resolved customer in the last 24 months.</summary>
    public int Orders { get; set; }

    /// <summary>Summed Order.TotalAmount only when every sampled order has one currency.</summary>
    public decimal? TotalOrderValue { get; set; }

    public string? TotalOrderCurrency { get; set; }

    /// <summary>Latest sent-Quote or linked-Order evidence used by the customer cohort.</summary>
    public DateTime? EvidenceAsOfUtc { get; set; }
}

/// <summary>Can we realistically respond before the bid closes?</summary>
public sealed class DeadlineFeasibility
{
    /// <summary>Null when absent or a sentinel (&lt; year 2000) value.</summary>
    public DateTime? BidClosingDate { get; set; }

    /// <summary>Whole days until closing (negative = past due); null when no usable date.</summary>
    public int? DaysLeft { get; set; }

    /// <summary>"overdue" | "critical" (≤3d) | "soon" (≤7d) | "comfortable" | "unknown".</summary>
    public string Urgency { get; set; } = LeadDecisionUrgency.Unknown;

    /// <summary>Plain-language workload hint (items vs days).</summary>
    public string? WorkloadHint { get; set; }
}

/// <summary>
/// Cheap per-lead card for list views. Exact-code coverage only, lead-item prices
/// only, and a coarse recommendation (no customer/margin signals are consulted).
/// </summary>
public sealed class LeadDecisionSummary
{
    public string PolicyVersion { get; set; } = LeadDecisionPolicy.Version;
    public string Completeness { get; set; } = LeadDecisionCompleteness.Partial;
    public bool IsActionable { get; set; }
    public long LeadId { get; set; }
    public decimal CoveragePct { get; set; }
    public decimal? EstimatedValue { get; set; }
    public int? DaysLeft { get; set; }
    public string Urgency { get; set; } = LeadDecisionUrgency.Unknown;
    public string Recommendation { get; set; } = LeadDecisionRecommendations.Review;
}

public static class LeadDecisionPolicy
{
    public const string Version = "lead-decision/v1.1";
}

public static class LeadDecisionCompleteness
{
    public const string Complete = "complete";
    public const string Partial = "partial";
}

public static class CustomerIdentityEvidence
{
    public const string Canonical = "canonical";
    public const string HeuristicEmail = "heuristic-email";
    public const string HeuristicName = "heuristic-name";
    public const string HeuristicAmbiguous = "heuristic-ambiguous";
    public const string CanonicalInvalid = "canonical-invalid";
    public const string Unresolved = "unresolved";
}

/// <summary>Stable recommendation values (wire contract).</summary>
public static class LeadDecisionRecommendations
{
    public const string Bid = "bid";
    public const string Review = "review";
    public const string Skip = "skip";

    /// <summary>
    /// The inputs the rules need do not exist for this lead, so no recommendation is
    /// made. Emitted instead of <see cref="Skip"/> when catalog coverage cannot be
    /// assessed — see LeadDecisionService.Recommend. "Likely skip" on every inbound
    /// enquiry is not advice, it is a stuck needle, and a rep who learns to ignore the
    /// column has lost the column for the cases where it would have been right.
    /// </summary>
    public const string CannotAssess = "cannot-assess";
}

/// <summary>Stable urgency band values (wire contract).</summary>
public static class LeadDecisionUrgency
{
    public const string Overdue = "overdue";
    public const string Critical = "critical";
    public const string Soon = "soon";
    public const string Comfortable = "comfortable";
    public const string Unknown = "unknown";
}
