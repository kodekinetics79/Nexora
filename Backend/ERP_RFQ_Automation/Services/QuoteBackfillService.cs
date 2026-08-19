using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Services;

/// <summary>
/// Carries a quote that predates Nexora into Nexora, so it can be monitored beside the quotes
/// Nexora produced.
///
/// This service ORIGINATES the commercial spine and then hands over to the ordinary create path.
/// It deliberately does not mint quote numbers, validate line financials or stamp commercial
/// identity itself: <see cref="QuoteService.CreateQuoteAsync"/> already does all of that, and a
/// second implementation would be a second set of rules to keep in step. The only thing unique
/// to a back-fill is where the RFQ came from.
/// </summary>
public sealed class QuoteBackfillService
{
    private readonly ErpRfqAutomationContext _db;
    private readonly QuoteBackfillSpine _spine;
    private readonly IQuoteService _quotes;
    private readonly ILogger<QuoteBackfillService> _log;

    public QuoteBackfillService(
        ErpRfqAutomationContext db, QuoteBackfillSpine spine,
        IQuoteService quotes, ILogger<QuoteBackfillService> log)
    {
        _db = db; _spine = spine; _quotes = quotes; _log = log;
    }

    public async Task<QuoteBackfillResult> BackfillAsync(
        QuoteBackfillRequest request, long businessUnitId, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reference = request.ExternalQuoteReference?.Trim();
        if (string.IsNullOrWhiteSpace(reference))
            throw new ArgumentException(
                "The customer's own quote number is required: it is how this quote is recognised and " +
                "how a repeated import is detected.", nameof(request));
        if (request.Lines.Count == 0)
            throw new ArgumentException("A quote with no lines cannot be back-filled.", nameof(request));

        // Idempotent by the customer's reference. Re-running an import — the normal way a bulk
        // file gets fixed and uploaded again — must not create the quote twice.
        var existing = await _db.Quotes.AsNoTracking().FirstOrDefaultAsync(
            q => q.BusinessUnitId == businessUnitId && q.ExternalQuoteReference == reference, ct);
        if (existing is not null)
        {
            _log.LogInformation(
                "Quote {Reference} is already present as {QuoteNo}; nothing was written.", reference, existing.QuoteNo);
            return new QuoteBackfillResult
            {
                QuoteId = existing.Id, QuoteNo = existing.QuoteNo, ExternalQuoteReference = reference,
                NexoraSerial = existing.NexoraSerial ?? string.Empty, RfqId = existing.Rfqid ?? 0,
                TotalAmount = existing.TotalAmount ?? 0m, AlreadyPresent = true,
            };
        }

        var customerExists = await _db.Customers.AsNoTracking()
            .AnyAsync(c => c.Id == request.CustomerId && c.Buid == businessUnitId, ct);
        if (!customerExists)
            throw new InvalidOperationException("The selected customer was not found in this tenant.");

        var rfq = await _spine.OriginateAsync(
            businessUnitId, request.CustomerId, request.ContactId, request.QuoteDate, actor, reference, ct);

        var statusId = await LifecycleStatusCatalog.ResolveIdAsync(
            _db, businessUnitId, "Quote",
            string.IsNullOrWhiteSpace(request.StatusCode) ? "DRAFT" : request.StatusCode!.Trim().ToUpperInvariant(), ct);

        var lineTotal = 0m;
        var items = new List<QuoteItemCreateRequestDTO>(request.Lines.Count);
        foreach (var line in request.Lines)
        {
            var total = (line.Quantity * line.UnitPrice) + (line.TaxAmount ?? 0m) - (line.Discount ?? 0m);
            lineTotal += total;
            items.Add(new QuoteItemCreateRequestDTO
            {
                ProductId = line.ProductId,
                ItemDescription = line.Description,
                UnitOfMeasure = string.IsNullOrWhiteSpace(line.UnitOfMeasure) ? null : line.UnitOfMeasure!.Trim(),
                CustomerLineRef = string.IsNullOrWhiteSpace(line.CustomerLineRef) ? null : line.CustomerLineRef!.Trim(),
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                Discount = line.Discount,
                TotalAmount = total,
            });
        }

        // The total the customer holds on paper wins over the total the lines imply. If they
        // disagree the difference is REPORTED, never quietly corrected: silently restating what a
        // tenant promised a customer is the one outcome worse than importing nothing.
        var stored = request.TotalAmount ?? lineTotal;
        string? mismatch = null;
        if (request.TotalAmount is { } supplied && Math.Abs(supplied - lineTotal) > 0.01m)
            mismatch = $"The quoted total {supplied:0.##} differs from the sum of its lines {lineTotal:0.##}. " +
                       "The quoted total was kept, because that is the figure the customer was given.";

        var created = await _quotes.CreateQuoteAsync(new QuoteCreateRequestDTO
        {
            RfqId = rfq.Id,
            CustomerId = request.CustomerId,
            BusinessUnitId = businessUnitId,
            QuoteDate = request.QuoteDate,
            ValidUntil = request.ValidUntil,
            StatusId = statusId,
            CurrencyId = request.CurrencyId,
            TotalAmount = stored,
            HeaderRemarks = request.HeaderRemarks,
            ExternalQuoteReference = reference,
            Origin = QuoteOrigin.Backfill,
            CreatedBy = actor,
            QuoteItems = items,
        });

        _log.LogInformation(
            "Back-filled quote {Reference} as {QuoteNo} on serial {Serial}.",
            reference, created.QuoteNo, rfq.NexoraSerial);

        return new QuoteBackfillResult
        {
            QuoteId = created.Id, QuoteNo = created.QuoteNo, ExternalQuoteReference = reference,
            NexoraSerial = rfq.NexoraSerial ?? string.Empty, RfqId = rfq.Id, LeadId = rfq.LeadId ?? 0,
            TotalAmount = stored, AlreadyPresent = false, TotalMismatchWarning = mismatch,
        };
    }
}
