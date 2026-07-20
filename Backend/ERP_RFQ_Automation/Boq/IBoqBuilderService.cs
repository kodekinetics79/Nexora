namespace ERP_RFQ_Automation.Boq;

/// <summary>
/// Service RFQ → BOQ engine. Every method takes the caller-resolved
/// businessUnitId (from the JWT / AgentToolContext — never the client body) and
/// applies an explicit BU predicate on top of the EF global query filters, the
/// same defense-in-depth convention as the Pricing engine.
/// </summary>
public interface IBoqBuilderService
{
    /// <summary>
    /// Drafts a BOQ from a lead's extracted content or from raw scope text via the
    /// LLM (ILLMService.DraftServiceBoqAsync). Quantities the source does not state
    /// are NEVER invented — those lines are persisted with IsTbd = true. Drawing
    /// files attempt the vision reader first and fall back to an honest TBD-heavy
    /// skeleton when no vision model is configured. Persists and returns the full tree.
    /// </summary>
    Task<BoqDocumentDto> DraftFromTextAsync(BoqDraftRequest request, long businessUnitId, CancellationToken ct);

    Task<BoqListResultDto> ListAsync(long businessUnitId, int page, int pageSize, string? status, string? search, CancellationToken ct);

    Task<BoqDocumentDto?> GetAsync(long boqDocumentId, long businessUnitId, CancellationToken ct);

    /// <summary>Full-tree upsert (review-workbench style). Returns null when the document is not in this BU.</summary>
    Task<BoqDocumentDto?> UpdateAsync(long boqDocumentId, long businessUnitId, BoqUpdateRequest request, CancellationToken ct);

    Task<BoqDocumentDto?> ApproveAsync(long boqDocumentId, long businessUnitId, string? approvedBy, CancellationToken ct);

    /// <summary>Tenant assembly library; lazily seeds the ~10 starter assemblies on first use (idempotent).</summary>
    Task<IReadOnlyList<BoqAssemblyDto>> GetAssembliesAsync(long businessUnitId, CancellationToken ct);

    /// <summary>
    /// Replaces a BOQ item with the component lines of its assembly (item.AssemblyCode,
    /// or <paramref name="assemblyCode"/> when supplied), quantities multiplied by the
    /// item's quantity and rates taken from the library (Source = "assembly").
    /// Returns the updated full tree.
    /// </summary>
    Task<BoqDocumentDto> ExplodeAssemblyAsync(long boqItemId, long businessUnitId, string? assemblyCode, CancellationToken ct);

    /// <summary>
    /// Recomputes line totals (qty × rate where both exist and the line is not TBD),
    /// section totals, the document total and the TBD count, then saves.
    /// </summary>
    Task RecalcTotalsAsync(long boqDocumentId, long businessUnitId, CancellationToken ct);

    /// <summary>CSV export of the full tree (sections / items / rates / totals; TBD lines marked).</summary>
    Task<string?> ExportCsvAsync(long boqDocumentId, long businessUnitId, CancellationToken ct);
}
