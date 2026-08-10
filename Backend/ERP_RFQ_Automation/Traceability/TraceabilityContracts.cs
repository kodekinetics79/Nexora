namespace ERP_RFQ_Automation.Traceability;

public sealed class MaterialTraceabilityValidationException(string message) : InvalidOperationException(message);

public sealed class MaterialTraceabilityConflictException(string message) : InvalidOperationException(message);

// ---------------------------------------------------------------------------------------------
// Receipt seam (FR-MTR-01). Deliberately the smallest surface that can carry the requirement.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// What the operator said about the material on one receipt line. Every field is optional: a
/// receipt of untracked bulk material supplies none of them and still produces a traceable lot.
/// </summary>
public sealed record ReceiptLotDeclaration(
    // Supplier batch/lot number. Ignored when the product is serial-tracked.
    string? LotNumber = null,
    // One entry per unit for serial-tracked material. Count must equal the received quantity.
    IReadOnlyCollection<string>? SerialNumbers = null,
    // Origin of the goods that actually arrived. Defaults to the purchase-order line's.
    string? CountryOfOrigin = null,
    string? ManufacturerName = null,
    string? ManufacturerPartNumber = null,
    string? SupplierBatchReference = null,
    DateOnly? ManufactureDate = null,
    DateOnly? ExpiryDate = null);

/// <summary>
/// One receipt line as the lot recorder sees it. The recorder never reads the procurement tables
/// itself — the caller already holds them inside its transaction, and re-reading would be both a
/// wasted round trip and an opportunity for the two views to disagree.
/// </summary>
public sealed record ReceiptLotLine(
    long PurchaseOrderLineId,
    long ProductId,
    long InventoryId,
    long WarehouseId,
    decimal Quantity,
    string? OrderedCountryOfOrigin,
    long? RfqItemId,
    ReceiptLotDeclaration? Declaration);

/// <summary>The receipt context a lot needs to state where it came from.</summary>
public sealed record ReceiptLotContext(
    long BusinessUnitId,
    long GoodsReceiptId,
    string ReceiptNumber,
    long SupplierPurchaseOrderId,
    long SupplierId,
    long? CommercialCaseId,
    string? NexoraSerial,
    DateTime ReceivedOn,
    string Actor,
    IReadOnlyCollection<ReceiptLotLine> Lines);

