namespace ERP_RFQ_Automation.CommercialFinance;

public sealed record CreateInvoiceRequest(DateTime? DocumentDate, DateTime? DueDate, IReadOnlyList<CreateInvoiceLineRequest>? Lines);
public sealed record CreateInvoiceLineRequest(long OrderItemId, decimal Quantity);
public sealed record CreateAdjustmentRequest(
    string DocumentType,
    DateTime? DocumentDate,
    DateTime? DueDate,
    string ReasonCode,
    string Reason,
    IReadOnlyList<CreateAdjustmentLineRequest> Lines);
public sealed record CreateAdjustmentLineRequest(long ParentLineId, decimal Quantity);
public sealed record IssueDocumentRequest(long ExpectedVersion);
public sealed record CancelDocumentRequest(long ExpectedVersion, string Reason);
public sealed record ReversePaymentRequest(long ExpectedVersion, string Reason);
public sealed record PostPaymentRequest(
    long CustomerId,
    long? CommercialCaseId,
    long? CurrencyId,
    DateTime? PaymentDate,
    decimal Amount,
    string? Method,
    string? BankReference,
    IReadOnlyList<PaymentAllocationRequest> Allocations);
public sealed record PaymentAllocationRequest(long ReceivableDocumentId, decimal Amount);
public sealed record CreateWriteOffRequest(
    DateTime? AccountingDate, string ReasonCode, string Reason, string? EvidenceReference,
    IReadOnlyList<WriteOffAllocationRequest> Allocations);
public sealed record WriteOffAllocationRequest(long ReceivableDocumentId, decimal Amount);
public sealed record CreateRefundRequest(
    long SourcePaymentId, DateTime? RequestedExecutionDate, decimal Amount, string Method,
    string DestinationReference, bool DestinationVerified, string ReasonCode, string Reason,
    string? EvidenceReference);
public sealed record FinanceExceptionActionRequest(long ExpectedVersion, string? Reason = null, string? EvidenceReference = null);
public sealed record RefundDisbursementRequest(long ExpectedVersion, string ProviderReference, string? Reason = null);

public sealed record ReceivableLineDto(
    long Id, long? OrderItemId, long? ParentDocumentLineId, string Description, decimal Quantity, decimal UnitPrice,
    decimal DiscountAmount, decimal TaxAmount, decimal LineTotal);
public sealed record ReceivableDocumentDto(
    long Id, long? CommercialCaseId, long CustomerId, long? OrderId, long? ParentDocumentId,
    string? AdjustmentReasonCode, string? AdjustmentReason, long? CurrencyId, string? CurrencyCode,
    string DocumentType, string Status, string? DocumentNumber, DateTime DocumentDate,
    DateTime DueDate, DateTime? IssuedOn, DateTime? VoidedOn, string? VoidReason, string? VoidedBy, decimal SubTotal, decimal DiscountAmount,
    decimal TaxAmount, decimal TotalAmount, decimal AllocatedAmount, decimal OutstandingAmount,
    long Version, IReadOnlyList<ReceivableLineDto> Lines);
public sealed record CustomerPaymentDto(
    long Id, long CustomerId, long? CommercialCaseId, long? CurrencyId, string? CurrencyCode, string ReceiptNumber,
    string Status, DateTime PaymentDate, decimal Amount, decimal AllocatedAmount,
    decimal UnappliedAmount, long Version);
public sealed record ArOpenItemDto(
    long DocumentId, string DocumentNumber, string DocumentType, long CustomerId, long? CommercialCaseId,
    long? CurrencyId, string? CurrencyCode, DateTime DocumentDate, DateTime DueDate, decimal OriginalAmount,
    decimal OutstandingAmount, int DaysPastDue, string AgingBucket);
public sealed record WriteOffAllocationDto(
    long Id, long ReceivableDocumentId, string DocumentNumber, decimal Amount,
    decimal BalanceBefore, decimal BalanceAfter);
public sealed record ReceivableWriteOffDto(
    long Id, long CustomerId, long? CommercialCaseId, long? CurrencyId, string? CurrencyCode,
    string? WriteOffNumber, string Status, DateTime AccountingDate, decimal TotalAmount,
    string ReasonCode, string Reason, string? EvidenceReference, string PostingStatus,
    string? JournalReference, long Version, string CreatedBy, DateTime CreatedOn,
    string? ApprovedBy, DateTime? ApprovedOn, string? CancelledBy, DateTime? CancelledOn,
    string? CancellationReason, string? ReversedBy, DateTime? ReversedOn, string? ReversalReason,
    string? ReversalEvidenceReference, IReadOnlyList<WriteOffAllocationDto> Allocations);
public sealed record CustomerRefundDto(
    long Id, long SourcePaymentId, string ReceiptNumber, long CustomerId, long? CommercialCaseId,
    long? CurrencyId, string? CurrencyCode, string? RefundNumber, string Status,
    DateTime RequestedExecutionDate, decimal Amount, string Method, string DestinationReference,
    bool DestinationVerified, string ReasonCode, string Reason, string? EvidenceReference,
    string PostingStatus, string? JournalReference, long Version, string CreatedBy,
    DateTime CreatedOn, string? ApprovedBy, DateTime? ApprovedOn, string? ReleasedBy, DateTime? ReleasedOn,
    string? DisbursementUpdatedBy, DateTime? DisbursementUpdatedOn, string? DisbursementFailureReason,
    string? CancelledBy, DateTime? CancelledOn, string? CancellationReason,
    string? ReversedBy, DateTime? ReversedOn, string? ReversalReason, string? ReversalEvidenceReference);
public sealed record WriteOffEligibilityDto(long ReceivableDocumentId, decimal CurrentBalance, decimal PendingAmount, decimal AvailableAmount);
public sealed record RefundEligibilityDto(long SourcePaymentId, decimal PaymentAmount, decimal AllocatedAmount, decimal ReservedAmount, decimal ReleasedAmount, decimal AvailableAmount);

public sealed class FinanceConflictException(string message) : Exception(message);
