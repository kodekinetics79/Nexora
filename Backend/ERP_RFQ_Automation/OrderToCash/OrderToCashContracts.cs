namespace ERP_RFQ_Automation.OrderToCash;

public sealed record CreateCustomerPurchaseOrderRequest(
    long CommercialCaseId,
    long CustomerId,
    string ExternalPoNumber,
    DateTime PoDate,
    DateTime? ReceivedOn,
    long CurrencyId,
    long ExpectedVersion,
    string CorrelationId,
    IReadOnlyList<CreateCustomerPurchaseOrderLineRequest> Lines);

public sealed record CreateCustomerPurchaseOrderLineRequest(
    string ExternalLineReference,
    long? ProductId,
    string Description,
    decimal OrderedQuantity,
    int? UomId,
    decimal? UnitPrice,
    decimal? LineAmount);

public sealed record UpdateCustomerPurchaseOrderRequest(
    long ExpectedVersion,
    string CorrelationId,
    string ExternalPoNumber,
    DateTime PoDate,
    DateTime? ReceivedOn,
    IReadOnlyList<CreateCustomerPurchaseOrderLineRequest> Lines);

public sealed record CreateCustomerAwardRequest(
    long CustomerPurchaseOrderId,
    long QuoteId,
    long ExpectedVersion,
    string CorrelationId,
    IReadOnlyList<CreateCustomerAwardAllocationRequest> Allocations);

public sealed record CreateCustomerAwardAllocationRequest(
    long CustomerPurchaseOrderLineId,
    long QuoteItemId,
    decimal AwardedQuantity);

public sealed record UpdateCustomerAwardRequest(
    long ExpectedVersion,
    string CorrelationId,
    IReadOnlyList<CreateCustomerAwardAllocationRequest> Allocations);

public sealed record OrderToCashTransitionRequest(long ExpectedVersion, string CorrelationId);
public sealed record OrderToCashCancellationRequest(long ExpectedVersion, string CorrelationId, string Reason);
public sealed record ConvertCustomerAwardToOrderRequest(long ExpectedVersion, string CorrelationId);

public sealed record CustomerPurchaseOrderLineDto(
    long Id,
    string ExternalLineReference,
    long? ProductId,
    string Description,
    decimal OrderedQuantity,
    decimal AwardedQuantity,
    decimal RemainingQuantity,
    int? UomId,
    decimal? UnitPrice,
    decimal? LineAmount,
    long Version);

public sealed record CustomerPurchaseOrderDto(
    long Id,
    long CommercialCaseId,
    long CustomerId,
    string InternalNumber,
    string ExternalPoNumber,
    DateTime PoDate,
    DateTime ReceivedOn,
    long CurrencyId,
    string Status,
    long Version,
    DateTime CreatedOn,
    string CreatedBy,
    DateTime? ModifiedOn,
    string? ModifiedBy,
    string? CancellationReason,
    IReadOnlyList<CustomerPurchaseOrderLineDto> Lines);

public sealed record CustomerAwardLineAllocationDto(
    long Id,
    long CustomerPurchaseOrderLineId,
    long QuoteItemId,
    decimal AwardedQuantity,
    decimal UnitPriceSnapshot,
    decimal DiscountSnapshot,
    decimal TaxSnapshot,
    decimal TotalSnapshot,
    long Version);

public sealed record CustomerAwardDto(
    long Id,
    string AwardNumber,
    long CustomerPurchaseOrderId,
    long QuoteId,
    long CommercialCaseId,
    long CustomerId,
    long CurrencyId,
    string Status,
    long Version,
    DateTime? ConfirmedOn,
    string? ConfirmedBy,
    DateTime? CancelledOn,
    string? CancelledBy,
    string? CancellationReason,
    IReadOnlyList<CustomerAwardLineAllocationDto> Allocations);

public sealed record QuoteAwardBalanceDto(
    long QuoteItemId,
    decimal QuotedQuantity,
    decimal ConfirmedAwardQuantity,
    decimal RemainingQuantity);

public sealed record OrderToCashAuditEventDto(
    long Id,
    string AggregateType,
    long AggregateId,
    long AggregateVersion,
    string CommandType,
    string? PreviousState,
    string NewState,
    string Actor,
    string? Reason,
    string CorrelationId,
    DateTime OccurredOn);
