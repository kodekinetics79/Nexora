namespace ERP_RFQ_Automation.InboundLogistics;

public sealed class InboundLogisticsValidationException(string message) : InvalidOperationException(message);

public sealed class InboundLogisticsConflictException(string message) : InvalidOperationException(message);

/// <summary>
/// FR-MAS-01..05. Every write path for inbound supplier shipments, plus the reference and policy
/// data the Material Available Date is derived from.
///
/// <para>Command shape follows <c>IProcurementApplicationService</c> exactly — idempotency key,
/// actor and correlation id on every mutation, an expected version on every update — because these
/// writes append to the same <c>ProcurementEvent</c> ledger and a caller must not have to learn two
/// conventions to move one purchase order through its life.</para>
/// </summary>
public interface IInboundShipmentApplicationService
{
    Task<InboundShipmentView> CreateShipmentAsync(CreateInboundShipmentCommand command, CancellationToken ct = default);

    Task<InboundShipmentView> RecordMilestoneAsync(RecordInboundShipmentMilestoneCommand command, CancellationToken ct = default);

    Task<InboundShipmentView> UpdateTrackingAsync(UpdateInboundShipmentTrackingCommand command, CancellationToken ct = default);

    Task<IReadOnlyCollection<InboundShipmentView>> GetPurchaseOrderShipmentsAsync(
        long businessUnitId, long purchaseOrderId, CancellationToken ct = default);

    Task<IReadOnlyCollection<PortOfEntryView>> GetPortsOfEntryAsync(
        long businessUnitId, bool includeInactive, CancellationToken ct = default);

    Task<PortOfEntryView> CreatePortOfEntryAsync(CreatePortOfEntryCommand command, CancellationToken ct = default);

    Task<PortOfEntryView> SetPortOfEntryActiveAsync(SetPortOfEntryActiveCommand command, CancellationToken ct = default);

    /// <summary>
    /// Loads the canonical Saudi entry points into the tenant. Idempotent: codes the tenant already
    /// has are left exactly as they are, including any local edits.
    /// </summary>
    Task<IReadOnlyCollection<PortOfEntryView>> ImportStandardSaudiPortsAsync(
        ImportStandardSaudiPortsCommand command, CancellationToken ct = default);

    Task<InboundLogisticsPolicyView> GetPolicyAsync(long businessUnitId, CancellationToken ct = default);

    Task<InboundLogisticsPolicyView> UpdatePolicyAsync(UpdateInboundLogisticsPolicyCommand command, CancellationToken ct = default);
}

// ---------------------------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------------------------

/// <param name="Lines">
/// One entry per purchase order line being shipped. Empty is refused: a shipment with no quantity on
/// it is a record that reconciles against nothing and would still consume a shipment number.
/// </param>
public sealed record CreateInboundShipmentCommand(
    long BusinessUnitId,
    long PurchaseOrderId,
    IReadOnlyCollection<InboundShipmentLineCommand> Lines,
    string IdempotencyKey,
    string Actor,
    string CorrelationId,
    string? CarrierName = null,
    string? TrackingReference = null,
    DateOnly? EtaDate = null,
    long? PortOfEntryId = null,
    DateOnly? ReadyAtFactoryOn = null);

public sealed record InboundShipmentLineCommand(long PurchaseOrderLineId, decimal Quantity);

public sealed record RecordInboundShipmentMilestoneCommand(
    long BusinessUnitId,
    long ShipmentId,
    long ExpectedVersion,
    string Milestone,
    DateOnly OccurredOn,
    string IdempotencyKey,
    string Actor,
    string CorrelationId,
    long? PortOfEntryId = null,
    DateOnly? EtaDate = null,
    string? Note = null);

/// <param name="ClearEta">
/// Null on the wire cannot mean both "unchanged" and "erase". A forwarder that withdraws an ETA is a
/// real event — the Material Available Date must stop being derived from a date nobody stands
/// behind — so it gets its own explicit instruction.
/// </param>
public sealed record UpdateInboundShipmentTrackingCommand(
    long BusinessUnitId,
    long ShipmentId,
    long ExpectedVersion,
    string IdempotencyKey,
    string Actor,
    string CorrelationId,
    string? CarrierName = null,
    string? TrackingReference = null,
    DateOnly? EtaDate = null,
    bool ClearEta = false,
    string? Note = null);

public sealed record CreatePortOfEntryCommand(
    long BusinessUnitId,
    string Code,
    string Name,
    string Kind,
    string IdempotencyKey,
    string Actor,
    string CorrelationId,
    string CountryCode = "SA",
    string? City = null);

