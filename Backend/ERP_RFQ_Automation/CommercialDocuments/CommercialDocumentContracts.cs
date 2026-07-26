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

