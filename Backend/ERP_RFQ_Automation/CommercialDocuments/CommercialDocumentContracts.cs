using ERP_RFQ_Automation.DocumentIntelligence.Persistence;

namespace ERP_RFQ_Automation.CommercialDocuments;

public sealed record CommercialDocumentMatchReferences(
    long? CustomerRfqId = null,
    long? SupplierRfqId = null,
    long? SourcingCaseId = null,
    long? SupplierQuoteId = null,
    long? PurchaseOrderId = null,
    long? SupplierInvoiceId = null);

public sealed record CommercialDocumentClassificationSignals(
    string? OriginalFileName = null,
    string? Subject = null,
    string? SenderPartyType = null,
    string? BodyExcerpt = null,
    string? ReferenceKind = null);

public sealed record ClassifyCommercialDocumentRequest(
    long SourceDocumentId,
    string IdempotencyKey,
    CommercialDocumentClassificationSignals Signals,
    CommercialDocumentMatchReferences? Matches = null);

public sealed record CommercialDocumentDecision(
    CommercialDocumentType DocumentType,
    decimal Confidence,
    string Method,
    string EvidenceJson,
    bool RequiresReview);

public enum SupplierQuoteProjectionState
{
    NotApplicable,
    ReviewRequired,
    Rejected,
    MissingSupplierRfqMatch,
    MissingSourcingCaseMatch,
    MissingPriorSupplierQuoteMatch,
    AlreadyProjected,
    Ready
}

public sealed record SupplierQuoteProjectionReadiness(
    SupplierQuoteProjectionState State,
    bool IsReady,
    IReadOnlyList<string> BlockingReasons);

public sealed record CommercialDocumentInboxQuery(
    int Page = 1,
    int PageSize = 25,
    CommercialDocumentType? DocumentType = null,
    CommercialDocumentReviewStatus? ReviewStatus = null,
    SupplierQuoteProjectionState? ProjectionState = null);

public sealed record CommercialDocumentInboxItem(
    Guid Id,
    long SourceDocumentId,
    string OriginalFileName,
    string DetectedMimeType,
    DocumentSecurityStatus SecurityStatus,
    DocumentProcessingStatus ProcessingStatus,
    CommercialDocumentType DocumentType,
    CommercialDocumentReviewStatus ReviewStatus,
    decimal Confidence,
    string ClassificationMethod,
    CommercialDocumentMatchReferences Matches,
    SupplierQuoteProjectionReadiness SupplierQuoteProjection,
    int Version,
    DateTimeOffset CreatedOn,
    DateTimeOffset UpdatedOn);

public sealed record CommercialDocumentInboxDetail(
    CommercialDocumentInboxItem Item,
    string SourceDocumentContentHash,
    string SourceObjectVersion,
    string EvidenceJson,
    string? ReviewedBy,
    string? ReviewReason,
    DateTimeOffset? ReviewedOn);

public sealed record CommercialDocumentInboxResult(
    IReadOnlyList<CommercialDocumentInboxItem> Items,
    int TotalCount,
    int Page,
    int PageSize);
