namespace ERP_RFQ_Automation.CommercialFinance;

public sealed record CreateInvoiceRequest(DateTime? DocumentDate, DateTime? DueDate, IReadOnlyList<CreateInvoiceLineRequest>? Lines);
public sealed record CreateInvoiceLineRequest(long OrderItemId, decimal Quantity);
public sealed record IssueDocumentRequest(long ExpectedVersion);
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

public sealed record ReceivableLineDto(
    long Id, long? OrderItemId, string Description, decimal Quantity, decimal UnitPrice,
    decimal DiscountAmount, decimal TaxAmount, decimal LineTotal);
public sealed record ReceivableDocumentDto(
    long Id, long? CommercialCaseId, long CustomerId, long? OrderId, long? CurrencyId, string? CurrencyCode,
    string DocumentType, string Status, string? DocumentNumber, DateTime DocumentDate,
    DateTime DueDate, DateTime? IssuedOn, decimal SubTotal, decimal DiscountAmount,
    decimal TaxAmount, decimal TotalAmount, decimal AllocatedAmount, decimal OutstandingAmount,
    long Version, IReadOnlyList<ReceivableLineDto> Lines);
public sealed record CustomerPaymentDto(
    long Id, long CustomerId, long? CommercialCaseId, long? CurrencyId, string? CurrencyCode, string ReceiptNumber,
    string Status, DateTime PaymentDate, decimal Amount, decimal AllocatedAmount,
    decimal UnappliedAmount, long Version);
public sealed record ArOpenItemDto(
    long DocumentId, string DocumentNumber, long CustomerId, long? CommercialCaseId,
    long? CurrencyId, string? CurrencyCode, DateTime DocumentDate, DateTime DueDate, decimal OriginalAmount,
    decimal OutstandingAmount, int DaysPastDue, string AgingBucket);

public sealed class FinanceConflictException(string message) : Exception(message);
