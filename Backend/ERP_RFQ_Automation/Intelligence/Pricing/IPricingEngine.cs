namespace ERP_RFQ_Automation.Intelligence.Pricing;

/// <summary>
/// Multi-signal pricing intelligence for RFQ lines. Blends price-list, accepted-quote,
/// supplier-quote, purchase-history and product-master signals into a per-line
/// recommended price with rationale + confidence, and can persist chosen prices onto
/// <c>Rfqitem.UnitPrice</c> — the exact field the existing RFQ→Quote approve flow
/// (<c>RfqRepository.ApproveAsync</c>) copies into <c>QuoteItem.UnitPrice</c>.
/// </summary>
public interface IPricingEngine
{
    /// <summary>Compute a tenant-scoped price recommendation for every line of the RFQ.</summary>
    /// <exception cref="KeyNotFoundException">RFQ does not exist in this business unit.</exception>
    Task<PricePreview> PriceRfqAsync(long rfqId, long businessUnitId, CancellationToken ct);

    /// <summary>Persist chosen unit prices onto the RFQ lines (tenant-checked).</summary>
    /// <exception cref="KeyNotFoundException">RFQ does not exist in this business unit.</exception>
    /// <exception cref="ArgumentException">Invalid line reference or non-positive price.</exception>
    Task<ApplyPricingResult> ApplyPricingAsync(long rfqId, long businessUnitId, ApplyPricingRequest req, CancellationToken ct);
}
