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
    public long LeadId { get; set; }
    public string? Rfqno { get; set; }
    public string? BuyersName { get; set; }

    /// <summary>Lead-level extraction confidence (Lead.Aiconfidence), when stamped.</summary>
    public decimal? ExtractionConfidence { get; set; }

    public CatalogCoverage Coverage { get; set; } = new();

    /// <summary>Sum of best-available line prices × quantities (0 when nothing is priceable).</summary>
    public decimal EstimatedValue { get; set; }

    /// <summary>"high" when most lines were priced from real numbers, else "low".</summary>
    public string ValueConfidence { get; set; } = "low";

    /// <summary>Single currency across the lead's lines; null when mixed or unknown.</summary>
    public string? Currency { get; set; }

    /// <summary>
    /// Average (price − cost) / price across matched+costed lines, as a percentage
    /// (0–100). Null when no line has both a usable price and a known cost.
    /// </summary>
    public decimal? MarginPotentialPct { get; set; }

    public CustomerHistory Customer { get; set; } = new();

    public DeadlineFeasibility Deadline { get; set; } = new();

    /// <summary>"bid" | "review" | "skip".</summary>
    public string Recommendation { get; set; } = LeadDecisionRecommendations.Review;

    /// <summary>Plain-language reasons a sales exec reads at a glance.</summary>
    public List<string> Reasons { get; set; } = new();
}

/// <summary>How much of the lead we can actually supply from the catalog.</summary>
public sealed class CatalogCoverage
{
    public int TotalItems { get; set; }
    public int CoveredItems { get; set; }

    /// <summary>coveredItems / totalItems as a percentage 0–100 (0 for empty leads).</summary>
    public decimal CoveragePct { get; set; }

    /// <summary>Matched items whose catalog product has QtyOnHand &gt; 0.</summary>
    public int InStockItems { get; set; }

    public List<CoverageItem> Items { get; set; } = new();
}

/// <summary>Per-line matching + pricing detail behind the coverage numbers.</summary>
public sealed class CoverageItem
{
    public long LeadItemId { get; set; }
    public string? Description { get; set; }
    public int Quantity { get; set; }
    public bool Matched { get; set; }

    /// <summary>"code" | "mpn" | "name" — how the catalog match was made (null when unmatched).</summary>
    public string? MatchType { get; set; }

    public long? ProductId { get; set; }

    /// <summary>Matched product has QtyOnHand &gt; 0.</summary>
    public bool InStock { get; set; }

    /// <summary>The unit price used for the value estimate (null when unpriceable).</summary>
    public decimal? UnitPrice { get; set; }

    /// <summary>"lead" (the RFQ's own price) | "catalog" (product master) | null.</summary>
    public string? PriceSource { get; set; }
}

/// <summary>Do we already know this buyer, and what are they worth to us?</summary>
public sealed class CustomerHistory
{
    public bool IsExistingCustomer { get; set; }
    public string? CustomerName { get; set; }

    /// <summary>Past leads from the same buyer name in this business unit (excluding this one).</summary>
    public int PastLeads { get; set; }

    /// <summary>Quotes issued to the resolved customer.</summary>
    public int Quotes { get; set; }

    /// <summary>Orders from the resolved customer in the last 24 months.</summary>
    public int Orders { get; set; }

    /// <summary>Summed Order.TotalAmount over the last 24 months.</summary>
    public decimal TotalOrderValue { get; set; }
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
    public long LeadId { get; set; }
    public decimal CoveragePct { get; set; }
    public decimal EstimatedValue { get; set; }
    public int? DaysLeft { get; set; }
    public string Urgency { get; set; } = LeadDecisionUrgency.Unknown;
    public string Recommendation { get; set; } = LeadDecisionRecommendations.Review;
}

/// <summary>Stable recommendation values (wire contract).</summary>
public static class LeadDecisionRecommendations
{
    public const string Bid = "bid";
    public const string Review = "review";
    public const string Skip = "skip";
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
