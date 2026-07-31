namespace ERP_RFQ_Automation.Procurement;

public interface IProcurementApplicationService
{
    Task<SourcingCaseView> CreateOrOpenSourcingCaseAsync(CreateSourcingCaseCommand command, CancellationToken ct = default)
        => throw new NotSupportedException("Sourcing Cases are not supported by this procurement adapter.");
    Task<SourcingCaseView> GetSourcingCaseAsync(long businessUnitId, long sourcingCaseId, CancellationToken ct = default)
        => throw new NotSupportedException("Sourcing Cases are not supported by this procurement adapter.");
    Task<SourcingCandidateSearchResult> SearchSourcingCandidatesAsync(SearchSourcingCandidatesCommand command, CancellationToken ct = default)
        => throw new NotSupportedException("Supplier candidate search is not supported by this procurement adapter.");
    Task<PreparedSupplierRfqResult> PrepareSupplierRfqAsync(PrepareSupplierRfqCommand command, CancellationToken ct = default)
        => throw new NotSupportedException("Supplier RFQ preparation is not supported by this procurement adapter.");
    Task<QueuedSupplierRfqResult> QueuePreparedSupplierRfqAsync(QueuePreparedSupplierRfqCommand command, CancellationToken ct = default)
        => throw new NotSupportedException("Prepared Supplier RFQ dispatch is not supported by this procurement adapter.");
    Task<ProcurementWorkbench> GetWorkbenchAsync(long businessUnitId, long rfqId, CancellationToken ct = default);
    Task<IReadOnlyCollection<SupplierPurchaseOrderSummary>> SearchPurchaseOrdersAsync(
        long businessUnitId, string? search, int limit, CancellationToken ct = default);
    Task<SolicitationResult> CreateSolicitationAsync(CreateSolicitationCommand command, CancellationToken ct = default);
    Task<SolicitationResult> RetrySolicitationAsync(RetrySolicitationCommand command, CancellationToken ct = default);
    Task<SupplierQuoteResult> CaptureSupplierQuoteAsync(CaptureSupplierQuoteCommand command, CancellationToken ct = default);
    Task<QuoteComparisonResult> CompareQuotesAsync(long businessUnitId, long rfqItemId, CancellationToken ct = default);
    Task<AwardResult> ApproveAwardAsync(ApproveAwardCommand command, CancellationToken ct = default);
    Task<PurchaseOrderResult> CreatePurchaseOrderAsync(CreatePurchaseOrderCommand command, CancellationToken ct = default);
    Task<PurchaseOrderResult> IssuePurchaseOrderAsync(IssuePurchaseOrderCommand command, CancellationToken ct = default);
    Task<GoodsReceiptResult> PostGoodsReceiptAsync(PostGoodsReceiptCommand command, CancellationToken ct = default);
}

