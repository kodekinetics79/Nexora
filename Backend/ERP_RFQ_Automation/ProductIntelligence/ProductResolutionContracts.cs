namespace ERP_RFQ_Automation.ProductIntelligence;

public enum ProductReferenceKind
{
    Alias,
    Supersession
}

public enum ProductResolutionDecisionState
{
    AutoLinked,
    ReviewRequired,
    Unresolved
}

public static class ProductResolutionMethods
{
    public const string ExactPartNumber = "exact_part_number";
    public const string ExactInternalCode = "exact_internal_code";
    public const string CanonicalCompactIdentity = "canonical_compact_identity";
    public const string ApprovedAlias = "approved_alias";
    public const string ApprovedSupersession = "approved_supersession";
    public const string LocalSimilarity = "local_similarity";
}

public sealed record ProductResolutionEvidence(
    string EvidenceType,
    string Reference,
    string? RawValue = null,
    string? Detail = null);

public sealed record ProductResolutionRequest(
    long BusinessUnitId,
    long SourceLeadRevisionId,
    long SourceLeadItemRevisionId,
    string? OriginalPartNumber,
    string? OriginalManufacturer,
    string? Description,
    IReadOnlyList<ProductResolutionEvidence> Evidence);

public sealed record ProductIdentityCandidate(
    long BusinessUnitId,
    long ProductId,
    string PartNumber,
    string? InternalCode,
    string? Manufacturer,
    string? ProductName,
    string? Description);

public sealed record ApprovedProductReference(
    long BusinessUnitId,
    ProductReferenceKind Kind,
    string ReferenceValue,
    long ProductId,
    string? Manufacturer,
    string ApprovalReference,
    DateTimeOffset ApprovedAtUtc);

public sealed record RankedProductCandidate(
    long ProductId,
    string PartNumber,
    string? InternalCode,
    string? Manufacturer,
    string? ProductName,
    decimal Confidence,
    string Method,
    string Reason,
    IReadOnlyList<ProductResolutionEvidence> Evidence);

public sealed record ProductResolutionResult(
    long BusinessUnitId,
    long SourceLeadRevisionId,
    long SourceLeadItemRevisionId,
    string? OriginalPartNumber,
    string? NormalizedPartNumber,
    string? OriginalManufacturer,
    string? NormalizedManufacturer,
    IReadOnlyList<RankedProductCandidate> RankedCandidates,
    decimal Confidence,
    decimal Margin,
    string? Method,
    string RuleVersion,
    IReadOnlyList<ProductResolutionEvidence> Evidence,
    ProductResolutionDecisionState DecisionState,
    long? ResolvedProductId,
    bool IsAmbiguous,
    bool ExternalProviderUsed);

public interface IProductResolutionCatalog
{
    Task<IReadOnlyList<ProductIdentityCandidate>> GetActiveProductsAsync(
        long businessUnitId,
        CancellationToken cancellationToken = default);
}

public interface IApprovedProductReferenceSource
{
    Task<IReadOnlyList<ApprovedProductReference>> GetApprovedReferencesAsync(
        long businessUnitId,
        CancellationToken cancellationToken = default);
}

public interface IProductItemResolver
{
    Task<ProductResolutionResult> ResolveAsync(
        ProductResolutionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class EmptyApprovedProductReferenceSource : IApprovedProductReferenceSource
{
    public Task<IReadOnlyList<ApprovedProductReference>> GetApprovedReferencesAsync(
        long businessUnitId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ApprovedProductReference>>(Array.Empty<ApprovedProductReference>());
}
