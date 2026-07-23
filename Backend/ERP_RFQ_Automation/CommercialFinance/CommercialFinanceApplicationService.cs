using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.BankReconciliation;
using ERP_RFQ_Automation.GeneralLedger;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.CommercialFinance;

public interface ICommercialFinanceApplicationService
{
    Task<ReceivableDocumentDto> CreateInvoiceAsync(long businessUnitId, long orderId, string idempotencyKey, CreateInvoiceRequest request, string actor);
    Task<ReceivableDocumentDto> CreateAdjustmentAsync(long businessUnitId, long invoiceId, string idempotencyKey, CreateAdjustmentRequest request, string actor);
    Task<ReceivableDocumentDto> IssueAsync(long businessUnitId, long documentId, IssueDocumentRequest request, string actor);
    Task<ReceivableDocumentDto> IssueAdjustmentAsync(long businessUnitId, long documentId, IssueDocumentRequest request, string actor);
    Task<ReceivableDocumentDto> CancelAsync(long businessUnitId, long documentId, CancelDocumentRequest request, string actor);
    Task<ReceivableDocumentDto> CancelAdjustmentAsync(long businessUnitId, long documentId, CancelDocumentRequest request, string actor);
    Task<ReceivableDocumentDto?> GetDocumentAsync(long businessUnitId, long documentId);
    Task<IReadOnlyList<ReceivableDocumentDto>> GetDocumentsAsync(long businessUnitId, long? customerId, string? status);
    Task<CustomerPaymentDto> PostPaymentAsync(long businessUnitId, string idempotencyKey, PostPaymentRequest request, string actor);
    Task<IReadOnlyList<CustomerPaymentDto>> GetPaymentsAsync(long businessUnitId, long? customerId, string? status);
    Task<CustomerPaymentDto> ReversePaymentAsync(long businessUnitId, long paymentId, ReversePaymentRequest request, string actor);
    Task<IReadOnlyList<ArOpenItemDto>> GetOpenItemsAsync(long businessUnitId, DateTime? asOf);
    Task<WriteOffEligibilityDto> GetWriteOffEligibilityAsync(long businessUnitId, long documentId);
    Task<ReceivableWriteOffDto> CreateWriteOffAsync(long businessUnitId, string idempotencyKey, CreateWriteOffRequest request, string actor);
    Task<ReceivableWriteOffDto> PostWriteOffAsync(long businessUnitId, long writeOffId, FinanceExceptionActionRequest request, string actor);
    Task<ReceivableWriteOffDto> CancelWriteOffAsync(long businessUnitId, long writeOffId, FinanceExceptionActionRequest request, string actor);
    Task<ReceivableWriteOffDto> ReverseWriteOffAsync(long businessUnitId, long writeOffId, FinanceExceptionActionRequest request, string actor);
    Task<IReadOnlyList<ReceivableWriteOffDto>> GetWriteOffsAsync(long businessUnitId, long? customerId, string? status);
    Task<RefundEligibilityDto> GetRefundEligibilityAsync(long businessUnitId, long paymentId);
    Task<CustomerRefundDto> CreateRefundAsync(long businessUnitId, string idempotencyKey, CreateRefundRequest request, string actor);
    Task<CustomerRefundDto> ApproveRefundAsync(long businessUnitId, long refundId, FinanceExceptionActionRequest request, string actor);
    Task<CustomerRefundDto> ReleaseRefundAsync(long businessUnitId, long refundId, FinanceExceptionActionRequest request, string actor);
    Task<CustomerRefundDto> ConfirmRefundDisbursementAsync(long businessUnitId, long refundId, RefundDisbursementRequest request, string actor);
    Task<CustomerRefundDto> FailRefundDisbursementAsync(long businessUnitId, long refundId, RefundDisbursementRequest request, string actor);
    Task<CustomerRefundDto> CancelRefundAsync(long businessUnitId, long refundId, FinanceExceptionActionRequest request, string actor);
    Task<CustomerRefundDto> ReverseRefundAsync(long businessUnitId, long refundId, FinanceExceptionActionRequest request, string actor);
    Task<IReadOnlyList<CustomerRefundDto>> GetRefundsAsync(long businessUnitId, long? customerId, string? status);
}

