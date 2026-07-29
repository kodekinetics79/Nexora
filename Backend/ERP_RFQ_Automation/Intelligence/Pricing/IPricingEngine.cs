namespace ERP_RFQ_Automation.Intelligence.Pricing;

/// <summary>
/// Tenant-scoped shadow pricing intelligence for RFQ lines. It uses explicit-currency
/// accepted Customer Quote evidence to produce advisory prices with rationale and
/// confidence. It never mutates authoritative RFQ or Customer Quote prices.
/// </summary>
public interface IPricingEngine
{
    /// <summary>Compute a tenant-scoped price recommendation for every line of the RFQ.</summary>
    /// <exception cref="KeyNotFoundException">RFQ does not exist in this business unit.</exception>
    Task<PricePreview> PriceRfqAsync(long rfqId, long businessUnitId, CancellationToken ct);

    /// <summary>
    /// Closed compatibility surface. Direct pricing mutation is prohibited and this
    /// method always fails; confirmed pricing must use the governed Customer Quote flow.
    /// </summary>
    /// <exception cref="InvalidOperationException">Always thrown because pricing is shadow-only.</exception>
    Task<ApplyPricingResult> ApplyPricingAsync(long rfqId, long businessUnitId, ApplyPricingRequest req, CancellationToken ct);
}
