namespace ERP_RFQ_Automation.Intelligence.Conversion;

/// <summary>
/// Lead -&gt; RFQ conversion intelligence: deterministic catalog product resolution,
/// quantity/UoM normalization and per-line confidence, plus the enriched convert
/// itself. All reads/writes flow through the tenant-filtered context AND explicit
/// business-unit predicates (mirroring the legacy convert path).
/// </summary>
public interface ILeadConversionIntelligence
{
    /// <summary>
    /// Dry-run: resolve every lead line against the tenant-visible product catalog
    /// and report matches, normalized values and confidence. Never writes.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Lead not found in the business unit.</exception>
    Task<ConversionPreview> PreviewAsync(long leadId, long businessUnitId, CancellationToken ct);

    /// <summary>
    /// The same dry-run for a lead the caller has ALREADY read with its items (untracked). The
    /// decision workbench reads the lead and its lines anyway; reading 3,000 lead items a second
    /// time for a 1,500-line bid list only doubled the heaviest query of the screen.
    /// </summary>
    Task<ConversionPreview> PreviewAsync(Models.Lead loadedLead, long businessUnitId, CancellationToken ct)
        => PreviewAsync(loadedLead.Id, businessUnitId, ct);

    /// <summary>
    /// Convert the lead into an RFQ, mirroring the legacy field mapping and applying
    /// the request's per-line choices (include/exclude, product, corrected qty/UoM).
    /// </summary>
    /// <returns>The new RFQ id.</returns>
    /// <exception cref="KeyNotFoundException">Lead not found in the business unit.</exception>
    /// <exception cref="InvalidOperationException">Lead not accepted / already converted.</exception>
    /// <exception cref="ArgumentException">Request references foreign lines/products or has invalid values.</exception>
    Task<long> ConvertAsync(long leadId, long businessUnitId, ConvertRequest request, CancellationToken ct);
}
