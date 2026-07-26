namespace ERP_RFQ_Automation.SupplierQuotes;

public sealed record CaptureSupplierQuoteCommand(
    long BusinessUnitId,
    long SupplierId,
    long SupplierSolicitationId,
    long SourcingCaseId,
    string NexoraSerial,
    string SupplierQuoteReference,
    int RevisionNumber,
    string CaptureChannel,
    long? SourceDocumentId,
    string SourceIdentity,
    string SourceSha256,
    long CurrencyId,
    DateTime? ValidUntil,
    string? Incoterms,
    decimal FreightAmount,
    decimal TaxAmount,
    string? PaymentTerms,
    string? Notes,
    IReadOnlyCollection<CaptureSupplierQuoteLine> Lines,
    IReadOnlyCollection<CaptureSupplierQuoteEvidence> Evidence,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record CaptureSupplierQuoteLine(
    int LineNumber,
    long RfqItemId,
    long CommercialDemandLineId,
    string? PartNumber,
    string? Manufacturer,
    string? SupplierPartNumber,
    string Description,
    decimal Quantity,
    decimal? AvailableQuantity,
    string UnitOfMeasure,
    decimal UnitPrice,
    decimal? MinimumOrderQuantity,
    int? LeadTimeDays,
    string? AvailabilityType,
    string? OriginCountry,
    string? Warranty,
    bool IsAlternate,
    string? Exceptions,
    IReadOnlyCollection<CaptureSupplierQuoteEvidence> Evidence);

public sealed record CaptureSupplierQuoteEvidence(
    string FieldName,
    string? OriginalValue,
    string? NormalizedValue,
    decimal Confidence,
    string Method,
    string? ModelOrRuleVersion,
    int? SourcePage,
    string? SourceRegion,
    bool Critical);

public sealed record SupplierQuoteAnchor(
    long SupplierId,
    long SupplierSolicitationId,
    long SourcingCaseId,
    long RfqId,
    string NexoraSerial,
    IReadOnlyDictionary<long, long> DemandLineByRfqItem);

public sealed record SupplierQuoteCaptureResult(
    long SupplierQuoteId,
    long RevisionId,
    int RevisionNumber,
    string InboxStatus,
    int ReviewRequiredCount,
    bool Replayed);

public sealed record SupplierQuoteInboxRow(
    long SupplierQuoteId,
    long SupplierId,
    string SupplierName,
    string SupplierQuoteReference,
    string NexoraSerial,
    long SourcingCaseId,
    int CurrentRevisionNumber,
    string InboxStatus,
    DateTime UpdatedOn,
    int ReviewRequiredCount);

public sealed record SupplierQuoteDetail(
    long SupplierQuoteId,
    long SupplierId,
    string SupplierName,
    long SupplierSolicitationId,
    long SourcingCaseId,
    long RfqId,
    string NexoraSerial,
    string SupplierQuoteReference,
    int CurrentRevisionNumber,
    string InboxStatus,
    IReadOnlyCollection<SupplierQuoteRevisionDetail> Revisions);

public sealed record SupplierQuoteRevisionDetail(
    long RevisionId,
    int RevisionNumber,
    string CaptureChannel,
    long CurrencyId,
    DateTime? ValidUntil,
    bool RequiresReview,
    DateTime CapturedOn,
    IReadOnlyCollection<SupplierQuoteLineDetail> Lines,
    IReadOnlyCollection<SupplierQuoteEvidenceDetail> Evidence);

public sealed record SupplierQuoteLineDetail(
    long Id,
    int LineNumber,
    long RfqItemId,
    string? PartNumber,
    string Description,
    decimal Quantity,
    decimal? AvailableQuantity,
    decimal UnitPrice,
    int? LeadTimeDays);

public sealed record SupplierQuoteEvidenceDetail(
    long Id,
    long? SupplierQuoteLineId,
    string FieldName,
    string? OriginalValue,
    string? NormalizedValue,
    decimal Confidence,
    string Method,
    bool Critical,
    bool ReviewRequired,
    string? LatestReviewStatus,
    string? CorrectedValue);

public sealed record ReviewSupplierQuoteFieldCommand(
    long BusinessUnitId,
    long SupplierQuoteId,
    long RevisionId,
    long EvidenceId,
    string Status,
    string? CorrectedValue,
    string Reason,
    string Actor,
    string CorrelationId);

public sealed class SupplierQuoteValidationException(string message) : InvalidOperationException(message);
public sealed class SupplierQuoteNotFoundException(string message) : InvalidOperationException(message);
public sealed class SupplierQuoteConflictException(string message) : InvalidOperationException(message);