public sealed record CreateSourcingCaseCommand(
    long BusinessUnitId,
    long RfqId,
    long RfqItemId,
    int SearchLimit,
    bool SourceEntireQuantity,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record SearchSourcingCandidatesCommand(
    long BusinessUnitId,
    long SourcingCaseId,
    int Limit,
    long ExpectedVersion,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record PrepareSupplierRfqCommand(
    long BusinessUnitId,
    long SourcingCaseId,
    long SupplierId,
    DateTime? DueOn,
    long ExpectedVersion,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record QueuePreparedSupplierRfqCommand(
    long BusinessUnitId,
    long SourcingCaseId,
    long SupplierSolicitationId,
    long ExpectedSourcingCaseVersion,
    long ExpectedSolicitationVersion,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record QueuedSupplierRfqResult(
    long SourcingCaseId,
    long SupplierSolicitationId,
    string Status,
    long SourcingCaseVersion,
    long SolicitationVersion,
    bool Replayed);

public sealed record SourcingCaseView(
    long Id,
    long CommercialDemandLineId,
    long RfqId,
    long RfqItemId,
    string NexoraSerial,
    long? ProductId,
    string? RequestedPartNumber,
    string Description,
    decimal RequestedQuantity,
    decimal StockQuantity,
    decimal UnfulfilledQuantity,
    DateTime? RequiredOn,
    int SearchLimit,
    string Status,
    string NextAction,
    long Version,
    IReadOnlyCollection<SourcingCandidateView> Candidates);

public sealed record SourcingCandidateView(
    long Id,
    long SupplierId,
    string SupplierName,
    string? ContactEmail,
    int Rank,
    string EvidenceType,
    string RecommendationReason,
    decimal EvidenceScore,
    DateTime? EvidenceFreshOn,
    bool Selected,
    string GovernanceStatus,
    string VerificationStatus,
    string ComplianceStatus,
    string RiskStatus,
    string ReadinessStatus,
    bool EligibleForSupplierRfq,
    IReadOnlyCollection<string> BlockingReasons);

public sealed record SourcingCandidateSearchResult(
    long SourcingCaseId,
    int RequestedLimit,
    int ResultCount,
    long Version,
    bool Replayed,
    IReadOnlyCollection<SourcingCandidateView> Candidates);

public sealed record PreparedSupplierRfqResult(
    long SourcingCaseId,
    long SupplierSolicitationId,
    string Status,
    long SourcingCaseVersion,
    long SolicitationVersion,
    bool Replayed);

public sealed record CreateSolicitationCommand(
    long BusinessUnitId,
    long RfqId,
    long SupplierId,
    IReadOnlyCollection<long> RfqItemIds,
    DateTime? DueOn,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record CaptureSupplierQuoteCommand(
    long BusinessUnitId,
    long SolicitationId,
    string SupplierQuoteReference,
    int Revision,
    DateTime ValidUntil,
    string IdempotencyKey,
    string Actor,
    string CorrelationId,
    IReadOnlyCollection<CaptureSupplierQuoteLine> Lines);

public sealed record CaptureSupplierQuoteLine(
    long RfqItemId,
    long? ProductId,
    decimal Quantity,
    decimal UnitPrice,
    long CurrencyId,
    int? LeadTimeDays,
    decimal? AvailableQuantity,
    decimal FreightCost,
    decimal DutyCost,
    decimal OtherCost,
    decimal TaxAmount,
    decimal DiscountAmount,
    decimal? MinimumOrderQuantity,
    decimal? ReliabilitySnapshot);

public sealed record RetrySolicitationCommand(
    long BusinessUnitId,
    long SolicitationId,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record ApproveAwardCommand(
    long BusinessUnitId,
    long SupplierQuotedItemId,
    decimal Quantity,
    long ExpectedQuoteVersion,
    string IdempotencyKey,
    string Actor,
    string CorrelationId,
    long? AwardedByUserId,
    string? Rationale);

public sealed record CreatePurchaseOrderCommand(
    long BusinessUnitId,
    long RfqId,
    long SupplierId,
    long CurrencyId,
    long WarehouseId,
    DateOnly ExpectedOn,
    IReadOnlyCollection<long> AwardIds,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record IssuePurchaseOrderCommand(
    long BusinessUnitId,
    long PurchaseOrderId,
    long ExpectedVersion,
    string DeliveryEvidenceReference,
    string IdempotencyKey,
    string Actor,
    string CorrelationId,
    string? DeliveryEvidenceSha256 = null,
    DateTime? DeliveredOn = null);

public sealed record PostGoodsReceiptCommand(
    long BusinessUnitId,
    long PurchaseOrderId,
    long WarehouseId,
    string ReceiptNumber,
    DateTime ReceivedOn,
    long ExpectedPurchaseOrderVersion,
    IReadOnlyCollection<PostGoodsReceiptLine> Lines,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record PostGoodsReceiptLine(long PurchaseOrderLineId, decimal Quantity);

public sealed record ProcurementWorkbench(
    long RfqId,
    string RfqNumber,
    string? NexoraSerial,
    string? CustomerName,
    string? CurrencyCode,
    IReadOnlyCollection<SourcingLineView> Lines,
    IReadOnlyCollection<SolicitationView> Solicitations,
    IReadOnlyCollection<SupplierOfferView> Offers,
    IReadOnlyCollection<SourcingAwardView> Awards,
    IReadOnlyCollection<SupplierPurchaseOrderView> PurchaseOrders,
    CustomerQuoteDraftView? CustomerQuoteDraft);

public sealed record CustomerQuoteDraftView(long QuoteId, string QuoteNumber, long? CurrencyId,
    IReadOnlyCollection<CustomerQuoteDraftLineView> Lines);
public sealed record CustomerQuoteDraftLineView(long QuoteItemId, long RfqItemId, decimal Quantity,
    decimal UnitPrice, decimal TotalAmount);

public sealed record SourcingLineView(long Id, long RfqId, long? ProductId, long? SourcingCaseId, string? PartNumber,
    string Description, decimal RequestedQuantity, decimal AvailableQuantity, decimal ReservedQuantity,
    decimal ShortfallQuantity, DateTime? RequiredOn, string Resolution, DateTime ResolutionCheckedOn);
public sealed record SolicitationView(long Id, long RfqId, long SupplierId, string SupplierName,
    string? SupplierEmail, IReadOnlyCollection<long> RfqItemIds, string Status, string Channel,
    int AttemptCount, string? ProviderReference,
    string? LastErrorCode, DateTime? SentOn, DateTime? RespondedOn, DateTime UpdatedOn, long Version);
public sealed record SupplierOfferView(long Id, long SolicitationId, long RfqItemId, long SupplierId,
    string SupplierName, string? QuoteReference, int QuoteRevision, long CurrencyId, string CurrencyCode,
    decimal Quantity, decimal? AvailableQuantity, decimal UnitPrice, decimal FreightCost, decimal DutyCost,
    decimal OtherCost, decimal? LandedUnitCost, int? LeadTimeDays, decimal? ReliabilitySnapshot,
    DateTime? ValidUntil, bool Eligible, IReadOnlyCollection<string> BlockingReasons, bool Awarded, long Version);
public sealed record SourcingAwardView(long Id, long RfqItemId, long SupplierQuotedItemId, long SupplierId,
    string SupplierName, decimal Quantity, decimal LandedUnitCost, long CurrencyId, string CurrencyCode,
    string Status, string? Rationale, long? PurchaseOrderId, long Version);
public sealed record SupplierPurchaseOrderLineView(long Id, long RfqItemId, long ProductId, string Description,
    decimal OrderedQuantity, decimal ReceivedQuantity, decimal OpenQuantity, decimal UnitCost,
    decimal LandedUnitCost, long WarehouseId);
public sealed record SupplierPurchaseOrderView(long Id, long RfqId, string PurchaseOrderNumber,
    long SupplierId, string SupplierName, long CurrencyId, string CurrencyCode, string Status,
    decimal TotalValue, DateOnly? ExpectedOn, long Version, IReadOnlyCollection<SupplierPurchaseOrderLineView> Lines);
public sealed record SupplierPurchaseOrderSummary(
    long Id, string PurchaseOrderNumber, long RfqId, string RfqNumber, string? NexoraSerial,
    long SupplierId, string SupplierName, string CurrencyCode, string Status, decimal TotalValue,
    DateOnly? ExpectedOn, DateTime CreatedOn, int LineCount, decimal OpenQuantity);
public sealed record SolicitationResult(long Id, string Status, bool Replayed);
public sealed record SupplierQuoteResult(IReadOnlyCollection<long> LineIds, bool Replayed);
public sealed record AwardResult(long Id, string Status, decimal LandedUnitCost, bool Replayed);
public sealed record PurchaseOrderResult(long Id, string Number, string Status, bool Replayed);
public sealed record GoodsReceiptResult(long Id, string Number, string PurchaseOrderStatus, bool Replayed);

public sealed record QuoteComparisonResult(long RfqItemId, IReadOnlyCollection<QuoteComparisonLine> Lines, long? RecommendedSupplierQuotedItemId);
public sealed record QuoteComparisonLine(
    long SupplierQuotedItemId,
    long SupplierId,
    decimal Quantity,
    decimal? AvailableQuantity,
    decimal UnitPrice,
    decimal? LandedUnitCost,
    long CurrencyId,
    int? LeadTimeDays,
    decimal? Reliability,
    DateTime? ValidUntil,
    IReadOnlyCollection<string> Blockers,
    bool Eligible);

public sealed class ProcurementValidationException : InvalidOperationException
{
    public ProcurementValidationException(string message) : base(message) { }
}

public sealed class ProcurementConflictException : InvalidOperationException
{
    public ProcurementConflictException(string message) : base(message) { }
}