public sealed record SetPortOfEntryActiveCommand(
    long BusinessUnitId,
    long PortOfEntryId,
    bool IsActive,
    long ExpectedVersion,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record ImportStandardSaudiPortsCommand(
    long BusinessUnitId,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

/// <param name="ClearLeadTimes">
/// Puts the policy back to "not configured", which stops every Material Available Date in the tenant
/// from being derived. Deliberately loud, and deliberately separate from omitting a field.
/// </param>
public sealed record UpdateInboundLogisticsPolicyCommand(
    long BusinessUnitId,
    string IdempotencyKey,
    string Actor,
    string CorrelationId,
    int? CustomsClearanceLeadDays = null,
    int? PutawayLeadDays = null,
    bool ClearLeadTimes = false);

// ---------------------------------------------------------------------------------------------
// Views
// ---------------------------------------------------------------------------------------------

public sealed record PortOfEntryView(
    long Id, string Code, string Name, string Kind, string CountryCode, string? City,
    bool IsActive, long Version);

/// <summary>
/// FR-MAS-03 read model. Carries the derived date AND everything that produced it, because a derived
/// date a user cannot interrogate is a number they either trust blindly or ignore.
/// </summary>
public sealed record MaterialAvailabilityView(
    DateOnly? Date,
    string? BasisKind,
    DateOnly? BasisDate,
    int? CustomsClearanceDays,
    int? PutawayDays,
    DateTime? ComputedOn,
    string? UnavailableReason);

public sealed record InboundShipmentLineView(
    long Id,
    long PurchaseOrderLineId,
    long ProductId,
    string Description,
    decimal ShippedQuantity,
    decimal OrderedQuantity,
    decimal PurchaseOrderLineShippedQuantity,
    decimal PurchaseOrderLineReceivedQuantity,
    // How much of this shipment line a goods receipt has actually booked in, and what is still
    // owed one. Written only by the receipt path — see InboundShipmentReceiptSettlement.
    decimal ReceivedQuantity,
    decimal OutstandingReceiptQuantity);

/// <summary>One recorded transition, read back from the append-only <c>ProcurementEvent</c> log.</summary>
public sealed record InboundShipmentMilestoneEventView(
    string EventType,
    string? Milestone,
    DateOnly? OccurredOn,
    string Actor,
    DateTime RecordedOn,
    string? Note);

public sealed record InboundShipmentView(
    long Id,
    long PurchaseOrderId,
    string PurchaseOrderNumber,
    string ShipmentNumber,
    string Milestone,
    DateOnly MilestoneOccurredOn,
    DateOnly? ReadyAtFactoryOn,
    DateOnly? DepartedOriginOn,
    DateOnly? InTransitOn,
    DateOnly? ArrivedSaudiPortOn,
    DateOnly? CustomsClearanceOn,
    DateOnly? ReceivedAtWarehouseOn,
    DateOnly? CancelledOn,
    string? CancellationReason,
    long? PortOfEntryId,
    string? PortOfEntryName,
    string? CarrierName,
    string? TrackingReference,
    string TrackingSource,
    DateOnly? EtaDate,
    DateTime? EtaUpdatedOn,
    string? EtaUpdatedBy,
    MaterialAvailabilityView MaterialAvailability,
    // The purchase order's committed ship date, carried so FR-MAS-04's breach is visible on the
    // shipment itself rather than only in an email.
    DateOnly? CommittedShipDate,
    // The earliest customer required date across the demand this shipment covers, and the reason the
    // risk alert exists. Null when no covered RFQ line states one.
    DateOnly? CustomerRequiredOn,
    // The Gate 4/Gate 5 seam, made visible. RECEIVED_AT_WAREHOUSE says a truck reached the gate; a
    // goods receipt says somebody counted what was on it. These three fields are what stop the
    // milestone from implying the second when only the first happened.
    string ReceiptState,
    decimal ReceiptedQuantity,
    decimal OutstandingReceiptQuantity,
    long Version,
    IReadOnlyCollection<InboundShipmentLineView> Lines,
    IReadOnlyCollection<InboundShipmentMilestoneEventView> History,
    // The permitted next milestones, computed server-side so no client re-implements the ladder and
    // then disagrees with the server about what is legal.
    IReadOnlyCollection<string> AllowedNextMilestones);

public sealed record InboundLogisticsPolicyView(
    long BusinessUnitId,
    int? CustomsClearanceLeadDays,
    int? PutawayLeadDays,
    bool IsConfigured,
    long Version,
    DateTime? ModifiedOn,
    string? ModifiedBy,
    // True while the tenant has never saved a policy and is running on "not configured".
    bool IsDefault);
