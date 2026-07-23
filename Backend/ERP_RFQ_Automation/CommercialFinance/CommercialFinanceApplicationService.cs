using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.CommercialFinance;

public interface ICommercialFinanceApplicationService
{
    Task<ReceivableDocumentDto> CreateInvoiceAsync(long businessUnitId, long orderId, string idempotencyKey, CreateInvoiceRequest request, string actor);
    Task<ReceivableDocumentDto> IssueAsync(long businessUnitId, long documentId, IssueDocumentRequest request, string actor);
    Task<ReceivableDocumentDto> CancelAsync(long businessUnitId, long documentId, CancelDocumentRequest request, string actor);
    Task<ReceivableDocumentDto?> GetDocumentAsync(long businessUnitId, long documentId);
    Task<IReadOnlyList<ReceivableDocumentDto>> GetDocumentsAsync(long businessUnitId, long? customerId, string? status);
    Task<CustomerPaymentDto> PostPaymentAsync(long businessUnitId, string idempotencyKey, PostPaymentRequest request, string actor);
    Task<IReadOnlyList<CustomerPaymentDto>> GetPaymentsAsync(long businessUnitId, long? customerId, string? status);
    Task<CustomerPaymentDto> ReversePaymentAsync(long businessUnitId, long paymentId, ReversePaymentRequest request, string actor);
    Task<IReadOnlyList<ArOpenItemDto>> GetOpenItemsAsync(long businessUnitId, DateTime? asOf);
}

