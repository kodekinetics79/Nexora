using System.Text.Json.Serialization;

namespace ERP_RFQ_Automation.Intelligence.Conversion;

// ============================================================================
// Wire contract for the Lead -> RFQ conversion intelligence.
// Serialized with the app-wide System.Text.Json web defaults (camelCase), so
// C# property names below map 1:1 onto the frontend contract:
//   ConversionPreview = { leadId, header:{ rfqno, buyersName, recDate,
//     bidClosingDate }, items:[...], overallConfidence }
// ============================================================================

/// <summary>Full dry-run of a lead conversion: per-line product resolution + confidence.</summary>
public sealed class ConversionPreview
{
    public long LeadId { get; init; }
    public ConversionPreviewHeader Header { get; init; } = new();
    public IReadOnlyList<ConversionPreviewItem> Items { get; init; } = Array.Empty<ConversionPreviewItem>();
    /// <summary>Average of per-line confidences (0 when the lead has no lines).</summary>
    public decimal OverallConfidence { get; init; }
}

/// <summary>RFQ header the conversion would create (mirrors the legacy field copy).</summary>
public sealed class ConversionPreviewHeader
{
    public string? Rfqno { get; init; }
    public string? BuyersName { get; init; }
    public DateTime RecDate { get; init; }
    public DateTime? BidClosingDate { get; init; }
}

/// <summary>One catalog candidate for a lead line, with score + human-readable reason.</summary>
public sealed class ProductMatch
{
    public long ProductId { get; init; }
    public string? ProductName { get; init; }
    /// <summary>Catalog part number (Product.PartNo) — the material-code analog.</summary>
    public string? MaterialCode { get; init; }
    /// <summary>Catalog model number (Product.ModelNo) — the closest MPN analog.</summary>
    public string? ManufacturerPartNumber { get; init; }
    public decimal Score { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class ConversionPreviewItem
{
    public long LeadItemId { get; init; }
    /// <summary>The raw text the line was extracted from (short name / description).</summary>
    public string? SourceText { get; init; }
    /// <summary>Raw quantity as stored on the lead line.</summary>
    public int Quantity { get; init; }
    /// <summary>Raw unit-of-measure string as stored on the lead line.</summary>
    public string? UnitOfMeasure { get; init; }
    public decimal? NormalizedQuantity { get; init; }
    public string? NormalizedUom { get; init; }
    /// <summary>Top catalog matches (max 3), best first. Empty when nothing matched.</summary>
    public IReadOnlyList<ProductMatch> Matches { get; init; } = Array.Empty<ProductMatch>();
    public long? BestMatchProductId { get; init; }
    /// <summary>Top match score; 0 when there are no matches.</summary>
    public decimal Confidence { get; init; }
    public bool NeedsAttention { get; init; }
    public string? AttentionReason { get; init; }
}

/// <summary>
/// Caller choices for the actual conversion. Lead lines NOT referenced in
/// <see cref="Items"/> are included with defaults (raw qty/uom; product
/// auto-assigned only when resolution confidence &gt;= 0.70).
/// </summary>
public sealed class ConvertRequest
{
    public List<ConvertRequestItem>? Items { get; set; } = new();
    public string? Notes { get; set; }

    /// <summary>
    /// One reason covering every line acknowledged in <see cref="ConvertRequestItem.AcknowledgeWarning"/>
    /// that does not carry its own. Operators acknowledge a batch of lines with a single
    /// explanation ("supplier confirmed pack sizes by phone"), so a per-line reason is optional
    /// and this is the fallback — but between them a reason must always exist.
    /// </summary>
    public string? WarningAcknowledgementReason { get; set; }

    /// <summary>
    /// Acknowledges every soft-flagged line in one action, without ticking each one.
    ///
    /// <para>A real SEC bid list runs to 84 lines and it is normal for most of them to raise
    /// "No catalog match found" on a brand the tenant has not loaded yet. Demanding 60 individual
    /// ticks would train operators to click through the gate without reading it, which is worse
    /// than no gate. The audit is unaffected: every waived line is still recorded by name with
    /// its specific reason, and <b>hard integrity failures are still refused</b> — this waives
    /// soft warnings only.</para>
    /// </summary>
    public bool AcknowledgeAllWarnings { get; set; }

    /// <summary>
    /// Acting user for CreatedBy stamping. Set server-side from the JWT /
    /// agent context; never bound from the request body.
    /// </summary>
    [JsonIgnore]
    public string? ActingUser { get; set; }
}

public sealed class ConvertRequestItem
{
    public long LeadItemId { get; set; }
    public bool Include { get; set; } = true;
    /// <summary>Explicit catalog product for this line; overrides auto-resolution.</summary>
    public long? ProductId { get; set; }
    /// <summary>Corrected quantity; falls back to the lead line's raw quantity.</summary>
    public int? Quantity { get; set; }
    /// <summary>Corrected unit of measure; falls back to the lead line's raw value.</summary>
    public string? UnitOfMeasure { get; set; }

    /// <summary>
    /// The operator has seen this line's extraction warning and is converting anyway.
    ///
    /// <para>Required for any line the resolver flagged <c>NeedsAttention</c> whose reasons are
    /// all soft (no catalog match, low-confidence match, UoM needs review). It can NEVER waive a
    /// hard integrity failure — a missing quantity or a missing unit is refused whatever this
    /// says, because acknowledging "I don't know how many" produces an RFQ that cannot be
    /// quoted.</para>
    /// </summary>
    public bool AcknowledgeWarning { get; set; }

    /// <summary>Per-line explanation. Falls back to
    /// <see cref="ConvertRequest.WarningAcknowledgementReason"/> when omitted.</summary>
    public string? AcknowledgementReason { get; set; }
}
