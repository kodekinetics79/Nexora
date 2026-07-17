using System.Text.Json.Serialization;

namespace ERP_RFQ_Automation.Intelligence.Pricing;

// Wire contract for the Pricing Intelligence Engine. Serialized by ASP.NET Core's
// default web JSON options (camelCase), so the PascalCase properties below emit the
// exact contract the frontend builds against:
//   PricePreview = { rfqId, currency, lines:[...], totals:{ recommendedTotal }, overallConfidence }

/// <summary>Full multi-signal pricing preview for one RFQ.</summary>
public sealed class PricePreview
{
    public long RfqId { get; set; }

    /// <summary>
    /// ISO-ish currency code the preview is priced in (taken from the RFQ lines,
    /// falling back to the tenant's base currency). No FX conversion is performed —
    /// signals in a different explicit currency are excluded from the blend.
    /// </summary>
    public string Currency { get; set; } = string.Empty;

    public List<PriceLine> Lines { get; set; } = new();

    public PriceTotals Totals { get; set; } = new();

    /// <summary>Simple average of the per-line confidences (0–1).</summary>
    public decimal OverallConfidence { get; set; }
}

/// <summary>Per-RFQ-line recommendation with rationale and contributing signals.</summary>
public sealed class PriceLine
{
    public long RfqItemId { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }

    /// <summary>Priority-weighted blend of every signal that fired (sell price).</summary>
    public decimal RecommendedUnitPrice { get; set; }

    /// <summary>Cost basis with NO margin — the do-not-sell-below line.</summary>
    public decimal FloorUnitPrice { get; set; }

    /// <summary>(recommended − floor) / floor. 0 when no floor could be established.</summary>
    public decimal MarginPct { get; set; }

    /// <summary>0–1: strength/recency/plenty of the signals behind the recommendation.</summary>
    public decimal Confidence { get; set; }

    /// <summary>One plain-English sentence explaining the dominant signal.</summary>
    public string Rationale { get; set; } = string.Empty;

    public List<PriceSignal> Signals { get; set; } = new();

    /// <summary>true when confidence &lt; 0.5 or no signal fired — a human should look.</summary>
    public bool NeedsAttention { get; set; }
}

/// <summary>One contributing signal. Source is a stable enum-like string.</summary>
public sealed class PriceSignal
{
    /// <summary>"priceList" | "recentQuote" | "supplierQuote" | "purchaseHistory" | "productMaster"</summary>
    public string Source { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary>The sell-side unit price this signal contributed to the blend.</summary>
    public decimal Value { get; set; }

    /// <summary>Human detail: raw cost, date, supplier, quantity, currency.</summary>
    public string Detail { get; set; } = string.Empty;
}

public sealed class PriceTotals
{
    public decimal RecommendedTotal { get; set; }
}

/// <summary>POST body for apply-pricing: { lines:[{ rfqItemId, unitPrice }] }.</summary>
public sealed class ApplyPricingRequest
{
    public List<ApplyPricingLine> Lines { get; set; } = new();

    /// <summary>
    /// Set server-side (controller/agent tool) from the authenticated identity —
    /// never bound from the request body.
    /// </summary>
    [JsonIgnore]
    public string? AppliedBy { get; set; }
}

public sealed class ApplyPricingLine
{
    public long RfqItemId { get; set; }
    public decimal UnitPrice { get; set; }
}

/// <summary>Result of apply-pricing: { applied, total }.</summary>
public sealed class ApplyPricingResult
{
    /// <summary>Number of RFQ lines whose unit price was updated.</summary>
    public int Applied { get; set; }

    /// <summary>Σ quantity × unitPrice over ALL lines of the RFQ after applying.</summary>
    public decimal Total { get; set; }
}
