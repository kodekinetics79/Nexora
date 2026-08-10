using System.Text.Json.Serialization;

namespace ERP_RFQ_Automation.Intelligence.Pricing;

// Wire contract for the Pricing Intelligence Engine. Serialized by ASP.NET Core's
// default web JSON options (camelCase), so the PascalCase properties below emit the
// exact contract the frontend builds against:
//   PricePreview = { rfqId, currency, lines:[...], totals:{ recommendedTotal }, overallConfidence }

/// <summary>Shadow-only pricing preview for one RFQ.</summary>
public sealed class PricePreview
{
    public long RfqId { get; set; }

    /// <summary>
    /// ISO-ish currency code the preview is priced in (taken from the RFQ lines,
    /// falling back to the tenant's base currency). No FX conversion is performed —
    /// signals in a different explicit currency are excluded from the blend.
    /// </summary>
    public string? Currency { get; set; }

    public string Mode { get; set; } = "SHADOW";

    public bool ApplyAllowed { get; set; }

    public string ApplyBlocker { get; set; } =
        "Create or revise the Customer Quote through the governed Supplier award pricing bridge.";

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

    /// <summary>Resolved line currency. Null means the line is not safe to recommend.</summary>
    public string? Currency { get; set; }

    /// <summary>Advisory sell price from admitted accepted-Quote evidence.</summary>
    public decimal RecommendedUnitPrice { get; set; }

    /// <summary>
    /// The authoritative cost floor for this line: the awarded supplier's LANDED unit cost, taken
    /// from <c>CustomerQuoteSourcingDecision.SupplierLandedUnitCost</c> — the very number the
    /// customer price was derived from (<c>landed / (1 - margin)</c>), so it is the honest floor
    /// rather than an inferred one. Denominated in <see cref="FloorCurrency"/>, NOT necessarily in
    /// <see cref="Currency"/>.
    ///
    /// <para>NULL means NO FLOOR IS ESTABLISHED: no governed sourcing decision has been recorded
    /// against this RFQ line, so nobody has decided what it costs. It must be rendered as a visible
    /// gap and must NEVER be defaulted to 0 — a floor of 0 asserts "any price is acceptable", which
    /// is a decision no one took (wiring contract failure #10) and which would make the below-floor
    /// send gate pass every price ever typed.</para>
    /// </summary>
    public decimal? FloorUnitPrice { get; set; }

    /// <summary>
    /// The currency code <see cref="FloorUnitPrice"/> is denominated in — the awarded supplier
    /// offer's currency, which can legitimately differ from <see cref="Currency"/> (the RFQ line's
    /// own currency). Every comparison against the floor MUST read this and convert, or refuse.
    ///
    /// <para>Null WITH a non-null floor means the floor's currency could not be named. That is not
    /// permission to assume the line currency: <see cref="BelowFloorGuard"/> treats it as an
    /// unresolvable comparison and holds the action (fail closed).</para>
    /// </summary>
    public string? FloorCurrency { get; set; }

    /// <summary>
    /// Plain-language provenance of the floor, for the rep who is being told not to go below it.
    /// Null exactly when <see cref="FloorUnitPrice"/> is null.
    /// </summary>
    public string? FloorBasis { get; set; }

    /// <summary>
    /// Gross margin of <see cref="RecommendedUnitPrice"/> over <see cref="FloorUnitPrice"/> as a
    /// PERCENT — 20m means 20%, not 0.2. Null when there is no floor, no recommendation, or when
    /// the two are in different currencies (this advisory does not convert; the send gate does).
    /// </summary>
    public decimal? MarginPct { get; set; }

    /// <summary>0-1: strength and recency of admitted evidence.</summary>
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
    /// <summary>Stable source code. The current policy admits only "recentQuote".</summary>
    public string Source { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary>The sell-side unit price this signal contributed to the blend.</summary>
    public decimal Value { get; set; }

    /// <summary>Human-readable quote reference, date, quantity and currency.</summary>
    public string Detail { get; set; } = string.Empty;
}

public sealed class PriceTotals
{
    /// <summary>Present only when every priced line uses one verified currency.</summary>
    public decimal? RecommendedTotal { get; set; }

    public List<CurrencyPriceTotal> ByCurrency { get; set; } = new();
    public int PricedLineCount { get; set; }
    public int UnpricedLineCount { get; set; }
}

public sealed record CurrencyPriceTotal(string Currency, decimal RecommendedTotal, int LineCount);

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
