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
    // Duty, other charges and discount are captured, never derived. R8 forbids an HS-code rate
    // engine; it does not forbid recording the duty the buyer already knows they will pay. Without
    // these three the canonical revision could not represent an import at all, and the discount a
    // supplier granted was dropped on the floor between the workbench and the revision.
    decimal DutyAmount,
    decimal OtherAmount,
    decimal DiscountAmount,
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
    long Version,
    IReadOnlyCollection<SupplierQuoteRevisionDetail> Revisions);

/// <summary>
/// The revision as a reviewer sees it. The trade term and the round charges are on it because a
/// reviewer cannot spot missing freight or missing duty on a screen that never showed either: the
/// detail view used to carry lines and evidence only, so the whole charge block was invisible to
/// the only person who could challenge it.
/// </summary>
public sealed record SupplierQuoteRevisionDetail(
    long RevisionId,
    int RevisionNumber,
    string CaptureChannel,
    long CurrencyId,
    DateTime? ValidUntil,
    bool RequiresReview,
    DateTime CapturedOn,
    IReadOnlyCollection<SupplierQuoteLineDetail> Lines,
    IReadOnlyCollection<SupplierQuoteEvidenceDetail> Evidence,
    string? Incoterms = null,
    decimal FreightAmount = 0m,
    decimal TaxAmount = 0m,
    decimal DutyAmount = 0m,
    decimal OtherAmount = 0m,
    decimal DiscountAmount = 0m);

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

public sealed record ProjectSupplierQuoteCommand(
    long BusinessUnitId,
    long SupplierQuoteId,
    long ExpectedVersion,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record SupplierQuoteProjectionResult(
    long SupplierQuoteId,
    long RevisionId,
    IReadOnlyCollection<long> SupplierQuotedItemIds,
    bool Replayed);

public sealed record ApplyCustomerQuotePricingCommand(
    long BusinessUnitId,
    long QuoteItemId,
    long SourcingAwardId,
    decimal TargetMarginPercent,
    string Rationale,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record CustomerQuotePricingResult(
    long DecisionId,
    long QuoteId,
    long QuoteItemId,
    decimal SupplierLandedUnitCost,
    decimal TargetMarginPercent,
    decimal CustomerUnitPrice,
    string NexoraSerial,
    bool Replayed);

public sealed class SupplierQuoteValidationException(string message) : InvalidOperationException(message);
public sealed class SupplierQuoteNotFoundException(string message) : InvalidOperationException(message);
public sealed class SupplierQuoteConflictException(string message) : InvalidOperationException(message);