public sealed class CommercialFinanceApplicationService(ErpRfqAutomationContext context,
    IInternalSourceJournalPostingService journalWriter)
    : ICommercialFinanceApplicationService
{
    private readonly ErpRfqAutomationContext _context = context;
    private readonly IInternalSourceJournalPostingService _journalWriter = journalWriter;
    public CommercialFinanceApplicationService(ErpRfqAutomationContext context)
        : this(context, new InternalSourceJournalPostingService(context)) { }

    public async Task<ReceivableDocumentDto> CreateInvoiceAsync(
        long businessUnitId, long orderId, string idempotencyKey, CreateInvoiceRequest request, string actor)
    {
        ValidateKey(idempotencyKey);
        var requestHash = Hash(new { OrderId = orderId, Request = request });

        return await InSerializableTransactionAsync(async () =>
        {
            var replay = await _context.ReceivableDocuments
                .Include(x => x.Lines)
                .FirstOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey);
            if (replay is not null)
            {
                EnsureReplay(replay.RequestHash, requestHash);
                return await MapDocumentAsync(replay);
            }

            var order = await LockOrderAsync(orderId, businessUnitId);
            if (!order.IsActive || order.OrderItems.Count == 0)
                throw new FinanceConflictException("Only an active order with lines can be invoiced.");
            if (!await IsInvoiceEligibleOrderAsync(order, businessUnitId))
                throw new FinanceConflictException("The order must be confirmed, completed, shipped, or backed by an accepted customer quote before invoicing.");

            var requested = request.Lines is { Count: > 0 }
                ? request.Lines.GroupBy(x => x.OrderItemId).ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity))
                : order.OrderItems.ToDictionary(x => x.Id, x => x.Quantity);
            if (requested.Values.Any(x => x <= 0))
                throw new ArgumentException("Invoice quantities must be positive.");

            var priorLines = await _context.ReceivableDocumentLines
            .Where(x => x.BusinessUnitId == businessUnitId && requested.Keys.Contains(x.OrderItemId ?? 0) &&
                        x.Document.DocumentType == ReceivableDocumentTypes.Invoice &&
                        x.Document.Status == ReceivableDocumentStatuses.Issued)
            .GroupBy(x => x.OrderItemId!.Value)
            .Select(x => new { OrderItemId = x.Key, Quantity = x.Sum(y => y.Quantity) })
            .ToDictionaryAsync(x => x.OrderItemId, x => x.Quantity);

            var document = new ReceivableDocument
        {
            BusinessUnitId = businessUnitId,
            CommercialCaseId = await ResolveCommercialCaseIdAsync(order, businessUnitId),
            CustomerId = order.CustomerId,
            OrderId = order.Id,
            CurrencyId = order.CurrencyId,
            DocumentType = ReceivableDocumentTypes.Invoice,
            Status = ReceivableDocumentStatuses.Draft,
            DocumentDate = request.DocumentDate?.Date ?? DateTime.UtcNow.Date,
            DueDate = request.DueDate?.Date ?? DateTime.UtcNow.Date.AddDays(30),
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            CreatedBy = actor,
            CreatedOn = DateTime.UtcNow
        };
            if (document.DueDate < document.DocumentDate)
                throw new ArgumentException("Due date cannot be before document date.");

            foreach (var pair in requested)
            {
            var source = order.OrderItems.SingleOrDefault(x => x.Id == pair.Key)
                ?? throw new ArgumentException($"Order line {pair.Key} does not belong to this order.");
            var alreadyInvoiced = priorLines.GetValueOrDefault(source.Id);
            if (alreadyInvoiced + pair.Value > source.Quantity)
                throw new FinanceConflictException($"Invoice quantity exceeds the remaining quantity for order line {source.Id}.");

            var ratio = pair.Value / source.Quantity;
            var gross = Round(pair.Value * source.UnitPrice);
            var discount = Round(source.Discount * ratio);
            var tax = Round(source.TaxAmount * ratio);
            document.Lines.Add(new ReceivableDocumentLine
            {
                BusinessUnitId = businessUnitId,
                OrderItemId = source.Id,
                Description = source.Description ?? $"Order item {source.Id}",
                Quantity = pair.Value,
                UnitPrice = source.UnitPrice,
                DiscountAmount = discount,
                TaxAmount = tax,
                LineTotal = Round(gross - discount + tax)
            });
            }

            document.SubTotal = Round(document.Lines.Sum(x => Round(x.Quantity * x.UnitPrice)));
            document.DiscountAmount = Round(document.Lines.Sum(x => x.DiscountAmount));
            document.TaxAmount = Round(document.Lines.Sum(x => x.TaxAmount));
            document.TotalAmount = Round(document.SubTotal - document.DiscountAmount + document.TaxAmount);

            _context.ReceivableDocuments.Add(document);
            await _context.SaveChangesAsync();
            await AddAuditAsync(businessUnitId, "ReceivableDocument", document.Id, "DraftCreated", actor, new { orderId });
            if (!_context.Database.IsNpgsql())
                AddOutbox(businessUnitId, "ReceivableDocument", document.Id, document.Version,
                    "finance.receivable.draft-created", new { document.Id, document.OrderId, document.Status, document.Version });
            await _context.SaveChangesAsync();
            return await MapDocumentAsync(document);
        });
    }

    public async Task<ReceivableDocumentDto> CreateAdjustmentAsync(
        long businessUnitId, long invoiceId, string idempotencyKey, CreateAdjustmentRequest request, string actor)
    {
        ValidateKey(idempotencyKey);
        var documentType = request.DocumentType?.Trim();
        if (documentType is not (ReceivableDocumentTypes.CreditNote or ReceivableDocumentTypes.DebitNote))
            throw new ArgumentException("Document type must be CreditNote or DebitNote.");
        var reason = request.Reason?.Trim();
        var reasonCode = request.ReasonCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 50)
            throw new ArgumentException("An adjustment reason code up to 50 characters is required.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new ArgumentException("An adjustment reason up to 500 characters is required.");
        if (request.Lines is not { Count: > 0 } || request.Lines.Any(x => x.Quantity <= 0) ||
            request.Lines.GroupBy(x => x.ParentLineId).Any(x => x.Count() > 1))
            throw new ArgumentException("Adjustment lines must be unique and have positive quantities.");

        var normalized = request with { DocumentType = documentType, ReasonCode = reasonCode, Reason = reason };
        var requestHash = Hash(new { InvoiceId = invoiceId, Request = normalized });
        return await InSerializableTransactionAsync(async () =>
        {
            var replay = await _context.ReceivableDocuments.Include(x => x.Lines)
                .FirstOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey);
            if (replay is not null)
            {
                EnsureReplay(replay.RequestHash, requestHash);
                return await MapDocumentAsync(replay);
            }

            var invoice = await LockDocumentAsync(invoiceId, businessUnitId);
            if (invoice.DocumentType != ReceivableDocumentTypes.Invoice ||
                invoice.Status != ReceivableDocumentStatuses.Issued)
                throw new FinanceConflictException("Adjustments can only be created against an issued invoice.");

            var documentDate = request.DocumentDate?.Date ?? DateTime.UtcNow.Date;
            var dueDate = request.DueDate?.Date ?? invoice.DueDate.Date;
            if (dueDate < documentDate) dueDate = documentDate;
            var document = new ReceivableDocument
            {
                BusinessUnitId = businessUnitId,
                CommercialCaseId = invoice.CommercialCaseId,
                CustomerId = invoice.CustomerId,
                OrderId = invoice.OrderId,
                ParentDocumentId = invoice.Id,
                AdjustmentReasonCode = reasonCode,
                AdjustmentReason = reason,
                CurrencyId = invoice.CurrencyId,
                DocumentType = documentType,
                Status = ReceivableDocumentStatuses.Draft,
                DocumentDate = documentDate,
                DueDate = dueDate,
                IdempotencyKey = idempotencyKey,
                RequestHash = requestHash,
                CreatedBy = actor,
                CreatedOn = DateTime.UtcNow
            };

            foreach (var requestedLine in request.Lines)
            {
                var source = invoice.Lines.SingleOrDefault(x => x.Id == requestedLine.ParentLineId)
                    ?? throw new ArgumentException($"Invoice line {requestedLine.ParentLineId} does not belong to the parent invoice.");
                if (requestedLine.Quantity > source.Quantity)
                    throw new FinanceConflictException("An adjustment quantity cannot exceed its parent invoice line quantity.");
                var ratio = requestedLine.Quantity / source.Quantity;
                var gross = Round(requestedLine.Quantity * source.UnitPrice);
                var discount = Round(source.DiscountAmount * ratio);
                var tax = Round(source.TaxAmount * ratio);
                document.Lines.Add(new ReceivableDocumentLine
                {
                    BusinessUnitId = businessUnitId,
                    OrderItemId = source.OrderItemId,
                    ParentDocumentLineId = source.Id,
                    Description = source.Description,
                    Quantity = requestedLine.Quantity,
                    UnitPrice = source.UnitPrice,
                    DiscountAmount = discount,
                    TaxAmount = tax,
                    LineTotal = Round(gross - discount + tax)
                });
            }

            document.SubTotal = Round(document.Lines.Sum(x => Round(x.Quantity * x.UnitPrice)));
            document.DiscountAmount = Round(document.Lines.Sum(x => x.DiscountAmount));
            document.TaxAmount = Round(document.Lines.Sum(x => x.TaxAmount));
            document.TotalAmount = Round(document.SubTotal - document.DiscountAmount + document.TaxAmount);
            _context.ReceivableDocuments.Add(document);
            await _context.SaveChangesAsync();
            await AddAuditAsync(businessUnitId, "ReceivableDocument", document.Id, "AdjustmentDraftCreated", actor,
                new { invoiceId, documentType });
            if (!_context.Database.IsNpgsql())
                AddOutbox(businessUnitId, "ReceivableDocument", document.Id, document.Version,
                    AdjustmentEventType(document.DocumentType, "draft-created"),
                    new { document.Id, document.ParentDocumentId, document.DocumentType, document.Status, document.Version });
            await _context.SaveChangesAsync();
            return await MapDocumentAsync(document);
        });
    }

    public async Task<ReceivableDocumentDto> IssueAsync(
        long businessUnitId, long documentId, IssueDocumentRequest request, string actor)
        => await IssueCoreAsync(businessUnitId, documentId, request, actor, adjustment: false);

    public async Task<ReceivableDocumentDto> IssueAdjustmentAsync(
        long businessUnitId, long documentId, IssueDocumentRequest request, string actor)
        => await IssueCoreAsync(businessUnitId, documentId, request, actor, adjustment: true);

    private async Task<ReceivableDocumentDto> IssueCoreAsync(
        long businessUnitId, long documentId, IssueDocumentRequest request, string actor, bool adjustment)
    {
        return await InSerializableTransactionAsync(async () =>
        {
            var document = await LockDocumentAsync(documentId, businessUnitId);
            var isAdjustment = document.DocumentType is ReceivableDocumentTypes.CreditNote or ReceivableDocumentTypes.DebitNote;
            if (isAdjustment != adjustment)
                throw new FinanceConflictException(adjustment
                    ? "Only credit or debit note drafts can be issued through this operation."
                    : "Only invoice drafts can be issued through this operation.");
            if (document.Status == ReceivableDocumentStatuses.Issued)
                return await MapDocumentAsync(document);
            if (document.Version != request.ExpectedVersion)
                throw new FinanceConflictException("The document changed; reload it before issuing.");
            if (document.Status != ReceivableDocumentStatuses.Draft)
                throw new FinanceConflictException("Only draft documents can be issued.");
            if (document.Lines.Count == 0 || document.TotalAmount <= 0)
                throw new FinanceConflictException("A document must have positive reconciled lines before issue.");
            EnsureDocumentReconciles(document);
            if (document.DocumentType == ReceivableDocumentTypes.Invoice)
                await EnsureIssueQuantitiesAsync(document, businessUnitId);
            else
            {
                if (string.Equals(document.CreatedBy, actor, StringComparison.OrdinalIgnoreCase))
                    throw new FinanceConflictException("The adjustment creator cannot issue the same note.");
                await EnsureAdjustmentIssueAsync(document, businessUnitId);
            }

            var databaseAllocatesNumber = _context.Database.IsNpgsql();
            var number = databaseAllocatesNumber
                ? "PENDING-DATABASE-ALLOCATION"
                : await AllocateNumberAsync(businessUnitId, document.DocumentType, document.DocumentDate.Year);
            document.DocumentNumber = number;
            document.Status = ReceivableDocumentStatuses.Issued;
            document.IssuedOn = DateTime.UtcNow;
            document.IssuedBy = actor;
            document.Version++;
            if (!databaseAllocatesNumber)
            {
                await AddAuditAsync(businessUnitId, "ReceivableDocument", document.Id, "Issued", actor, new { number });
                AddOutbox(businessUnitId, "ReceivableDocument", document.Id, document.Version,
                    DocumentEventType(document.DocumentType, "issued"), new { document.Id, document.OrderId, document.Status, document.DocumentNumber, document.Version });
            }
            await _context.SaveChangesAsync();
            if (databaseAllocatesNumber)
                await _context.Entry(document).ReloadAsync();
            return await MapDocumentAsync(document);
        });
    }

    public async Task<ReceivableDocumentDto> CancelAsync(
        long businessUnitId, long documentId, CancelDocumentRequest request, string actor)
        => await CancelCoreAsync(businessUnitId, documentId, request, actor, adjustment: false);

    public async Task<ReceivableDocumentDto> CancelAdjustmentAsync(
        long businessUnitId, long documentId, CancelDocumentRequest request, string actor)
        => await CancelCoreAsync(businessUnitId, documentId, request, actor, adjustment: true);

    private async Task<ReceivableDocumentDto> CancelCoreAsync(
        long businessUnitId, long documentId, CancelDocumentRequest request, string actor, bool adjustment)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new ArgumentException("A cancellation reason up to 500 characters is required.");

        return await InSerializableTransactionAsync(async () =>
        {
            var document = await LockDocumentAsync(documentId, businessUnitId);
            var isAdjustment = document.DocumentType is ReceivableDocumentTypes.CreditNote or ReceivableDocumentTypes.DebitNote;
            if (isAdjustment != adjustment)
                throw new FinanceConflictException(adjustment
                    ? "Only credit or debit note drafts can be cancelled through this operation."
                    : "Only invoice drafts can be cancelled through this operation.");
            if (document.Status == ReceivableDocumentStatuses.Cancelled)
            {
                if (!string.Equals(document.VoidReason, reason, StringComparison.Ordinal))
                    throw new FinanceConflictException("The document was already cancelled with a different reason.");
                return await MapDocumentAsync(document);
            }
            if (document.Version != request.ExpectedVersion)
                throw new FinanceConflictException("The document changed; reload it before cancelling.");
            if (document.Status != ReceivableDocumentStatuses.Draft)
                throw new FinanceConflictException("Only draft documents can be cancelled.");

            var databaseWritesAudit = _context.Database.IsNpgsql();
            document.Status = ReceivableDocumentStatuses.Cancelled;
            document.VoidedOn = DateTime.UtcNow;
            document.VoidReason = reason;
            document.VoidedBy = actor;
            document.Version++;
            if (!databaseWritesAudit)
            {
                await AddAuditAsync(businessUnitId, "ReceivableDocument", document.Id, "DraftCancelled", actor,
                    new { Reason = reason });
                AddOutbox(businessUnitId, "ReceivableDocument", document.Id, document.Version,
                    DocumentEventType(document.DocumentType, "cancelled"), new { document.Id, document.OrderId, document.Status, document.Version });
            }
            await _context.SaveChangesAsync();
            if (databaseWritesAudit)
                await _context.Entry(document).ReloadAsync();
            return await MapDocumentAsync(document);
        });
    }

    public async Task<ReceivableDocumentDto?> GetDocumentAsync(long businessUnitId, long documentId)
    {
        var document = await _context.ReceivableDocuments.Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == documentId && x.BusinessUnitId == businessUnitId);
        return document is null ? null : await MapDocumentAsync(document);
    }

    public async Task<IReadOnlyList<ReceivableDocumentDto>> GetDocumentsAsync(
        long businessUnitId, long? customerId, string? status)
    {
        var query = _context.ReceivableDocuments.Include(x => x.Lines)
            .Where(x => x.BusinessUnitId == businessUnitId);
        if (customerId.HasValue) query = query.Where(x => x.CustomerId == customerId.Value);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        var documents = await query.OrderByDescending(x => x.CreatedOn).ToListAsync();
        var result = new List<ReceivableDocumentDto>(documents.Count);
        foreach (var document in documents) result.Add(await MapDocumentAsync(document));
        return result;
    }

    public async Task<CustomerPaymentDto> PostPaymentAsync(
        long businessUnitId, string idempotencyKey, PostPaymentRequest request, string actor)
    {
        ValidateKey(idempotencyKey);
        var paymentAmount = Round(request.Amount);
        if (paymentAmount <= 0) throw new ArgumentException("Payment amount must be positive.");
        if (request.Allocations.GroupBy(x => x.ReceivableDocumentId).Any(x => x.Count() > 1))
            throw new ArgumentException("Combine duplicate allocations for the same document.");
        var normalizedAllocations = request.Allocations
            .Select(x => new PaymentAllocationRequest(x.ReceivableDocumentId, Round(x.Amount)))
            .OrderBy(x => x.ReceivableDocumentId).ToList();
        if (normalizedAllocations.Any(x => x.Amount <= 0) ||
            Round(normalizedAllocations.Sum(x => x.Amount)) > paymentAmount)
            throw new ArgumentException("Allocations must be positive and cannot exceed the payment amount.");
        var normalizedRequest = request with { Amount = paymentAmount, Allocations = normalizedAllocations };
        var requestHash = Hash(normalizedRequest);

        return await InSerializableTransactionAsync(async () =>
        {
            var replay = await _context.CustomerPayments.Include(x => x.Allocations)
                .FirstOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey);
            if (replay is not null)
            {
                EnsureReplay(replay.RequestHash, requestHash);
                return await MapPaymentAsync(replay);
            }

            var customerExists = await _context.Customers.AnyAsync(x => x.Id == request.CustomerId &&
                (x.Buid == businessUnitId || x.Buid == null));
            if (!customerExists) throw new KeyNotFoundException("Customer not found.");
            var bankAccount = await ResolveBankAccountAsync(businessUnitId, request.BankAccountId);
            var book = await _context.LedgerBooks.SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId)
                ?? throw new FinanceConflictException("Configure the governed ledger before posting customer cash.");
            if (request.CurrencyId != book.FunctionalCurrencyId || bankAccount.CurrencyId != book.FunctionalCurrencyId)
                throw new FinanceConflictException("Customer cash posting currently requires the ledger functional currency.");

            var documents = new List<ReceivableDocument>();
            foreach (var allocation in normalizedAllocations)
            {
                var document = await LockDocumentAsync(allocation.ReceivableDocumentId, businessUnitId);
                if (document.DocumentType is not (ReceivableDocumentTypes.Invoice or ReceivableDocumentTypes.DebitNote) ||
                    document.Status != ReceivableDocumentStatuses.Issued)
                    throw new FinanceConflictException("Payments can only be allocated to issued invoices or debit notes.");
                if (document.CustomerId != request.CustomerId || document.CurrencyId != request.CurrencyId)
                    throw new FinanceConflictException("Payment and invoice customer/currency must match.");
                var outstanding = await DocumentOutstandingAsync(document);
                if (allocation.Amount > outstanding)
                    throw new FinanceConflictException($"Allocation exceeds invoice {document.DocumentNumber} outstanding amount.");
                documents.Add(document);
            }

            var documentCaseIds = documents.Select(x => x.CommercialCaseId).Distinct().ToList();
            if (documentCaseIds.Count > 1)
                throw new FinanceConflictException("A payment can only be allocated across invoices from the same commercial case.");
            var commercialCaseId = documentCaseIds.SingleOrDefault();
            if (request.CommercialCaseId.HasValue && request.CommercialCaseId != commercialCaseId)
                throw new FinanceConflictException("The payment commercial case must match its allocated invoices.");

            var receipt = await AllocateNumberAsync(businessUnitId, "Receipt", (request.PaymentDate ?? DateTime.UtcNow).Year);
            var payment = new CustomerPayment
            {
                BusinessUnitId = businessUnitId,
                CustomerId = request.CustomerId,
                CommercialCaseId = commercialCaseId,
                CurrencyId = request.CurrencyId,
                ReceiptNumber = receipt,
                PaymentDate = request.PaymentDate ?? DateTime.UtcNow,
                Amount = paymentAmount,
                Method = request.Method,
                BankReference = request.BankReference,
                BankAccountId = bankAccount.Id,
                IdempotencyKey = idempotencyKey,
                RequestHash = requestHash,
                CreatedBy = actor,
                CreatedOn = DateTime.UtcNow
            };
            foreach (var allocation in normalizedAllocations)
                payment.Allocations.Add(new PaymentAllocation
                {
                    BusinessUnitId = businessUnitId,
                    ReceivableDocumentId = allocation.ReceivableDocumentId,
                    Amount = Round(allocation.Amount),
                    CreatedOn = DateTime.UtcNow
                });
            _context.CustomerPayments.Add(payment);
            await _context.SaveChangesAsync();
            payment.JournalEntryId = await _journalWriter.CreateAndPostCustomerPaymentAsync(payment, bankAccount,
                normalizedAllocations.Sum(x => x.Amount), actor, CancellationToken.None);
            await AddAuditAsync(businessUnitId, "CustomerPayment", payment.Id, "Posted", actor, new { receipt });
            if (!_context.Database.IsNpgsql())
                AddOutbox(businessUnitId, "CustomerPayment", payment.Id, payment.Version,
                    "finance.payment.posted", new { payment.Id, payment.Status, payment.ReceiptNumber, payment.Version });
            await _context.SaveChangesAsync();
            return await MapPaymentAsync(payment);
        });
    }

    public async Task<IReadOnlyList<CustomerPaymentDto>> GetPaymentsAsync(
        long businessUnitId, long? customerId, string? status)
    {
        var query = _context.CustomerPayments.Include(x => x.Allocations)
            .Where(x => x.BusinessUnitId == businessUnitId);
        if (customerId.HasValue) query = query.Where(x => x.CustomerId == customerId.Value);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        var payments = await query.OrderByDescending(x => x.PaymentDate).ThenByDescending(x => x.Id).ToListAsync();
        var result = new List<CustomerPaymentDto>(payments.Count);
        foreach (var payment in payments) result.Add(await MapPaymentAsync(payment));
        return result;
    }

    public async Task<CustomerPaymentDto> ReversePaymentAsync(
        long businessUnitId, long paymentId, ReversePaymentRequest request, string actor)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new ArgumentException("A reversal reason is required.");
        return await InSerializableTransactionAsync(async () =>
        {
            var payment = await LockPaymentAsync(paymentId, businessUnitId);
            if (payment.Status == CustomerPaymentStatuses.Reversed) return await MapPaymentAsync(payment);
            if (payment.Version != request.ExpectedVersion)
                throw new FinanceConflictException("The payment changed; reload it before reversing.");
            if (await ActiveRefundAmountAsync(payment.Id, includeReleased: true) > 0)
                throw new FinanceConflictException("A receipt with an approved or released refund cannot be reversed.");
            if (payment.JournalEntryId.HasValue)
            {
                if (string.Equals(payment.CreatedBy, actor, StringComparison.OrdinalIgnoreCase))
                    throw new FinanceConflictException("Payment reversal requires an independent controller.");
                payment.ReversalJournalEntryId = await _journalWriter.ReverseCustomerPaymentAsync(payment, actor,
                    request.Reason.Trim(), CancellationToken.None);
            }
            payment.Status = CustomerPaymentStatuses.Reversed;
            payment.ReversedBy = actor;
            payment.ReversedOn = DateTime.UtcNow;
            payment.ReversalReason = request.Reason.Trim();
            payment.Version++;
            await AddAuditAsync(businessUnitId, "CustomerPayment", payment.Id, "Reversed", actor, new { request.Reason });
            if (!_context.Database.IsNpgsql())
                AddOutbox(businessUnitId, "CustomerPayment", payment.Id, payment.Version,
                    "finance.payment.reversed", new { payment.Id, payment.Status, payment.ReceiptNumber, payment.Version });
            await _context.SaveChangesAsync();
            return await MapPaymentAsync(payment);
        });
    }

    public async Task<IReadOnlyList<ArOpenItemDto>> GetOpenItemsAsync(long businessUnitId, DateTime? asOf)
    {
        var effectiveDate = (asOf ?? DateTime.UtcNow).Date;
        var documents = await _context.ReceivableDocuments
            .Where(x => x.BusinessUnitId == businessUnitId &&
                        (x.DocumentType == ReceivableDocumentTypes.Invoice ||
                         x.DocumentType == ReceivableDocumentTypes.DebitNote) &&
                        x.Status == ReceivableDocumentStatuses.Issued &&
                        x.IssuedOn < effectiveDate.AddDays(1) &&
                        x.DocumentDate <= effectiveDate)
            .OrderBy(x => x.DueDate)
            .ToListAsync();
        var result = new List<ArOpenItemDto>();
        foreach (var document in documents)
        {
            var outstanding = await DocumentOutstandingAsync(document, effectiveDate.AddDays(1));
            if (outstanding <= 0) continue;
            var days = Math.Max(0, (effectiveDate - document.DueDate.Date).Days);
            result.Add(new ArOpenItemDto(
                document.Id, document.DocumentNumber!, document.DocumentType, document.CustomerId, document.CommercialCaseId,
                document.CurrencyId, await CurrencyCodeAsync(document.CurrencyId), document.DocumentDate, document.DueDate, document.TotalAmount,
                outstanding, days, AgingBucket(days)));
        }
        return result;
    }

    public async Task<WriteOffEligibilityDto> GetWriteOffEligibilityAsync(long businessUnitId, long documentId)
    {
        var document = await _context.ReceivableDocuments.SingleOrDefaultAsync(x =>
            x.Id == documentId && x.BusinessUnitId == businessUnitId)
            ?? throw new KeyNotFoundException("Receivable document not found.");
        if (document.Status != ReceivableDocumentStatuses.Issued ||
            document.DocumentType is not (ReceivableDocumentTypes.Invoice or ReceivableDocumentTypes.DebitNote))
            throw new FinanceConflictException("Only issued invoice and debit-note balances can be written off.");
        var current = await DocumentOutstandingAsync(document);
        var pending = Round(await _context.WriteOffAllocations
            .Where(x => x.BusinessUnitId == businessUnitId && x.ReceivableDocumentId == documentId &&
                x.WriteOff.Status == FinanceExceptionStatuses.Draft)
            .SumAsync(x => (decimal?)x.Amount) ?? 0m);
        return new(documentId, current, pending, current);
    }

    public async Task<ReceivableWriteOffDto> CreateWriteOffAsync(
        long businessUnitId, string idempotencyKey, CreateWriteOffRequest request, string actor)
    {
        ValidateKey(idempotencyKey);
        var reasonCode = RequiredCode(request.ReasonCode, "write-off");
        var reason = RequiredReason(request.Reason, "write-off");
        var evidence = OptionalEvidence(request.EvidenceReference);
        if (request.Allocations is not { Count: > 0 } || request.Allocations.Any(x => x.Amount <= 0) ||
            request.Allocations.GroupBy(x => x.ReceivableDocumentId).Any(x => x.Count() > 1))
            throw new ArgumentException("Write-off allocations must be unique and positive.");
        var allocations = request.Allocations.Select(x => new WriteOffAllocationRequest(
            x.ReceivableDocumentId, Round(x.Amount))).OrderBy(x => x.ReceivableDocumentId).ToList();
        var normalized = request with { ReasonCode = reasonCode, Reason = reason, EvidenceReference = evidence, Allocations = allocations };
        var requestHash = Hash(normalized);

        return await InSerializableTransactionAsync(async () =>
        {
            var replay = await _context.ReceivableWriteOffs.Include(x => x.Allocations)
                .FirstOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey);
            if (replay is not null)
            {
                EnsureReplay(replay.RequestHash, requestHash);
                return await MapWriteOffAsync(replay);
            }

            var documents = new List<ReceivableDocument>();
            foreach (var allocation in allocations)
            {
                var document = await LockDocumentAsync(allocation.ReceivableDocumentId, businessUnitId);
                if (document.Status != ReceivableDocumentStatuses.Issued ||
                    document.DocumentType is not (ReceivableDocumentTypes.Invoice or ReceivableDocumentTypes.DebitNote))
                    throw new FinanceConflictException("Write-offs require issued invoices or debit notes.");
                var available = await DocumentOutstandingAsync(document);
                if (allocation.Amount > available)
                    throw new FinanceConflictException($"Write-off exceeds {document.DocumentNumber} collectible balance.");
                documents.Add(document);
            }
            EnsureSingleCustomerCurrencyCase(documents);
            var first = documents[0];
            var writeOff = new ReceivableWriteOff
            {
                BusinessUnitId = businessUnitId,
                CustomerId = first.CustomerId,
                CommercialCaseId = first.CommercialCaseId,
                CurrencyId = first.CurrencyId,
                AccountingDate = (request.AccountingDate ?? DateTime.UtcNow).Date,
                TotalAmount = Round(allocations.Sum(x => x.Amount)),
                ReasonCode = reasonCode,
                Reason = reason,
                EvidenceReference = evidence,
                IdempotencyKey = idempotencyKey,
                RequestHash = requestHash,
                CreatedBy = actor,
                CreatedOn = DateTime.UtcNow
            };
            for (var index = 0; index < allocations.Count; index++)
            {
                var balance = await DocumentOutstandingAsync(documents[index]);
                writeOff.Allocations.Add(new WriteOffAllocation
                {
                    BusinessUnitId = businessUnitId,
                    ReceivableDocumentId = documents[index].Id,
                    Amount = allocations[index].Amount,
                    BalanceBefore = balance,
                    BalanceAfter = Round(balance - allocations[index].Amount)
                });
            }
            _context.ReceivableWriteOffs.Add(writeOff);
            await _context.SaveChangesAsync();
            await AddAuditAsync(businessUnitId, "ReceivableWriteOff", writeOff.Id, "DraftCreated", actor,
                new { writeOff.TotalAmount, writeOff.ReasonCode });
            if (!_context.Database.IsNpgsql())
                AddOutbox(businessUnitId, "ReceivableWriteOff", writeOff.Id, writeOff.Version,
                    "finance.write-off.draft-created", new { writeOff.Id, writeOff.TotalAmount, writeOff.Status, writeOff.Version });
            await _context.SaveChangesAsync();
            return await MapWriteOffAsync(writeOff);
        });
    }

    public Task<ReceivableWriteOffDto> PostWriteOffAsync(
        long businessUnitId, long writeOffId, FinanceExceptionActionRequest request, string actor)
        => TransitionWriteOffAsync(businessUnitId, writeOffId, request, actor, FinanceExceptionStatuses.Posted);

    public Task<ReceivableWriteOffDto> CancelWriteOffAsync(
        long businessUnitId, long writeOffId, FinanceExceptionActionRequest request, string actor)
        => TransitionWriteOffAsync(businessUnitId, writeOffId, request, actor, FinanceExceptionStatuses.Cancelled);

    public Task<ReceivableWriteOffDto> ReverseWriteOffAsync(
        long businessUnitId, long writeOffId, FinanceExceptionActionRequest request, string actor)
        => TransitionWriteOffAsync(businessUnitId, writeOffId, request, actor, FinanceExceptionStatuses.Reversed);

    public async Task<IReadOnlyList<ReceivableWriteOffDto>> GetWriteOffsAsync(
        long businessUnitId, long? customerId, string? status)
    {
        var query = _context.ReceivableWriteOffs.Include(x => x.Allocations)
            .Where(x => x.BusinessUnitId == businessUnitId);
        if (customerId.HasValue) query = query.Where(x => x.CustomerId == customerId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        var rows = await query.OrderByDescending(x => x.CreatedOn).ToListAsync();
        var result = new List<ReceivableWriteOffDto>(rows.Count);
        foreach (var row in rows) result.Add(await MapWriteOffAsync(row));
        return result;
    }

    public async Task<RefundEligibilityDto> GetRefundEligibilityAsync(long businessUnitId, long paymentId)
    {
        var payment = await _context.CustomerPayments.Include(x => x.Allocations)
            .SingleOrDefaultAsync(x => x.Id == paymentId && x.BusinessUnitId == businessUnitId)
            ?? throw new KeyNotFoundException("Payment not found.");
        var allocated = payment.Status == CustomerPaymentStatuses.Posted ? Round(payment.Allocations.Sum(x => x.Amount)) : 0m;
        var reserved = await ActiveRefundAmountAsync(paymentId, includeReleased: false);
        var released = await ReleasedRefundAmountAsync(paymentId);
        var available = payment.Status == CustomerPaymentStatuses.Posted
            ? Round(payment.Amount - allocated - reserved - released)
            : 0m;
        return new(paymentId, payment.Amount, allocated, reserved, released, available);
    }

    public async Task<CustomerRefundDto> CreateRefundAsync(
        long businessUnitId, string idempotencyKey, CreateRefundRequest request, string actor)
    {
        ValidateKey(idempotencyKey);
        var amount = Round(request.Amount);
        if (amount <= 0) throw new ArgumentException("Refund amount must be positive.");
        var reasonCode = RequiredCode(request.ReasonCode, "refund");
        var reason = RequiredReason(request.Reason, "refund");
        var evidence = OptionalEvidence(request.EvidenceReference);
        var method = request.Method?.Trim();
        var destination = request.DestinationReference?.Trim();
        if (string.IsNullOrWhiteSpace(method) || method.Length > 50)
            throw new ArgumentException("A refund method up to 50 characters is required.");
        if (string.IsNullOrWhiteSpace(destination) || destination.Length > 200 || !request.DestinationVerified ||
            !Regex.IsMatch(destination, "^token:[A-Za-z0-9_-]{8,180}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("A verified provider destination token is required; raw bank or card details are not accepted.");
        var normalized = request with { Amount = amount, Method = method, DestinationReference = destination,
            ReasonCode = reasonCode, Reason = reason, EvidenceReference = evidence };
        var requestHash = Hash(normalized);

        return await InSerializableTransactionAsync(async () =>
        {
            var replay = await _context.CustomerRefunds.Include(x => x.SourcePayment)
                .FirstOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey);
            if (replay is not null)
            {
                EnsureReplay(replay.RequestHash, requestHash);
                return await MapRefundAsync(replay);
            }
            var payment = await LockPaymentAsync(request.SourcePaymentId, businessUnitId);
            if (payment.Status != CustomerPaymentStatuses.Posted)
                throw new FinanceConflictException("Only posted receipts can fund a refund.");
            var eligibility = await GetRefundEligibilityAsync(businessUnitId, payment.Id);
            if (amount > eligibility.AvailableAmount)
                throw new FinanceConflictException("Refund amount exceeds the unapplied receipt balance.");
            var bankAccountId = request.BankAccountId ?? payment.BankAccountId
                ?? throw new FinanceConflictException("The source receipt has no governed bank account.");
            var bankAccount = await ResolveBankAccountAsync(businessUnitId, bankAccountId);
            if (payment.BankAccountId.HasValue && bankAccount.Id != payment.BankAccountId)
                throw new FinanceConflictException("A refund must use the source receipt bank account.");
            var refund = new CustomerRefund
            {
                BusinessUnitId = businessUnitId,
                SourcePaymentId = payment.Id,
                CustomerId = payment.CustomerId,
                CommercialCaseId = payment.CommercialCaseId,
                CurrencyId = payment.CurrencyId,
                RequestedExecutionDate = (request.RequestedExecutionDate ?? DateTime.UtcNow).Date,
                Amount = amount,
                Method = method,
                DestinationReference = destination,
                DestinationVerified = true,
                ReasonCode = reasonCode,
                Reason = reason,
                EvidenceReference = evidence,
                BankAccountId = bankAccount.Id,
                IdempotencyKey = idempotencyKey,
                RequestHash = requestHash,
                CreatedBy = actor,
                CreatedOn = DateTime.UtcNow
            };
            _context.CustomerRefunds.Add(refund);
            await _context.SaveChangesAsync();
            await AddAuditAsync(businessUnitId, "CustomerRefund", refund.Id, "DraftCreated", actor,
                new { refund.SourcePaymentId, refund.Amount, refund.ReasonCode });
            if (!_context.Database.IsNpgsql())
                AddOutbox(businessUnitId, "CustomerRefund", refund.Id, refund.Version,
                    "finance.refund.draft-created", new { refund.Id, refund.SourcePaymentId, refund.Amount, refund.Status, refund.Version });
            await _context.SaveChangesAsync();
            return await MapRefundAsync(refund);
        });
    }

    public Task<CustomerRefundDto> ApproveRefundAsync(long businessUnitId, long refundId, FinanceExceptionActionRequest request, string actor)
        => TransitionRefundAsync(businessUnitId, refundId, request, actor, FinanceExceptionStatuses.Approved);
    public Task<CustomerRefundDto> ReleaseRefundAsync(long businessUnitId, long refundId, FinanceExceptionActionRequest request, string actor)
        => TransitionRefundAsync(businessUnitId, refundId, request, actor, FinanceExceptionStatuses.Released);
    public Task<CustomerRefundDto> ConfirmRefundDisbursementAsync(
        long businessUnitId, long refundId, RefundDisbursementRequest request, string actor)
        => TransitionRefundDisbursementAsync(businessUnitId, refundId, request, actor, succeeded: true);
    public Task<CustomerRefundDto> FailRefundDisbursementAsync(
        long businessUnitId, long refundId, RefundDisbursementRequest request, string actor)
        => TransitionRefundDisbursementAsync(businessUnitId, refundId, request, actor, succeeded: false);
    public Task<CustomerRefundDto> CancelRefundAsync(long businessUnitId, long refundId, FinanceExceptionActionRequest request, string actor)
        => TransitionRefundAsync(businessUnitId, refundId, request, actor, FinanceExceptionStatuses.Cancelled);
    public Task<CustomerRefundDto> ReverseRefundAsync(long businessUnitId, long refundId, FinanceExceptionActionRequest request, string actor)
        => TransitionRefundAsync(businessUnitId, refundId, request, actor, FinanceExceptionStatuses.Reversed);

    public async Task<IReadOnlyList<CustomerRefundDto>> GetRefundsAsync(long businessUnitId, long? customerId, string? status)
    {
        var query = _context.CustomerRefunds.Include(x => x.SourcePayment).Where(x => x.BusinessUnitId == businessUnitId);
        if (customerId.HasValue) query = query.Where(x => x.CustomerId == customerId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        var rows = await query.OrderByDescending(x => x.CreatedOn).ToListAsync();
        var result = new List<CustomerRefundDto>(rows.Count);
        foreach (var row in rows) result.Add(await MapRefundAsync(row));
        return result;
    }

    private async Task<ReceivableWriteOffDto> TransitionWriteOffAsync(
        long businessUnitId, long writeOffId, FinanceExceptionActionRequest request, string actor, string targetStatus)
    {
        var reason = request.Reason?.Trim();
        if (targetStatus is FinanceExceptionStatuses.Cancelled or FinanceExceptionStatuses.Reversed)
            reason = RequiredReason(reason, targetStatus.ToLowerInvariant());
        var reversalEvidence = targetStatus == FinanceExceptionStatuses.Reversed
            ? OptionalEvidence(request.EvidenceReference)
            : null;
        if (targetStatus == FinanceExceptionStatuses.Reversed && string.IsNullOrWhiteSpace(reversalEvidence))
            throw new ArgumentException("Write-off reversal evidence is required.");
        return await InSerializableTransactionAsync(async () =>
        {
            var writeOff = await LockWriteOffAsync(writeOffId, businessUnitId);
            if (writeOff.Status == targetStatus) return await MapWriteOffAsync(writeOff);
            if (writeOff.Version != request.ExpectedVersion)
                throw new FinanceConflictException("The write-off changed; reload it before continuing.");

            if (targetStatus == FinanceExceptionStatuses.Posted)
            {
                if (writeOff.Status != FinanceExceptionStatuses.Draft)
                    throw new FinanceConflictException("Only draft write-offs can be posted.");
                if (string.Equals(writeOff.CreatedBy, actor, StringComparison.OrdinalIgnoreCase))
                    throw new FinanceConflictException("The write-off creator cannot post the same write-off.");
                foreach (var allocation in writeOff.Allocations.OrderBy(x => x.ReceivableDocumentId))
                {
                    var document = await LockDocumentAsync(allocation.ReceivableDocumentId, businessUnitId);
                    var balance = await DocumentOutstandingAsync(document);
                    if (allocation.Amount > balance)
                        throw new FinanceConflictException($"Write-off exceeds {document.DocumentNumber} current balance.");
                    if (allocation.BalanceBefore != balance)
                        throw new FinanceConflictException($"{document.DocumentNumber} balance changed; cancel and recreate the write-off.");
                }
                writeOff.WriteOffNumber = _context.Database.IsNpgsql()
                    ? "PENDING-DATABASE-ALLOCATION"
                    : await AllocateNumberAsync(businessUnitId, "WriteOff", writeOff.AccountingDate.Year);
                writeOff.Status = FinanceExceptionStatuses.Posted;
                writeOff.ApprovedBy = actor;
                writeOff.ApprovedOn = DateTime.UtcNow;
                writeOff.PostingStatus = "PendingExport";
            }
            else if (targetStatus == FinanceExceptionStatuses.Cancelled)
            {
                if (writeOff.Status != FinanceExceptionStatuses.Draft)
                    throw new FinanceConflictException("Only draft write-offs can be cancelled.");
                writeOff.Status = FinanceExceptionStatuses.Cancelled;
                writeOff.CancelledBy = actor;
                writeOff.CancelledOn = DateTime.UtcNow;
                writeOff.CancellationReason = reason;
            }
            else
            {
                if (writeOff.Status != FinanceExceptionStatuses.Posted)
                    throw new FinanceConflictException("Only posted write-offs can be reversed.");
                if (string.Equals(writeOff.CreatedBy, actor, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(writeOff.ApprovedBy, actor, StringComparison.OrdinalIgnoreCase))
                    throw new FinanceConflictException("A write-off maker or poster cannot reverse the same write-off.");
                writeOff.Status = FinanceExceptionStatuses.Reversed;
                writeOff.ReversedBy = actor;
                writeOff.ReversedOn = DateTime.UtcNow;
                writeOff.ReversalReason = reason;
                writeOff.ReversalEvidenceReference = reversalEvidence;
                writeOff.PostingStatus = "ReversalPendingExport";
            }
            writeOff.Version++;
            if (!_context.Database.IsNpgsql())
            {
                await AddAuditAsync(businessUnitId, "ReceivableWriteOff", writeOff.Id, targetStatus, actor,
                    new { writeOff.TotalAmount, writeOff.ReasonCode, Reason = reason });
                AddOutbox(businessUnitId, "ReceivableWriteOff", writeOff.Id, writeOff.Version,
                    $"finance.write-off.{targetStatus.ToLowerInvariant()}",
                    new { writeOff.Id, writeOff.WriteOffNumber, writeOff.TotalAmount, writeOff.Status, writeOff.Version });
            }
            await _context.SaveChangesAsync();
            if (_context.Database.IsNpgsql()) await _context.Entry(writeOff).ReloadAsync();
            return await MapWriteOffAsync(writeOff);
        });
    }

    private async Task<CustomerRefundDto> TransitionRefundAsync(
        long businessUnitId, long refundId, FinanceExceptionActionRequest request, string actor, string targetStatus)
    {
        var reason = request.Reason?.Trim();
        if (targetStatus is FinanceExceptionStatuses.Cancelled or FinanceExceptionStatuses.Reversed)
            reason = RequiredReason(reason, targetStatus.ToLowerInvariant());
        return await InSerializableTransactionAsync(async () =>
        {
            var refund = await LockRefundAsync(refundId, businessUnitId);
            if (refund.Status == targetStatus) return await MapRefundAsync(refund);
            if (refund.Version != request.ExpectedVersion)
                throw new FinanceConflictException("The refund changed; reload it before continuing.");
            var payment = await LockPaymentAsync(refund.SourcePaymentId, businessUnitId);

            if (targetStatus == FinanceExceptionStatuses.Approved)
            {
                if (refund.Status != FinanceExceptionStatuses.Draft || payment.Status != CustomerPaymentStatuses.Posted)
                    throw new FinanceConflictException("Only a draft refund against a posted receipt can be approved.");
                if (string.Equals(refund.CreatedBy, actor, StringComparison.OrdinalIgnoreCase))
                    throw new FinanceConflictException("The refund creator cannot approve the same refund.");
                var available = Round(payment.Amount - payment.Allocations.Sum(x => x.Amount) -
                    await ActiveRefundAmountAsync(payment.Id, includeReleased: true, excludingRefundId: refund.Id));
                if (refund.Amount > available)
                    throw new FinanceConflictException("Refund approval exceeds the current unapplied receipt balance.");
                refund.Status = FinanceExceptionStatuses.Approved;
                refund.ApprovedBy = actor;
                refund.ApprovedOn = DateTime.UtcNow;
                refund.PostingStatus = "Reserved";
            }
            else if (targetStatus == FinanceExceptionStatuses.Released)
            {
                if (refund.Status != FinanceExceptionStatuses.Approved)
                    throw new FinanceConflictException("Only approved refunds can be released.");
                if (string.Equals(refund.CreatedBy, actor, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(refund.ApprovedBy, actor, StringComparison.OrdinalIgnoreCase))
                    throw new FinanceConflictException("Refund release requires a third, independent operator.");
                refund.RefundNumber = _context.Database.IsNpgsql()
                    ? "PENDING-DATABASE-ALLOCATION"
                    : await AllocateNumberAsync(businessUnitId, "Refund", refund.RequestedExecutionDate.Year);
                refund.Status = FinanceExceptionStatuses.Released;
                refund.ReleasedBy = actor;
                refund.ReleasedOn = DateTime.UtcNow;
                refund.PostingStatus = "PendingDisbursement";
            }
            else if (targetStatus == FinanceExceptionStatuses.Cancelled)
            {
                if (refund.Status is not (FinanceExceptionStatuses.Draft or FinanceExceptionStatuses.Approved))
                    throw new FinanceConflictException("Only draft or approved refunds can be cancelled.");
                if (refund.Status == FinanceExceptionStatuses.Approved &&
                    string.Equals(refund.CreatedBy, actor, StringComparison.OrdinalIgnoreCase))
                    throw new FinanceConflictException("The refund creator cannot cancel an approved refund.");
                refund.Status = FinanceExceptionStatuses.Cancelled;
                refund.CancelledBy = actor;
                refund.CancelledOn = DateTime.UtcNow;
                refund.CancellationReason = reason;
                refund.PostingStatus = "Cancelled";
            }
            else
            {
                if (refund.Status != FinanceExceptionStatuses.Released)
                    throw new FinanceConflictException("Only released refunds can be reversed.");
                if (refund.PostingStatus != "Failed")
                    throw new FinanceConflictException("Only a confirmed failed disbursement can restore refundable funds.");
                if (string.Equals(refund.CreatedBy, actor, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(refund.ApprovedBy, actor, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(refund.ReleasedBy, actor, StringComparison.OrdinalIgnoreCase))
                    throw new FinanceConflictException("Refund reversal requires an independent operator.");
                var evidence = OptionalEvidence(request.EvidenceReference);
                if (string.IsNullOrWhiteSpace(evidence))
                    throw new ArgumentException("Refund reversal evidence is required.");
                refund.Status = FinanceExceptionStatuses.Reversed;
                refund.ReversedBy = actor;
                refund.ReversedOn = DateTime.UtcNow;
                refund.ReversalReason = reason;
                refund.ReversalEvidenceReference = evidence;
                refund.PostingStatus = "ReversalPendingExport";
            }
            refund.Version++;
            if (!_context.Database.IsNpgsql())
            {
                await AddAuditAsync(businessUnitId, "CustomerRefund", refund.Id, targetStatus, actor,
                    new { refund.SourcePaymentId, refund.Amount, refund.ReasonCode, Reason = reason });
                AddOutbox(businessUnitId, "CustomerRefund", refund.Id, refund.Version,
                    $"finance.refund.{targetStatus.ToLowerInvariant()}",
                    new { refund.Id, refund.RefundNumber, refund.SourcePaymentId, refund.Amount, refund.Status, refund.Version });
            }
            await _context.SaveChangesAsync();
            if (_context.Database.IsNpgsql()) await _context.Entry(refund).ReloadAsync();
            return await MapRefundAsync(refund);
        });
    }

    private async Task<CustomerRefundDto> TransitionRefundDisbursementAsync(
        long businessUnitId, long refundId, RefundDisbursementRequest request, string actor, bool succeeded)
    {
        var providerReference = request.ProviderReference?.Trim();
        if (string.IsNullOrWhiteSpace(providerReference) || providerReference.Length > 100 ||
            !Regex.IsMatch(providerReference, "^[A-Za-z0-9][A-Za-z0-9._:/-]{7,99}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("A provider reference between 8 and 100 safe characters is required.");
        var failureReason = succeeded ? null : RequiredReason(request.Reason, "disbursement failure");
        return await InSerializableTransactionAsync(async () =>
        {
            var refund = await LockRefundAsync(refundId, businessUnitId);
            var targetPostingStatus = succeeded ? "Settled" : "Failed";
            if (refund.Status == FinanceExceptionStatuses.Released && refund.PostingStatus == targetPostingStatus)
                return await MapRefundAsync(refund);
            if (refund.Version != request.ExpectedVersion)
                throw new FinanceConflictException("The refund changed; reload it before recording the disbursement result.");
            if (refund.Status != FinanceExceptionStatuses.Released || refund.PostingStatus != "PendingDisbursement")
                throw new FinanceConflictException("Only a pending released refund can receive a disbursement result.");
            if (string.Equals(refund.CreatedBy, actor, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(refund.ApprovedBy, actor, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(refund.ReleasedBy, actor, StringComparison.OrdinalIgnoreCase))
                throw new FinanceConflictException("Disbursement confirmation requires an independent reconciler or provider identity.");

            refund.PostingStatus = targetPostingStatus;
            refund.JournalReference = providerReference;
            if (succeeded)
            {
                var bankAccount = await ResolveBankAccountAsync(businessUnitId, refund.BankAccountId);
                refund.JournalEntryId = await _journalWriter.CreateAndPostCustomerRefundAsync(
                    refund, bankAccount, actor, CancellationToken.None);
            }
            refund.DisbursementUpdatedBy = actor;
            refund.DisbursementUpdatedOn = DateTime.UtcNow;
            refund.DisbursementFailureReason = failureReason;
            refund.Version++;
            if (!_context.Database.IsNpgsql())
            {
                var action = succeeded ? "DisbursementConfirmed" : "DisbursementFailed";
                await AddAuditAsync(businessUnitId, "CustomerRefund", refund.Id, action, actor,
                    new { refund.SourcePaymentId, refund.Amount, ProviderReference = providerReference, Reason = failureReason });
                AddOutbox(businessUnitId, "CustomerRefund", refund.Id, refund.Version,
                    succeeded ? "finance.refund.disbursement-confirmed" : "finance.refund.disbursement-failed",
                    new { refund.Id, refund.RefundNumber, refund.SourcePaymentId, refund.Amount,
                        refund.Status, refund.PostingStatus, ProviderReference = providerReference, refund.Version });
            }
            await _context.SaveChangesAsync();
            return await MapRefundAsync(refund);
        });
    }

    private async Task<ReceivableWriteOff> LockWriteOffAsync(long id, long businessUnitId)
    {
        IQueryable<ReceivableWriteOff> query = _context.ReceivableWriteOffs.Include(x => x.Allocations);
        if (_context.Database.IsNpgsql())
            query = _context.ReceivableWriteOffs.FromSqlInterpolated(
                $"SELECT * FROM \"ReceivableWriteOffs\" WHERE \"Id\" = {id} FOR UPDATE").Include(x => x.Allocations);
        return await query.SingleOrDefaultAsync(x => x.Id == id && x.BusinessUnitId == businessUnitId)
            ?? throw new KeyNotFoundException("Write-off not found.");
    }

    private async Task<CustomerRefund> LockRefundAsync(long id, long businessUnitId)
    {
        IQueryable<CustomerRefund> query = _context.CustomerRefunds.Include(x => x.SourcePayment);
        if (_context.Database.IsNpgsql())
            query = _context.CustomerRefunds.FromSqlInterpolated(
                $"SELECT * FROM \"CustomerRefunds\" WHERE \"Id\" = {id} FOR UPDATE").Include(x => x.SourcePayment);
        return await query.SingleOrDefaultAsync(x => x.Id == id && x.BusinessUnitId == businessUnitId)
            ?? throw new KeyNotFoundException("Refund not found.");
    }

    private async Task<CustomerPayment> LockPaymentAsync(long id, long businessUnitId)
    {
        IQueryable<CustomerPayment> query = _context.CustomerPayments.Include(x => x.Allocations);
        if (_context.Database.IsNpgsql())
            query = _context.CustomerPayments.FromSqlInterpolated(
                $"SELECT * FROM \"CustomerPayments\" WHERE \"Id\" = {id} FOR UPDATE").Include(x => x.Allocations);
        return await query.SingleOrDefaultAsync(x => x.Id == id && x.BusinessUnitId == businessUnitId)
            ?? throw new KeyNotFoundException("Payment not found.");
    }

    private async Task<ReceivableDocument> LockDocumentAsync(long documentId, long businessUnitId)
    {
        IQueryable<ReceivableDocument> query = _context.ReceivableDocuments.Include(x => x.Lines);
        if (_context.Database.IsNpgsql())
            query = _context.ReceivableDocuments.FromSqlInterpolated(
                $"SELECT * FROM \"ReceivableDocuments\" WHERE \"Id\" = {documentId} FOR UPDATE").Include(x => x.Lines);
        return await query.FirstOrDefaultAsync(x => x.Id == documentId && x.BusinessUnitId == businessUnitId)
            ?? throw new KeyNotFoundException("Receivable document not found.");
    }

    private async Task EnsureAdjustmentIssueAsync(ReceivableDocument document, long businessUnitId)
    {
        if (!document.ParentDocumentId.HasValue)
            throw new FinanceConflictException("An adjustment must reference its parent invoice.");
        var invoice = await LockDocumentAsync(document.ParentDocumentId.Value, businessUnitId);
        if (invoice.DocumentType != ReceivableDocumentTypes.Invoice ||
            invoice.Status != ReceivableDocumentStatuses.Issued ||
            invoice.CustomerId != document.CustomerId || invoice.CurrencyId != document.CurrencyId ||
            invoice.OrderId != document.OrderId)
            throw new FinanceConflictException("The adjustment parent invoice is no longer eligible.");

        foreach (var line in document.Lines)
        {
            var source = invoice.Lines.SingleOrDefault(x => x.Id == line.ParentDocumentLineId)
                ?? throw new FinanceConflictException("An adjustment line no longer matches its parent invoice.");
            if (line.Quantity > source.Quantity || line.UnitPrice != source.UnitPrice)
                throw new FinanceConflictException("An adjustment line exceeds or diverges from its parent invoice line.");
            var priorAdjustmentQuantity = await _context.ReceivableDocumentLines
                    .Where(x => x.BusinessUnitId == businessUnitId && x.ReceivableDocumentId != document.Id &&
                        x.ParentDocumentLineId == source.Id && x.Document.ParentDocumentId == invoice.Id &&
                        x.Document.DocumentType == document.DocumentType &&
                        x.Document.Status == ReceivableDocumentStatuses.Issued)
                    .SumAsync(x => (decimal?)x.Quantity) ?? 0m;
            if (priorAdjustmentQuantity + line.Quantity > source.Quantity)
                throw new FinanceConflictException($"Issued {document.DocumentType} quantities cannot exceed the parent invoice line quantity.");
        }
        if (document.DocumentType == ReceivableDocumentTypes.CreditNote &&
            document.TotalAmount > await DocumentOutstandingAsync(invoice))
            throw new FinanceConflictException("A credit note cannot exceed the parent invoice's live outstanding balance.");
    }

    private async Task<Order> LockOrderAsync(long orderId, long businessUnitId)
    {
        IQueryable<Order> query = _context.Orders.Include(x => x.OrderItems);
        if (_context.Database.IsNpgsql())
            query = _context.Orders.FromSqlInterpolated(
                $"SELECT * FROM \"Orders\" WHERE \"ID\" = {orderId} FOR UPDATE").Include(x => x.OrderItems);
        return await query.FirstOrDefaultAsync(x => x.Id == orderId && x.BusinessUnitId == businessUnitId)
            ?? throw new KeyNotFoundException("Order not found.");
    }

    private async Task<bool> IsInvoiceEligibleOrderAsync(Order order, long businessUnitId)
    {
        var statusCode = await _context.SetupMasters
            .Where(x => x.SetupId == order.StatusId && x.BusinessUnitId == businessUnitId)
            .Select(x => (x.SetupCode ?? x.SetupValue ?? string.Empty).ToUpper())
            .SingleOrDefaultAsync();
        if (statusCode is "CONFIRMED" or "COMPLETED" or "SHIPPED" or "DELIVERED") return true;
        if (!order.QuoteId.HasValue) return false;

        return await _context.Quotes.AnyAsync(x => x.Id == order.QuoteId && x.BusinessUnitId == businessUnitId &&
            (x.Status!.SetupCode == "ACCEPTED" || x.Status.SetupCode == "ORDERED" ||
             (x.Status.SetupValue ?? string.Empty).ToUpper() == "ACCEPTED" ||
             (x.Status.SetupValue ?? string.Empty).ToUpper() == "ORDERED"));
    }

    private static void EnsureDocumentReconciles(ReceivableDocument document)
    {
        foreach (var line in document.Lines)
        {
            var expected = Round(Round(line.Quantity * line.UnitPrice) - line.DiscountAmount + line.TaxAmount);
            if (line.LineTotal != expected)
                throw new FinanceConflictException("Document line totals do not reconcile to quantity, price, discount, and tax.");
        }

        var subtotal = Round(document.Lines.Sum(x => Round(x.Quantity * x.UnitPrice)));
        var discount = Round(document.Lines.Sum(x => x.DiscountAmount));
        var tax = Round(document.Lines.Sum(x => x.TaxAmount));
        var total = Round(subtotal - discount + tax);
        if (document.SubTotal != subtotal || document.DiscountAmount != discount ||
            document.TaxAmount != tax || document.TotalAmount != total)
            throw new FinanceConflictException("Document header totals do not reconcile to its lines.");
    }

    private async Task EnsureIssueQuantitiesAsync(ReceivableDocument document, long businessUnitId)
    {
        if (!document.OrderId.HasValue) return;

        var order = await LockOrderAsync(document.OrderId.Value, businessUnitId);
        var lineIds = document.Lines.Where(x => x.OrderItemId.HasValue)
            .Select(x => x.OrderItemId!.Value).ToArray();
        var alreadyIssued = await _context.ReceivableDocumentLines
            .Where(x => x.BusinessUnitId == businessUnitId &&
                        x.ReceivableDocumentId != document.Id &&
                        lineIds.Contains(x.OrderItemId ?? 0) &&
                        x.Document.DocumentType == ReceivableDocumentTypes.Invoice &&
                        x.Document.Status == ReceivableDocumentStatuses.Issued)
            .GroupBy(x => x.OrderItemId!.Value)
            .Select(x => new { OrderItemId = x.Key, Quantity = x.Sum(y => y.Quantity) })
            .ToDictionaryAsync(x => x.OrderItemId, x => x.Quantity);

        foreach (var line in document.Lines)
        {
            if (!line.OrderItemId.HasValue)
                throw new FinanceConflictException("An order invoice line must reference its source order line.");
            var orderLine = order.OrderItems.SingleOrDefault(x => x.Id == line.OrderItemId.Value)
                ?? throw new FinanceConflictException("The source order line no longer belongs to this order.");
            if (alreadyIssued.GetValueOrDefault(orderLine.Id) + line.Quantity > orderLine.Quantity)
                throw new FinanceConflictException($"Issuing this document would exceed order line {orderLine.Id} quantity.");
        }
    }

    private async Task<string> AllocateNumberAsync(long businessUnitId, string documentType, int fiscalYear)
    {
        LegalDocumentCounter? counter;
        if (_context.Database.IsNpgsql())
        {
            await _context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "LegalDocumentCounters" ("BusinessUnitId", "DocumentType", "FiscalYear", "NextNumber")
                VALUES ({businessUnitId}, {documentType}, {fiscalYear}, 1)
                ON CONFLICT ("BusinessUnitId", "DocumentType", "FiscalYear") DO NOTHING
                """);
            counter = await _context.LegalDocumentCounters.FromSqlInterpolated($"""
                SELECT * FROM "LegalDocumentCounters"
                WHERE "BusinessUnitId" = {businessUnitId} AND "DocumentType" = {documentType} AND "FiscalYear" = {fiscalYear}
                FOR UPDATE
                """).SingleAsync();
        }
        else
        {
            counter = await _context.LegalDocumentCounters.SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.DocumentType == documentType && x.FiscalYear == fiscalYear);
            if (counter is null)
            {
                counter = new LegalDocumentCounter { BusinessUnitId = businessUnitId, DocumentType = documentType, FiscalYear = fiscalYear };
                _context.LegalDocumentCounters.Add(counter);
            }
        }
        var sequence = counter.NextNumber++;
        var prefix = documentType switch
        {
            ReceivableDocumentTypes.Invoice => "INV",
            ReceivableDocumentTypes.CreditNote => "CRN",
            ReceivableDocumentTypes.DebitNote => "DBN",
            "WriteOff" => "WOF",
            "Refund" => "RFD",
            _ => "RCT"
        };
        return $"{prefix}-{fiscalYear}-{sequence:D6}";
    }

    private async Task<long?> ResolveCommercialCaseIdAsync(Order order, long businessUnitId)
    {
        if (order.LeadId.HasValue)
            return await _context.Leads.Where(x => x.Id == order.LeadId && x.BusinessUnitId == businessUnitId)
                .Select(x => (long?)x.CommercialCaseId).SingleOrDefaultAsync();
        if (order.Rfqid.HasValue)
            return await _context.Rfqs.Where(x => x.Id == order.Rfqid && x.BusinessUnitId == businessUnitId)
                .Select(x => x.Lead == null ? null : (long?)x.Lead.CommercialCaseId).SingleOrDefaultAsync();
        return null;
    }

    private async Task<decimal> ActiveAllocatedAsync(long documentId, DateTime? before = null)
    {
        var query = _context.PaymentAllocations.Where(x => x.ReceivableDocumentId == documentId);
        if (before.HasValue)
            query = query.Where(x => x.Payment.PaymentDate < before.Value &&
                (!x.Payment.ReversedOn.HasValue || x.Payment.ReversedOn >= before.Value));
        else
            query = query.Where(x => x.Payment.Status == CustomerPaymentStatuses.Posted);
        return Round(await query.SumAsync(x => (decimal?)x.Amount) ?? 0m);
    }

    private async Task<decimal> DocumentOutstandingAsync(ReceivableDocument document, DateTime? before = null)
    {
        var credits = 0m;
        if (document.DocumentType == ReceivableDocumentTypes.Invoice)
        {
            var adjustments = _context.ReceivableDocuments.Where(x => x.BusinessUnitId == document.BusinessUnitId &&
                x.ParentDocumentId == document.Id && x.DocumentType == ReceivableDocumentTypes.CreditNote &&
                x.Status == ReceivableDocumentStatuses.Issued);
            if (before.HasValue) adjustments = adjustments.Where(x => x.IssuedOn < before.Value);
            credits = Round(await adjustments.SumAsync(x => (decimal?)x.TotalAmount) ?? 0m);
        }
        var writeOffs = _context.WriteOffAllocations.Where(x =>
            x.BusinessUnitId == document.BusinessUnitId && x.ReceivableDocumentId == document.Id &&
            (x.WriteOff.Status == FinanceExceptionStatuses.Posted ||
             x.WriteOff.Status == FinanceExceptionStatuses.Reversed));
        if (before.HasValue)
            writeOffs = writeOffs.Where(x => x.WriteOff.ApprovedOn < before.Value &&
                (!x.WriteOff.ReversedOn.HasValue || x.WriteOff.ReversedOn >= before.Value));
        else
            writeOffs = writeOffs.Where(x => x.WriteOff.Status == FinanceExceptionStatuses.Posted);
        var writtenOff = Round(await writeOffs.SumAsync(x => (decimal?)x.Amount) ?? 0m);
        return Round(document.TotalAmount - credits - await ActiveAllocatedAsync(document.Id, before) - writtenOff);
    }

    private async Task<decimal> ActiveRefundAmountAsync(long paymentId, bool includeReleased, long? excludingRefundId = null)
    {
        var query = _context.CustomerRefunds.Where(x => x.SourcePaymentId == paymentId &&
            (x.Status == FinanceExceptionStatuses.Approved ||
             (includeReleased && x.Status == FinanceExceptionStatuses.Released)));
        if (excludingRefundId.HasValue) query = query.Where(x => x.Id != excludingRefundId);
        return Round(await query.SumAsync(x => (decimal?)x.Amount) ?? 0m);
    }

    private async Task<decimal> ReleasedRefundAmountAsync(long paymentId)
        => Round(await _context.CustomerRefunds.Where(x => x.SourcePaymentId == paymentId &&
            x.Status == FinanceExceptionStatuses.Released).SumAsync(x => (decimal?)x.Amount) ?? 0m);

    private async Task<ReceivableDocumentDto> MapDocumentAsync(ReceivableDocument document)
    {
        var allocated = document.Status == ReceivableDocumentStatuses.Issued &&
                        document.DocumentType is ReceivableDocumentTypes.Invoice or ReceivableDocumentTypes.DebitNote
            ? await ActiveAllocatedAsync(document.Id)
            : 0m;
        var outstanding = document.Status != ReceivableDocumentStatuses.Issued
            ? document.TotalAmount
            : document.DocumentType == ReceivableDocumentTypes.CreditNote
                ? 0m
                : await DocumentOutstandingAsync(document);
        return new ReceivableDocumentDto(
            document.Id, document.CommercialCaseId, document.CustomerId, document.OrderId,
            document.ParentDocumentId, document.AdjustmentReasonCode, document.AdjustmentReason, document.CurrencyId,
            await CurrencyCodeAsync(document.CurrencyId), document.DocumentType, document.Status, document.DocumentNumber,
            document.DocumentDate, document.DueDate, document.IssuedOn, document.VoidedOn, document.VoidReason, document.VoidedBy, document.SubTotal,
            document.DiscountAmount, document.TaxAmount, document.TotalAmount, allocated,
            outstanding, document.Version,
            document.Lines.OrderBy(x => x.Id).Select(x => new ReceivableLineDto(
                x.Id, x.OrderItemId, x.ParentDocumentLineId, x.Description, x.Quantity, x.UnitPrice,
                x.DiscountAmount, x.TaxAmount, x.LineTotal)).ToList());
    }

    private async Task<CustomerPaymentDto> MapPaymentAsync(CustomerPayment payment)
    {
        var isPosted = payment.Status == CustomerPaymentStatuses.Posted;
        var allocated = isPosted
            ? Round(payment.Allocations.Sum(x => x.Amount))
            : 0m;
        var unavailableForRefund = isPosted ? await ActiveRefundAmountAsync(payment.Id, includeReleased: true) : 0m;
        return new CustomerPaymentDto(
            payment.Id, payment.CustomerId, payment.CommercialCaseId, payment.CurrencyId,
            await CurrencyCodeAsync(payment.CurrencyId), payment.ReceiptNumber, payment.Status, payment.PaymentDate, payment.Amount,
            allocated, isPosted ? Round(payment.Amount - allocated - unavailableForRefund) : 0m, payment.Version,
            payment.BankAccountId, payment.JournalEntryId, payment.ReversalJournalEntryId,
            payment.JournalEntryId.HasValue ? "Integrated" : "LegacyUnlinked");
    }

    private async Task<ReceivableWriteOffDto> MapWriteOffAsync(ReceivableWriteOff writeOff)
    {
        if (!_context.Entry(writeOff).Collection(x => x.Allocations).IsLoaded)
            await _context.Entry(writeOff).Collection(x => x.Allocations).LoadAsync();
        var documentIds = writeOff.Allocations.Select(x => x.ReceivableDocumentId).Distinct().ToArray();
        var numbers = await _context.ReceivableDocuments.Where(x => documentIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DocumentNumber ?? $"Document #{x.Id}");
        return new ReceivableWriteOffDto(
            writeOff.Id, writeOff.CustomerId, writeOff.CommercialCaseId, writeOff.CurrencyId,
            await CurrencyCodeAsync(writeOff.CurrencyId), writeOff.WriteOffNumber, writeOff.Status,
            writeOff.AccountingDate, writeOff.TotalAmount, writeOff.ReasonCode, writeOff.Reason,
            writeOff.EvidenceReference, writeOff.PostingStatus, writeOff.JournalReference,
            writeOff.Version, writeOff.CreatedBy, writeOff.CreatedOn, writeOff.ApprovedBy,
            writeOff.ApprovedOn, writeOff.CancelledBy, writeOff.CancelledOn, writeOff.CancellationReason,
            writeOff.ReversedBy, writeOff.ReversedOn, writeOff.ReversalReason,
            writeOff.ReversalEvidenceReference, writeOff.Allocations.OrderBy(x => x.ReceivableDocumentId)
                .Select(x => new WriteOffAllocationDto(x.Id, x.ReceivableDocumentId,
                    numbers.GetValueOrDefault(x.ReceivableDocumentId, $"Document #{x.ReceivableDocumentId}"),
                    x.Amount, x.BalanceBefore, x.BalanceAfter)).ToList());
    }

    private async Task<CustomerRefundDto> MapRefundAsync(CustomerRefund refund)
    {
        if (refund.SourcePayment is null)
            await _context.Entry(refund).Reference(x => x.SourcePayment).LoadAsync();
        var sourcePayment = refund.SourcePayment
            ?? throw new FinanceConflictException("The refund source receipt is unavailable.");
        return new CustomerRefundDto(
            refund.Id, refund.SourcePaymentId, sourcePayment.ReceiptNumber, refund.CustomerId,
            refund.CommercialCaseId, refund.CurrencyId, await CurrencyCodeAsync(refund.CurrencyId),
            refund.RefundNumber, refund.Status, refund.RequestedExecutionDate, refund.Amount,
            refund.Method, "Verified provider destination", refund.DestinationVerified, refund.ReasonCode,
            refund.Reason, refund.EvidenceReference, refund.PostingStatus, refund.JournalReference,
            refund.Version, refund.CreatedBy, refund.CreatedOn, refund.ApprovedBy, refund.ApprovedOn,
            refund.ReleasedBy, refund.ReleasedOn, refund.DisbursementUpdatedBy, refund.DisbursementUpdatedOn,
            refund.DisbursementFailureReason, refund.CancelledBy, refund.CancelledOn,
            refund.CancellationReason, refund.ReversedBy, refund.ReversedOn, refund.ReversalReason,
            refund.ReversalEvidenceReference, refund.BankAccountId, refund.JournalEntryId,
            refund.JournalEntryId.HasValue ? "Integrated" : "LegacyUnlinked");
    }

    private async Task<string?> CurrencyCodeAsync(long? currencyId)
        => currencyId.HasValue
            ? await _context.Currencies.Where(x => x.Id == currencyId.Value).Select(x => x.Code).SingleOrDefaultAsync()
            : null;

    private async Task<BankAccount> ResolveBankAccountAsync(long businessUnitId, long? requestedId)
    {
        var query = _context.BankAccounts.Where(x => x.BusinessUnitId == businessUnitId &&
            x.Status == BankAccountStatuses.Active);
        if (requestedId.HasValue)
            return await query.SingleOrDefaultAsync(x => x.Id == requestedId.Value)
                ?? throw new FinanceConflictException("The selected bank account is not active for this tenant.");
        var accounts = await query.OrderBy(x => x.Id).Take(2).ToListAsync();
        return accounts.Count == 1 ? accounts[0]
            : throw new FinanceConflictException("Select a governed bank account when the tenant does not have exactly one active account.");
    }

    private async Task<T> InSerializableTransactionAsync<T>(Func<Task<T>> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var strategy = _context.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                    var result = await action();
                    await transaction.CommitAsync();
                    return result;
                });
            }
            catch (Exception exception) when (attempt < 4 && IsRetryableFinanceRace(exception))
            {
                _context.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt));
            }
        }
    }

    private static bool IsRetryableFinanceRace(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres &&
                (postgres.SqlState == PostgresErrorCodes.SerializationFailure ||
                 postgres.SqlState == PostgresErrorCodes.DeadlockDetected ||
                 (postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  postgres.ConstraintName is "UX_ReceivableDocuments_BU_Idempotency" or
                      "UX_CustomerPayments_BU_Idempotency" or
                      "UX_ReceivableWriteOffs_BU_Idempotency" or
                      "UX_CustomerRefunds_BU_Idempotency")))
                return true;
        }
        return false;
    }

    private async Task AddAuditAsync(long businessUnitId, string type, long id, string action, string actor, object detail)
    {
        var detailJson = JsonSerializer.Serialize(detail);
        if (_context.Database.IsNpgsql())
            return;
        _context.CommercialFinanceAudits.Add(new CommercialFinanceAudit
        {
            BusinessUnitId = businessUnitId,
            AggregateType = type,
            AggregateId = id,
            Action = action,
            Actor = actor,
            OccurredOn = DateTime.UtcNow,
            DetailJson = detailJson
        });
    }

    private void AddOutbox(
        long businessUnitId, string aggregateType, long aggregateId, long aggregateVersion,
        string eventType, object payload)
    {
        var now = DateTime.UtcNow;
        _context.FinanceOutboxMessages.Add(new FinanceOutboxMessage
        {
            BusinessUnitId = businessUnitId,
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            AggregateVersion = aggregateVersion,
            EventType = eventType,
            Payload = JsonSerializer.Serialize(payload),
            SchemaVersion = 1,
            OccurredOn = now,
            AvailableOn = now
        });
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
            throw new ArgumentException("Idempotency-Key is required and must be 128 characters or fewer.");
    }

    private static string RequiredCode(string? value, string subject)
    {
        var result = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(result) || result.Length > 50)
            throw new ArgumentException($"A {subject} reason code up to 50 characters is required.");
        return result;
    }

    private static string RequiredReason(string? value, string subject)
    {
        var result = value?.Trim();
        if (string.IsNullOrWhiteSpace(result) || result.Length < 20 || result.Length > 500)
            throw new ArgumentException($"A {subject} reason between 20 and 500 characters is required.");
        return result;
    }

    private static string? OptionalEvidence(string? value)
    {
        var result = value?.Trim();
        if (result?.Length > 500) throw new ArgumentException("Evidence reference cannot exceed 500 characters.");
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static void EnsureSingleCustomerCurrencyCase(IReadOnlyList<ReceivableDocument> documents)
    {
        if (documents.Select(x => x.CustomerId).Distinct().Count() != 1 ||
            documents.Select(x => x.CurrencyId).Distinct().Count() != 1 ||
            documents.Select(x => x.CommercialCaseId).Distinct().Count() != 1)
            throw new FinanceConflictException("A write-off can only span one customer, currency, and commercial case.");
    }

    private static void EnsureReplay(string storedHash, string requestHash)
    {
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(storedHash), Encoding.ASCII.GetBytes(requestHash)))
            throw new FinanceConflictException("The idempotency key was already used with a different request.");
    }

    private static string Hash<T>(T value)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string DocumentEventType(string documentType, string action)
        => documentType switch
        {
            ReceivableDocumentTypes.CreditNote => AdjustmentEventType(documentType, action),
            ReceivableDocumentTypes.DebitNote => AdjustmentEventType(documentType, action),
            _ => $"finance.receivable.{action}"
        };

    private static string AdjustmentEventType(string documentType, string action)
        => $"finance.{(documentType == ReceivableDocumentTypes.CreditNote ? "credit-note" : "debit-note")}.{action}";

    private static string AgingBucket(int days) => days switch
    {
        0 => "Current",
        <= 30 => "1-30",
        <= 60 => "31-60",
        <= 90 => "61-90",
        _ => "90+"
    };
}