public sealed class CommercialFinanceApplicationService(ErpRfqAutomationContext context)
    : ICommercialFinanceApplicationService
{
    private readonly ErpRfqAutomationContext _context = context;

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
            AddAudit(businessUnitId, "ReceivableDocument", document.Id, "DraftCreated", actor, new { orderId });
            await _context.SaveChangesAsync();
            return await MapDocumentAsync(document);
        });
    }

    public async Task<ReceivableDocumentDto> IssueAsync(
        long businessUnitId, long documentId, IssueDocumentRequest request, string actor)
    {
        return await InSerializableTransactionAsync(async () =>
        {
            var document = await LockDocumentAsync(documentId, businessUnitId);
            if (document.Version != request.ExpectedVersion)
                throw new FinanceConflictException("The document changed; reload it before issuing.");
            if (document.Status == ReceivableDocumentStatuses.Issued)
                return await MapDocumentAsync(document);
            if (document.Status != ReceivableDocumentStatuses.Draft)
                throw new FinanceConflictException("Only draft documents can be issued.");
            if (document.Lines.Count == 0 || document.TotalAmount <= 0)
                throw new FinanceConflictException("A document must have positive reconciled lines before issue.");
            EnsureDocumentReconciles(document);
            await EnsureIssueQuantitiesAsync(document, businessUnitId);

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
                AddAudit(businessUnitId, "ReceivableDocument", document.Id, "Issued", actor, new { number });
            await _context.SaveChangesAsync();
            if (databaseAllocatesNumber)
                await _context.Entry(document).ReloadAsync();
            return await MapDocumentAsync(document);
        });
    }

    public async Task<ReceivableDocumentDto> CancelAsync(
        long businessUnitId, long documentId, CancelDocumentRequest request, string actor)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new ArgumentException("A cancellation reason up to 500 characters is required.");

        return await InSerializableTransactionAsync(async () =>
        {
            var document = await LockDocumentAsync(documentId, businessUnitId);
            if (document.Version != request.ExpectedVersion)
                throw new FinanceConflictException("The document changed; reload it before cancelling.");
            if (document.Status == ReceivableDocumentStatuses.Cancelled)
            {
                if (!string.Equals(document.VoidReason, reason, StringComparison.Ordinal))
                    throw new FinanceConflictException("The document was already cancelled with a different reason.");
                return await MapDocumentAsync(document);
            }
            if (document.Status != ReceivableDocumentStatuses.Draft)
                throw new FinanceConflictException("Only draft documents can be cancelled.");

            var databaseWritesAudit = _context.Database.IsNpgsql();
            document.Status = ReceivableDocumentStatuses.Cancelled;
            document.VoidedOn = DateTime.UtcNow;
            document.VoidReason = reason;
            document.VoidedBy = actor;
            document.Version++;
            if (!databaseWritesAudit)
                AddAudit(businessUnitId, "ReceivableDocument", document.Id, "DraftCancelled", actor,
                    new { Reason = reason });
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
            .Select(x => new PaymentAllocationRequest(x.ReceivableDocumentId, Round(x.Amount))).ToList();
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

            var documents = new List<ReceivableDocument>();
            foreach (var allocation in normalizedAllocations)
            {
                var document = await LockDocumentAsync(allocation.ReceivableDocumentId, businessUnitId);
                if (document.DocumentType != ReceivableDocumentTypes.Invoice || document.Status != ReceivableDocumentStatuses.Issued)
                    throw new FinanceConflictException("Payments can only be allocated to issued invoices.");
                if (document.CustomerId != request.CustomerId || document.CurrencyId != request.CurrencyId)
                    throw new FinanceConflictException("Payment and invoice customer/currency must match.");
                var allocated = await ActiveAllocatedAsync(document.Id);
                if (allocated + allocation.Amount > document.TotalAmount)
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
            AddAudit(businessUnitId, "CustomerPayment", payment.Id, "Posted", actor, new { receipt });
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
            var payment = await _context.CustomerPayments.Include(x => x.Allocations)
                .FirstOrDefaultAsync(x => x.Id == paymentId && x.BusinessUnitId == businessUnitId)
                ?? throw new KeyNotFoundException("Payment not found.");
            if (payment.Status == CustomerPaymentStatuses.Reversed) return await MapPaymentAsync(payment);
            if (payment.Version != request.ExpectedVersion)
                throw new FinanceConflictException("The payment changed; reload it before reversing.");
            payment.Status = CustomerPaymentStatuses.Reversed;
            payment.ReversedOn = DateTime.UtcNow;
            payment.ReversalReason = request.Reason.Trim();
            payment.Version++;
            AddAudit(businessUnitId, "CustomerPayment", payment.Id, "Reversed", actor, new { request.Reason });
            await _context.SaveChangesAsync();
            return await MapPaymentAsync(payment);
        });
    }

    public async Task<IReadOnlyList<ArOpenItemDto>> GetOpenItemsAsync(long businessUnitId, DateTime? asOf)
    {
        var effectiveDate = (asOf ?? DateTime.UtcNow).Date;
        var documents = await _context.ReceivableDocuments
            .Where(x => x.BusinessUnitId == businessUnitId &&
                        x.DocumentType == ReceivableDocumentTypes.Invoice &&
                        x.Status == ReceivableDocumentStatuses.Issued &&
                        x.DocumentDate <= effectiveDate)
            .OrderBy(x => x.DueDate)
            .ToListAsync();
        var result = new List<ArOpenItemDto>();
        foreach (var document in documents)
        {
            var allocated = await ActiveAllocatedAsync(document.Id, effectiveDate.AddDays(1));
            var outstanding = Round(document.TotalAmount - allocated);
            if (outstanding <= 0) continue;
            var days = Math.Max(0, (effectiveDate - document.DueDate.Date).Days);
            result.Add(new ArOpenItemDto(
                document.Id, document.DocumentNumber!, document.CustomerId, document.CommercialCaseId,
                document.CurrencyId, await CurrencyCodeAsync(document.CurrencyId), document.DocumentDate, document.DueDate, document.TotalAmount,
                outstanding, days, AgingBucket(days)));
        }
        return result;
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

    private async Task<ReceivableDocumentDto> MapDocumentAsync(ReceivableDocument document)
    {
        var allocated = document.Status == ReceivableDocumentStatuses.Issued
            ? await ActiveAllocatedAsync(document.Id)
            : 0m;
        return new ReceivableDocumentDto(
            document.Id, document.CommercialCaseId, document.CustomerId, document.OrderId,
            document.CurrencyId, await CurrencyCodeAsync(document.CurrencyId), document.DocumentType, document.Status, document.DocumentNumber,
            document.DocumentDate, document.DueDate, document.IssuedOn, document.VoidedOn, document.VoidReason, document.VoidedBy, document.SubTotal,
            document.DiscountAmount, document.TaxAmount, document.TotalAmount, allocated,
            Round(document.TotalAmount - allocated), document.Version,
            document.Lines.OrderBy(x => x.Id).Select(x => new ReceivableLineDto(
                x.Id, x.OrderItemId, x.Description, x.Quantity, x.UnitPrice,
                x.DiscountAmount, x.TaxAmount, x.LineTotal)).ToList());
    }

    private async Task<CustomerPaymentDto> MapPaymentAsync(CustomerPayment payment)
    {
        var isPosted = payment.Status == CustomerPaymentStatuses.Posted;
        var allocated = isPosted
            ? Round(payment.Allocations.Sum(x => x.Amount))
            : 0m;
        return new CustomerPaymentDto(
            payment.Id, payment.CustomerId, payment.CommercialCaseId, payment.CurrencyId,
            await CurrencyCodeAsync(payment.CurrencyId), payment.ReceiptNumber, payment.Status, payment.PaymentDate, payment.Amount,
            allocated, isPosted ? Round(payment.Amount - allocated) : 0m, payment.Version);
    }

    private async Task<string?> CurrencyCodeAsync(long? currencyId)
        => currencyId.HasValue
            ? await _context.Currencies.Where(x => x.Id == currencyId.Value).Select(x => x.Code).SingleOrDefaultAsync()
            : null;

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
                      "UX_CustomerPayments_BU_Idempotency")))
                return true;
        }
        return false;
    }

    private void AddAudit(long businessUnitId, string type, long id, string action, string actor, object detail)
        => _context.CommercialFinanceAudits.Add(new CommercialFinanceAudit
        {
            BusinessUnitId = businessUnitId,
            AggregateType = type,
            AggregateId = id,
            Action = action,
            Actor = actor,
            OccurredOn = DateTime.UtcNow,
            DetailJson = JsonSerializer.Serialize(detail)
        });

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
            throw new ArgumentException("Idempotency-Key is required and must be 128 characters or fewer.");
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

    private static string AgingBucket(int days) => days switch
    {
        0 => "Current",
        <= 30 => "1-30",
        <= 60 => "31-60",
        <= 90 => "61-90",
        _ => "90+"
    };
}