/// <summary>
/// FR-MTR-01. The only writer of <see cref="MaterialLot"/>.
///
/// <para>Called from inside <c>PostGoodsReceiptAsync</c>'s transaction so that stock and the lot
/// that explains it commit or fail together. A separate call afterwards would leave a window — and
/// after a crash, a permanent gap — in which received stock exists with no traceable origin, which
/// is the exact condition FR-MTR-03 says must never arise.</para>
/// </summary>
public interface IMaterialLotRecorder
{
    /// <summary>
    /// Creates the lots for one goods receipt. Joins the caller's ambient transaction; does not
    /// commit. Idempotent through the unique index on
    /// (BusinessUnitId, GoodsReceiptId, SupplierPurchaseOrderLineId, LotNumber) — a replayed
    /// receipt is short-circuited by the caller before this is reached.
    /// </summary>
    Task<IReadOnlyList<MaterialLot>> RecordReceiptLotsAsync(
        ReceiptLotContext context, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------------------------
// Quarantine and release (FR-MTR-05)
// ---------------------------------------------------------------------------------------------

public sealed record QuarantineLotCommand(
    long BusinessUnitId,
    long MaterialLotId,
    long ExpectedVersion,
    string ReasonCode,
    string Reason,
    string Actor,
    string CorrelationId,
    string IdempotencyKey);

public sealed record ReleaseLotCommand(
    long BusinessUnitId,
    long MaterialLotId,
    long ExpectedVersion,
    string Reason,
    string Actor,
    string CorrelationId,
    string IdempotencyKey);

/// <summary>
/// A customer order whose stock hold was released because the lot behind it was quarantined.
///
/// <para>Returned rather than buried in a log, because it is the only way the person pressing
/// "quarantine" learns which customers now have an unfulfilled promise. Silently releasing an
/// order's stock is how a delivery date is missed with nobody knowing why.</para>
/// </summary>
public sealed record DisplacedReservation(
    long ReservationId, long? OrderId, long? OrderItemId, decimal Quantity);

public sealed record LotQuarantineResult(
    long MaterialLotId,
    string LotNumber,
    string Status,
    decimal QuarantinedQuantity,
    decimal InventoryQuarantineQuantity,
    decimal AvailableToPromiseAfter,
    IReadOnlyList<DisplacedReservation> DisplacedReservations,
    long Version,
    bool Replayed);

// ---------------------------------------------------------------------------------------------
// Certificates (FR-MTR-02)
// ---------------------------------------------------------------------------------------------

public sealed record RecordLotCertificateCommand(
    long BusinessUnitId,
    long MaterialLotId,
    string CertificateType,
    string? CertificateNumber,
    string? IssuedBy,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    string? Notes,
    string Actor);

public sealed record LotCertificateView(
    long Id,
    long MaterialLotId,
    string CertificateType,
    string? CertificateNumber,
    string? IssuedBy,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    string ExpiryState,
    int? DaysUntilExpiry,
    long AttachmentId,
    string FileName,
    string ContentSha256,
    string? Notes,
    DateTime UploadedOn,
    string UploadedBy);

// ---------------------------------------------------------------------------------------------
// Fulfilment declaration (FR-MTR-01 forward link, FR-MTR-05 enforcement)
// ---------------------------------------------------------------------------------------------

public sealed record DeclareLotConsumptionCommand(
    long BusinessUnitId,
    long MaterialLotId,
    long OrderId,
    long OrderItemId,
    long? ShipmentId,
    decimal Quantity,
    // Required only when the lot's certificates have lapsed. Naming a reason for a lot that is
    // fully in date is refused, so the field cannot become a habit that silences a future warning.
    string? ComplianceOverrideReason,
    string Actor,
    string CorrelationId,
    string IdempotencyKey);

public sealed record LotConsumptionResult(
    long Id,
    long MaterialLotId,
    string LotNumber,
    long OrderId,
    long OrderItemId,
    long? ShipmentId,
    decimal Quantity,
    decimal LotQuantityRemaining,
    string ComplianceStateAtDeclaration,
    bool Replayed);

// ---------------------------------------------------------------------------------------------
// Trace (FR-MTR-03)
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Kinds of traceability defect the trace reports rather than hides. Mirrors
/// <c>CommercialCaseGapKinds</c> on purpose: the lesson is the same one, applied to material.
/// </summary>
public static class MaterialTraceGapKinds
{
    /// <summary>Goods left the building against this line and no lot was ever declared for them.</summary>
    public const string UndeclaredFulfilment = "UndeclaredFulfilment";

    /// <summary>More lot quantity is declared against a line than has actually shipped.</summary>
    public const string OverDeclared = "OverDeclared";

    /// <summary>A lot already shipped to this customer is now under quarantine. Recall exposure.</summary>
    public const string ShippedLotQuarantined = "ShippedLotQuarantined";

    /// <summary>The lot's certificates had lapsed when it was declared, and someone accepted that.</summary>
    public const string ShippedOnExpiredCertificate = "ShippedOnExpiredCertificate";

    /// <summary>A lot in the chain carries no compliance document at all.</summary>
    public const string NoCertificate = "NoCertificate";

    /// <summary>A certificate on a lot still in stock has expired.</summary>
    public const string CertificateExpired = "CertificateExpired";

    /// <summary>What arrived did not come from where it was ordered from.</summary>
    public const string OriginMismatch = "OriginMismatch";

    /// <summary>The declaration states a different commercial case from the order it belongs to.</summary>
    public const string ConflictingCase = "ConflictingCase";

    /// <summary>The lot was bought against a customer demand but states no commercial case.</summary>
    public const string UnlinkedCase = "UnlinkedCase";
}

public sealed record MaterialTraceGap(
    string Kind,
    string Subject,
    long SubjectId,
    string Reference,
    decimal? Quantity,
    string Detail);

/// <summary>Where a lot came from — FR-MTR-03, the "where-from" half.</summary>
public sealed record LotWhereFromView(
    long MaterialLotId,
    string LotNumber,
    string TrackingMode,
    string Status,
    long ProductId,
    string? PartNumber,
    string? ProductName,
    long WarehouseId,
    string? WarehouseName,
    decimal QuantityReceived,
    decimal QuantityConsumed,
    decimal QuantityRemaining,
    DateTime ReceivedOn,
    long GoodsReceiptId,
    string? ReceiptNumber,
    long SupplierPurchaseOrderId,
    string? PurchaseOrderNumber,
    string? PurchaseOrderDemandSource,
    long SupplierPurchaseOrderLineId,
    long SupplierId,
    string? SupplierName,
    long? RfqId,
    string? RfqNumber,
    long? CommercialCaseId,
    string? NexoraSerial,
    string? CountryOfOrigin,
    string? OrderedCountryOfOrigin,
    string? ManufacturerName,
    string? ManufacturerPartNumber,
    string? SupplierBatchReference,
    DateOnly? ManufactureDate,
    DateOnly? ExpiryDate,
    string? HsCode,
    string? QuarantineReasonCode,
    string? QuarantineReason,
    DateTime? QuarantinedOn,
    string? QuarantinedBy,
    DateTime? ReleasedOn,
    string? ReleasedBy,
    string? ReleaseReason,
    string CertificateState,
    IReadOnlyList<LotCertificateView> Certificates,
    IReadOnlyList<LotFulfilmentView> FulfilledInto,
    IReadOnlyList<MaterialTraceGap> Gaps,
    long Version);

/// <summary>One declared onward use of a lot.</summary>
public sealed record LotFulfilmentView(
    long ConsumptionId,
    long OrderId,
    string? OrderNumber,
    long OrderItemId,
    long? ShipmentId,
    string? ShipmentNumber,
    long? CommercialCaseId,
    string? NexoraSerial,
    decimal Quantity,
    string ComplianceStateAtDeclaration,
    string? ComplianceOverrideReason,
    string? ComplianceOverrideBy,
    DateTime DeclaredOn,
    string DeclaredBy);

/// <summary>Where a sales order's material came from — FR-MTR-03, the "where-used" half.</summary>
public sealed record OrderWhereUsedView(
    long OrderId,
    string OrderNumber,
    long? CommercialCaseId,
    string? NexoraSerial,
    IReadOnlyList<OrderLineTraceView> Lines,
    IReadOnlyList<MaterialTraceGap> Gaps);

public sealed record OrderLineTraceView(
    long OrderItemId,
    long? ProductId,
    string? PartNumber,
    string? Description,
    decimal OrderedQuantity,
    decimal ShippedQuantity,
    decimal DeclaredQuantity,
    decimal UntracedQuantity,
    IReadOnlyList<OrderLineLotView> Lots);

public sealed record OrderLineLotView(
    long ConsumptionId,
    long MaterialLotId,
    string LotNumber,
    string TrackingMode,
    string LotStatus,
    decimal Quantity,
    long SupplierPurchaseOrderId,
    string? PurchaseOrderNumber,
    long SupplierId,
    string? SupplierName,
    string? CountryOfOrigin,
    string? ManufacturerName,
    long? ShipmentId,
    string? ShipmentNumber,
    string ComplianceStateAtDeclaration,
    string CertificateStateNow);

// ---------------------------------------------------------------------------------------------
// Lot search / worklist
// ---------------------------------------------------------------------------------------------

public sealed record LotSummaryView(
    long Id,
    string LotNumber,
    string TrackingMode,
    string Status,
    long ProductId,
    string? PartNumber,
    string? ProductName,
    long WarehouseId,
    string? WarehouseName,
    decimal QuantityReceived,
    decimal QuantityConsumed,
    decimal QuantityRemaining,
    string? CountryOfOrigin,
    string? ManufacturerName,
    long SupplierId,
    string? SupplierName,
    long SupplierPurchaseOrderId,
    string? PurchaseOrderNumber,
    DateTime ReceivedOn,
    string CertificateState,
    int CertificateCount,
    DateOnly? EarliestCertificateExpiry,
    long? CommercialCaseId,
    string? NexoraSerial,
    long Version);

public sealed record LotSearchQuery(
    string? Search = null,
    string? Status = null,
    long? SupplierId = null,
    long? ProductId = null,
    long? WarehouseId = null,
    // Restrict to lots whose certificates have lapsed. The FR-MTR-02 watchlist.
    bool ExpiredCertificatesOnly = false,
    int Limit = 50);

public interface IMaterialTraceabilityService
{
    Task<IReadOnlyList<LotSummaryView>> SearchLotsAsync(
        long businessUnitId, LotSearchQuery query, CancellationToken ct = default);

    Task<LotWhereFromView> GetLotAsync(long businessUnitId, long materialLotId, CancellationToken ct = default);

    Task<OrderWhereUsedView> GetOrderTraceAsync(long businessUnitId, long orderId, CancellationToken ct = default);

    Task<LotQuarantineResult> QuarantineAsync(QuarantineLotCommand command, CancellationToken ct = default);

    Task<LotQuarantineResult> ReleaseAsync(ReleaseLotCommand command, CancellationToken ct = default);

    Task<LotConsumptionResult> DeclareConsumptionAsync(
        DeclareLotConsumptionCommand command, CancellationToken ct = default);
}
