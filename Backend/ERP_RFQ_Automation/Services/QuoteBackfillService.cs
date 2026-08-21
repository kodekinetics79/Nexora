using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
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

        // EVERYTHING that can refuse this import runs BEFORE the spine originates anything.
        // OriginateAsync SAVES a Lead and an RFQ, and the idempotency check above keys on the
        // Quote that does not exist yet, so a refusal thrown after it strands a BACKFILL lead and
        // RFQ in the very pipeline this feature exists to make honest — and every retry of the
        // corrected file strands another pair.
        var statusId = await LifecycleStatusCatalog.ResolveIdAsync(
            _db, businessUnitId, "Quote",
            string.IsNullOrWhiteSpace(request.StatusCode) ? "DRAFT" : request.StatusCode!.Trim().ToUpperInvariant(), ct);

        // R17: CreateQuoteAsync DERIVES every line's output tax from the tenant's current rate and
        // discards any amount handed to it. Resolved here so a historical tax figure can be checked
        // against that derivation BEFORE anything is written, instead of being folded into a header
        // total the create path then throws away — which re-taxed a quote issued under a different
        // VAT rate at today's rate, silently.
        var outputTaxRatePercent = await _db.ResolveOutputTaxRatePercentAsync(businessUnitId, ct);

        // A back-filled line discount is an AMOUNT off that line, so it maps to the tenant's FIXED
        // discount type. Resolved once, and only when a line actually carries one, so a tenant
        // importing undiscounted quotes is never blocked on setup it does not need.
        var fixedDiscountTypeId = request.Lines.Any(line => (line.Discount ?? 0m) != 0m)
            ? await ResolveFixedDiscountTypeIdAsync(businessUnitId, ct)
            : (long?)null;

        var items = new List<QuoteItemCreateRequestDTO>(request.Lines.Count);
        for (var index = 0; index < request.Lines.Count; index++)
        {
            var line = request.Lines[index];
            var label = string.IsNullOrWhiteSpace(line.CustomerLineRef)
                ? $"#{index + 1}"
                : line.CustomerLineRef!.Trim();

            var discount = line.Discount ?? 0m;
            var gross = decimal.Round(line.Quantity * line.UnitPrice, 2, MidpointRounding.AwayFromZero);
            if (discount < 0m || discount > gross)
                throw new InvalidOperationException(
                    $"Line {label} carries a discount of {discount:0.##} against a line value of {gross:0.##}. " +
                    "A back-filled discount is an amount off the line: it cannot be negative, and one larger " +
                    "than the line would be clamped, storing a figure the customer was never given.");

            var taxableBase = OutputTaxFormula.TaxableBase(gross, discount);
            if (line.TaxAmount is { } historicalTax)
            {
                var derived = OutputTaxFormula.Derive(
                    taxableBase, outputTaxRatePercent, QuoteLineTaxCategories.Standard);
                if (derived is null)
                    throw new InvalidOperationException(
                        $"Line {label} was quoted with tax of {historicalTax:0.##}, but this business unit has " +
                        "no output tax rate configured, so Nexora can derive no tax on it at all. Set the output " +
                        "tax rate in Commercial Policy settings before importing this quote.");
                if (Math.Abs(derived.Value - historicalTax) > 0.01m)
                    throw new InvalidOperationException(
                        $"Line {label} was quoted with tax of {historicalTax:0.##}; this tenant's current rate " +
                        $"({outputTaxRatePercent:0.##}%) derives {derived.Value:0.##} on the same line. Output tax " +
                        "is derived, never stored from the request (R17), so importing this line would re-tax it " +
                        "at today's rate and change what the customer was charged. Omit the line's tax to accept " +
                        "Nexora's figure — the stated quote total is then reported back as a mismatch rather " +
                        "than the difference disappearing.");
            }

            items.Add(new QuoteItemCreateRequestDTO
            {
                ProductId = line.ProductId,
                ItemDescription = line.Description,
                UnitOfMeasure = string.IsNullOrWhiteSpace(line.UnitOfMeasure) ? null : line.UnitOfMeasure!.Trim(),
                CustomerLineRef = string.IsNullOrWhiteSpace(line.CustomerLineRef) ? null : line.CustomerLineRef!.Trim(),
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                // The historical discount, expressed the only way the create path reads one.
                // Setting QuoteItem.Discount instead — which is what this did — dropped it:
                // CalculateQuoteTotals RECOMPUTES that field from these two and never reads the
                // value it was handed, so every historical line discount vanished on import.
                DiscountTypeId = discount != 0m ? fixedDiscountTypeId : null,
                DiscountValue = discount != 0m ? discount : null,
                // Ignored by CreateQuoteAsync like every other line total; stated as the ex-tax net
                // so the request never carries a figure that contradicts what will be stored.
                TotalAmount = taxableBase,
            });
        }

        var rfq = await _spine.OriginateAsync(
            businessUnitId, request.CustomerId, request.ContactId, request.QuoteDate, actor, reference, ct);

        var created = await _quotes.CreateQuoteAsync(new QuoteCreateRequestDTO
        {
            RfqId = rfq.Id,
            CustomerId = request.CustomerId,
            BusinessUnitId = businessUnitId,
            QuoteDate = request.QuoteDate,
            ValidUntil = request.ValidUntil,
            StatusId = statusId,
            CurrencyId = request.CurrencyId,
            // INERT, and left here only so the next reader sees it is: CreateQuoteAsync never
            // assigns request.TotalAmount to the entity, and CalculateQuoteTotals overwrites the
            // header total unconditionally. Treating this as "stored" is exactly what let an
            // import answer 201 with the tenant's number while the database held another.
            TotalAmount = request.TotalAmount,
            HeaderRemarks = request.HeaderRemarks,
            ExternalQuoteReference = reference,
            Origin = QuoteOrigin.Backfill,
            CreatedBy = actor,
            QuoteItems = items,
        });

        // Read back off the created quote, not off a local variable. This is the whole defect: the
        // caller is told what the database holds, because that is what the pipeline, the quote list,
        // the view screen and any re-issued PDF will show.
        var stored = created.TotalAmount ?? 0m;

        // And the warning is computed against that same stored figure. It used to be computed
        // against a local sum of the request's own lines, so the one case it exists to report --
        // Nexora holding a different total from the one the tenant typed -- was the single case it
        // could not see.
        string? mismatch = null;
        if (request.TotalAmount is { } supplied && Math.Abs(supplied - stored) > 0.01m)
            mismatch = $"Nexora recomputed this quote at {stored:0.##}; the customer holds {supplied:0.##}. " +
                       "The recomputed figure is the one stored, and the one every screen and any re-issued " +
                       "PDF will show.";

        _log.LogInformation(
            "Back-filled quote {Reference} as {QuoteNo} on serial {Serial} at {Total}.",
            reference, created.QuoteNo, rfq.NexoraSerial, stored);
        if (mismatch is not null)
            _log.LogWarning("Back-filled quote {Reference}: {Mismatch}", reference, mismatch);

        return new QuoteBackfillResult
        {
            QuoteId = created.Id, QuoteNo = created.QuoteNo, ExternalQuoteReference = reference,
            NexoraSerial = rfq.NexoraSerial ?? string.Empty, RfqId = rfq.Id, LeadId = rfq.LeadId ?? 0,
            TotalAmount = stored, AlreadyPresent = false, TotalMismatchWarning = mismatch,
        };
    }

    /// <summary>
    /// The tenant's FIXED discount type, or a refusal. Fail-closed on purpose: the alternative to
    /// finding a row is dropping the discount, and dropping it re-grosses the line to a price the
    /// customer never agreed to. Matched the way <c>LifecycleStatusCatalog</c> matches setup rows —
    /// type folded for case and spacing, code folded for case — so a tenant whose row reads
    /// "Discount Type" is recognised rather than declared missing.
    /// </summary>
    private async Task<long> ResolveFixedDiscountTypeIdAsync(long businessUnitId, CancellationToken ct)
        => await _db.SetupMasters.AsNoTracking()
               .Where(row => row.BusinessUnitId == businessUnitId && row.IsActive != false)
               .Where(row => row.SetupType.ToLower().Replace(" ", "") == "discounttype")
               .Where(row => row.SetupCode != null && row.SetupCode.ToUpper() == "FIXED")
               .OrderBy(row => row.SetupId)
               .Select(row => (long?)row.SetupId)
               .FirstOrDefaultAsync(ct)
           ?? throw new InvalidOperationException(
               "A quote being back-filled has a line discount, but this business unit has no active FIXED " +
               "discount type to record it against. Without one the discount could only be dropped and the " +
               "line re-grossed. Seed the tenant baseline (it creates PERCENTAGE and FIXED) and import again.");
}
