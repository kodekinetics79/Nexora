using System.IO;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System;
using System.Data;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.QuoteDelivery;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.CommercialLearning;

namespace ERP_RFQ_Automation.Services
{
    public interface IQuoteService
    {
        Task<byte[]> GenerateQuotePdfAsync(long quoteId, long businessUnitId);

        /// <summary>
        /// Transactionally queues durable PDF delivery — unless a line is priced
        /// below its pricing-engine floor (WP-B3), in which case NOTHING is sent:
        /// the send is parked as a pending approve_below_floor_quote approval and
        /// the result says so. options.BypassFloorHold (approved-hold execution
        /// only) skips the check.
        /// </summary>
        Task<QuoteSendResult> SendQuoteEmailAsync(long quoteId, long businessUnitId, string recipientEmail, string? customSubject = null, string? customBody = null, QuoteSendOptions? options = null);
        Task FinalizeQuoteDeliveryAsync(long quoteId, long businessUnitId, CancellationToken ct = default);
        Task<QuoteResponseDTO> CreateQuoteAsync(QuoteCreateRequestDTO request);
        Task<QuoteResponseDTO> PrepareDraftFromRfqAsync(long rfqId, long businessUnitId, string actor, CancellationToken ct = default);
        Task<QuoteResponseDTO> UpdateQuoteAsync(long id, QuoteUpdateRequestDTO request);
        Task<QuoteResponseDTO> TransitionStatusAsync(long id, string statusCode, string modifiedBy);
        Task<QuoteResponseDTO> GetQuoteAsync(long id);

        /// <summary>
        /// Revisions-lite (WP-B4): clones a non-DRAFT quote (+items) as a new DRAFT
        /// revision (RevisionNo+1, linked back via RevisionOfQuoteId). Throws
        /// InvalidOperationException (→ 409) when the quote is a draft, already
        /// superseded, or its chain is locked by a recorded outcome.
        /// </summary>
        Task<QuoteResponseDTO> ReviseQuoteAsync(long quoteId, long businessUnitId, string actor);

        /// <summary>Revision-chain facts for one quote (chip + Revise button state).</summary>
        Task<QuoteRevisionInfoDTO> GetRevisionInfoAsync(long quoteId, long businessUnitId);
        Task ResolveRevisionImpactAsync(long quoteId, long businessUnitId, string actor,
            string idempotencyKey, CancellationToken ct = default);
    }

    public class QuoteService : IQuoteService
    {
        private readonly ErpRfqAutomationContext _context;
        private readonly IEmailService _emailService;
        private readonly IQuoteConfigurationRepository _quoteConfigRepository;
        private readonly ERP_RFQ_Automation.Intelligence.Pricing.IBelowFloorGuard? _belowFloorGuard;
        private readonly ILifecycleApplicationService? _lifecycle;
        private readonly ISalesApplicationService? _sales;
        private readonly ICommercialLineResolutionApplicationService? _lineResolution;
        private readonly CommercialLearningService? _commercialLearning;

        // Optional collaborators preserve existing direct constructions used by focused
        // tests; production DI supplies the lifecycle and sales services.
        public QuoteService(
            ErpRfqAutomationContext context,
            IEmailService emailService,
            IQuoteConfigurationRepository quoteConfigRepository,
            ERP_RFQ_Automation.Intelligence.Pricing.IBelowFloorGuard? belowFloorGuard = null,
            ILifecycleApplicationService? lifecycle = null,
            ISalesApplicationService? sales = null,
            ICommercialLineResolutionApplicationService? lineResolution = null,
            CommercialLearningService? commercialLearning = null)
        {
            _context = context;
            _emailService = emailService;
            _quoteConfigRepository = quoteConfigRepository;
            _belowFloorGuard = belowFloorGuard;
            _lifecycle = lifecycle;
            _sales = sales;
            _lineResolution = lineResolution;
            _commercialLearning = commercialLearning;
        }

        // Legacy QuoteStatus id map, used ONLY when no matching SetupMaster row is
        // configured (older tenants). All status resolution now goes through
        // ResolveQuoteStatusIdAsync (SetupType "QuoteStatus" + SetupCode) — the
        // magic numbers below are documented fallbacks, not the primary path.
        private static readonly Dictionary<string, long> LegacyQuoteStatusIds = new(StringComparer.OrdinalIgnoreCase)
        {
            ["DRAFT"] = 42,
            ["SENT"] = 43,
            ["ACCEPTED"] = 44,
            ["REJECTED"] = 45
        };

        /// <summary>
        /// Resolves a QuoteStatus id by SetupMaster code — BU-scoped row first, then
        /// any-BU row, then the documented legacy id map. Returns null when the code
        /// is unknown everywhere (e.g. EXPIRED before it has been seeded).
        /// </summary>
        private async Task<long?> ResolveQuoteStatusIdAsync(string statusCode, long? businessUnitId)
        {
            var code = statusCode.ToUpperInvariant();

            SetupMaster? row = null;
            if (businessUnitId.HasValue)
            {
                row = await _context.SetupMasters.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.SetupType == "QuoteStatus"
                        && s.SetupCode == code && s.BusinessUnitId == businessUnitId.Value);
            }
            row ??= await _context.SetupMasters.AsNoTracking()
                .FirstOrDefaultAsync(s => s.SetupType == "QuoteStatus" && s.SetupCode == code);
            if (row != null) return row.SetupId;

            return LegacyQuoteStatusIds.TryGetValue(code, out var legacyId) ? legacyId : null;
        }

        public async Task<QuoteResponseDTO> CreateQuoteAsync(QuoteCreateRequestDTO request)
        {
            var quoteNo = request.QuoteNo;
            if (string.IsNullOrWhiteSpace(quoteNo))
            {
                quoteNo = await GenerateNextQuoteNumber();
            }

            // FIN-12: reject non-positive quantity/price and negative tax up front.
            foreach (var itemDto in request.QuoteItems)
                ValidateQuoteItemFinancials(itemDto.Quantity, itemDto.UnitPrice, itemDto.TaxAmount);

            var quote = new Quote
            {
                QuoteNo = quoteNo,
                Rfqid = request.RfqId,
                CustomerId = request.CustomerId,
                BusinessUnitId = request.BusinessUnitId,
                QuoteDate = request.QuoteDate,
                ValidUntil = request.ValidUntil,
                StatusId = (request.StatusId > 0)
                    ? request.StatusId
                    : await ResolveQuoteStatusIdAsync("DRAFT", request.BusinessUnitId), // default: DRAFT (resolved via SetupMaster; legacy id 42 fallback)
                CurrencyId = request.CurrencyId,
                HeaderRemarks = request.HeaderRemarks,
                CreatedBy = request.CreatedBy,
                CreatedDate = DateTime.UtcNow,
                DiscountTypeId = request.DiscountTypeId,
                DiscountValue = request.DiscountValue,
                QuoteItems = request.QuoteItems.Select(i => new QuoteItem
                {
                    RfqitemId = i.RfqItemId,
                    ProductId = i.ProductId,
                    ItemDescription = i.ItemDescription,
                    Quantity = i.Quantity,
                    UnitOfMeasure = i.UnitOfMeasure,
                    CustomerLineRef = i.CustomerLineRef,
                    UnitPrice = i.UnitPrice,
                    // TotalAmount calculated later
                    DiscountTypeId = i.DiscountTypeId,
                    DiscountValue = i.DiscountValue, // Using DiscountValue column for input
                    TaxAmount = i.TaxAmount,
                    DeliveryLeadTime = i.DeliveryLeadTime,
                    CreatedBy = request.CreatedBy,
                    CreatedDate = DateTime.UtcNow
                }).ToList()
            };

            await CalculateQuoteTotals(quote);

            _context.Quotes.Add(quote);
            await _context.SaveChangesAsync();

            return await GetQuoteByIdAsync(quote.Id);
        }

        public async Task<QuoteResponseDTO> PrepareDraftFromRfqAsync(
            long rfqId, long businessUnitId, string actor, CancellationToken ct = default)
        {
            if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
            if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Authenticated actor is required.", nameof(actor));

            var leadId = await _context.Rfqs.AsNoTracking()
                .Where(item => item.Id == rfqId && item.BusinessUnitId == businessUnitId)
                .Select(item => item.LeadId).SingleOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException("The RFQ was not found in this tenant.");
            if (_lineResolution is not null)
            {
                var resourceLimit = await _context.Set<LeadLineCommercialResolution>().AsNoTracking()
                    .Where(x => x.BusinessUnitId == businessUnitId && x.LeadId == leadId)
                    .Select(x => (int?)x.ResourceLimit).MaxAsync(ct) ?? 10;
                if (resourceLimit is not (10 or 20 or 50)) resourceLimit = 10;
                await _lineResolution.ResolveLeadAsync(businessUnitId, leadId, resourceLimit, ct, forceRefresh: true);
                await _lineResolution.LinkRfqAsync(businessUnitId, leadId, rfqId, ct);
            }
            if (_commercialLearning is not null)
            {
                var intelligence = await _commercialLearning.GetRfqIntelligenceAsync(businessUnitId, rfqId, ct);
                if (intelligence.CommercialDecision != "VIABLE_READY")
                    throw new InvalidOperationException(
                        $"Customer Quote preparation is blocked: {intelligence.NextBestAction.Explanation}");
            }

            var strategy = _context.Database.CreateExecutionStrategy();
            var quoteId = await strategy.ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();
                await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var rfq = await _context.Rfqs
                    .Include(item => item.Lead)
                    .Include(item => item.Rfqitems)
                    .SingleOrDefaultAsync(item => item.Id == rfqId && item.BusinessUnitId == businessUnitId, ct)
                    ?? throw new KeyNotFoundException("The RFQ was not found in this tenant.");

                if (rfq.Lead == null) throw new InvalidOperationException("The RFQ is not linked to its canonical Lead.");
                rfq.InheritCommercialIdentity(rfq.Lead);
                if (!rfq.CustomerId.HasValue) throw new InvalidOperationException("Resolve the RFQ customer before preparing a Quote Draft.");
                if (rfq.Rfqitems.Count == 0) throw new InvalidOperationException("Add at least one verified RFQ line before preparing a Quote Draft.");

                var invalidLines = rfq.Rfqitems
                    .Where(item => item.Quantity <= 0
                        || string.IsNullOrWhiteSpace(item.UnitOfMeasure)
                        || string.IsNullOrWhiteSpace(item.ItemMaterialCode)
                           && string.IsNullOrWhiteSpace(item.ManufacturerPartNumber)
                           && string.IsNullOrWhiteSpace(item.ProductShortDescription))
                    .Select(item => string.IsNullOrWhiteSpace(item.LineItemNo) ? $"line {item.Id}" : $"line {item.LineItemNo}")
                    .ToArray();
                if (invalidLines.Length > 0)
                    throw new InvalidOperationException($"Review required request data for {string.Join(", ", invalidLines)} before preparing a Quote Draft.");

                var existing = await _context.Quotes
                    .Include(item => item.Status)
                    .SingleOrDefaultAsync(item => item.Rfqid == rfqId && item.BusinessUnitId == businessUnitId, ct);
                if (existing != null)
                {
                    var existingCode = LifecyclePolicy.Canonicalize("Quote", existing.Status?.SetupCode, existing.Status?.SetupValue);
                    if (existingCode != "DRAFT")
                        throw new InvalidOperationException("This RFQ already has a customer-issued Quote. Use the governed Quote revision action.");
                    existing.InheritCommercialIdentity(rfq);
                    await _context.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return existing.Id;
                }

                var now = DateTime.UtcNow;
                var quote = new Quote
                {
                    QuoteNo = await GenerateNextQuoteNumber(),
                    Rfqid = rfq.Id,
                    CustomerId = rfq.CustomerId,
                    BusinessUnitId = businessUnitId,
                    QuoteDate = now,
                    ValidUntil = null,
                    StatusId = await ResolveQuoteStatusIdAsync("DRAFT", businessUnitId),
                    CurrencyId = null,
                    TotalAmount = 0m,
                    HeaderRemarks = "Commercial Review Required: pricing, inventory, lead time, tax, freight and validity remain pending.",
                    CreatedBy = actor.Trim(),
                    CreatedDate = now,
                    QuoteItems = rfq.Rfqitems.OrderBy(item => item.Id).Select(item => new QuoteItem
                    {
                        RfqitemId = item.Id,
                        ProductId = item.ProductId,
                        ItemDescription = item.ProductShortDescription ?? item.ProductShortName ?? item.ItemText ?? item.ItemMaterialCode,
                        Quantity = item.Quantity,
                        // The draft gate above refuses a blank UnitOfMeasure — keep what it
                        // validated instead of throwing it away, and carry the buyer's own
                        // line number so the printed quote can echo their reference back.
                        UnitOfMeasure = item.UnitOfMeasure,
                        CustomerLineRef = item.LineItemNo,
                        UnitPrice = 0m,
                        TotalAmount = 0m,
                        TaxAmount = null,
                        DeliveryLeadTime = null,
                        CreatedBy = actor.Trim(),
                        CreatedDate = now
                    }).ToList()
                };
                quote.InheritCommercialIdentity(rfq);
                _context.Quotes.Add(quote);
                await _context.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return quote.Id;
            });

            return await GetQuoteByIdAsync(quoteId);
        }

        private async Task<string> GenerateNextQuoteNumber()
        {
            // Format: QT-MMYY-0001
            var now = DateTime.UtcNow;
            var prefix = $"QT-{now:MM}{now:yy}-";

            // Get the last quote number with this prefix
            var lastQuote = await _context.Quotes
                .Where(q => q.QuoteNo.StartsWith(prefix))
                .OrderByDescending(q => q.QuoteNo)
                .FirstOrDefaultAsync();

            int nextSequence = 1;
            if (lastQuote != null)
            {
                // Extract the sequence part
                var parts = lastQuote.QuoteNo.Split('-');
                if (parts.Length >= 3 && int.TryParse(parts[2], out int lastSequence))
                {
                    nextSequence = lastSequence + 1;
                }
            }

            return $"{prefix}{nextSequence:D4}";
        }

        public async Task<QuoteResponseDTO> UpdateQuoteAsync(long id, QuoteUpdateRequestDTO request)
        {
            var quote = await _context.Quotes
                .Include(q => q.QuoteItems)
                .FirstOrDefaultAsync(q => q.Id == id);

            if (quote == null) throw new KeyNotFoundException($"Quote with ID {id} not found.");

            // FIN-05: a quote's financial content may only be modified while it is still in DRAFT.
            // Once it has been SENT / ACCEPTED / ORDERED, the customer already holds a PDF with
            // fixed totals; silently recalculating here would diverge the stored figures from that
            // issued document. Reject the edit and require a new revision instead.
            if (!await IsQuoteInDraftAsync(quote))
            {
                throw new InvalidOperationException(
                    $"Quote '{quote.QuoteNo}' can no longer be edited: it has left DRAFT status " +
                    "(it has been sent, accepted, or converted to an order). Editing would diverge the " +
                    "stored totals from the quotation already issued to the customer. Create a new revision instead.");
            }

            quote.QuoteNo = request.QuoteNo;
            quote.CustomerId = request.CustomerId;
            quote.QuoteDate = request.QuoteDate;
            quote.ValidUntil = request.ValidUntil;
            if (request.StatusId != quote.StatusId)
                throw new InvalidOperationException("Quote status changes require the governed lifecycle endpoint.");
            quote.CurrencyId = request.CurrencyId;
            quote.HeaderRemarks = request.HeaderRemarks;
            quote.ModifiedBy = request.ModifiedBy;
            quote.ModifiedDate = DateTime.UtcNow;
            quote.DiscountTypeId = request.DiscountTypeId;
            quote.DiscountValue = request.DiscountValue;

            // Handle Items - Simplified: Remove missing, Update existing, Add new
            // Note: For production, a more robust diffing is recommended.
            // Here we will clear and re-add or try to match. 
            // Matching by ID is best.

            // 1. Update existing and Add new
            foreach (var itemDto in request.QuoteItems)
            {
                // FIN-12: validate financial inputs for any item that will remain on the quote.
                if (!itemDto.IsDeleted)
                    ValidateQuoteItemFinancials(itemDto.Quantity, itemDto.UnitPrice, itemDto.TaxAmount);

                if (itemDto.Id.HasValue && itemDto.Id.Value > 0)
                {
                    var existingItem = quote.QuoteItems.FirstOrDefault(i => i.Id == itemDto.Id.Value);
                    if (existingItem != null)
                    {
                        if (itemDto.IsDeleted)
                        {
                            _context.QuoteItems.Remove(existingItem);
                        }
                        else
                        {
                            existingItem.RfqitemId = itemDto.RfqItemId;
                            existingItem.ProductId = itemDto.ProductId;
                            existingItem.ItemDescription = itemDto.ItemDescription;
                            existingItem.Quantity = itemDto.Quantity;
                            // Preserve-when-absent: the edit UI shows these read-only, and an
                            // older client that omits them must not silently strip the unit or
                            // the buyer's line reference from an RFQ-born line.
                            existingItem.UnitOfMeasure = itemDto.UnitOfMeasure ?? existingItem.UnitOfMeasure;
                            existingItem.CustomerLineRef = itemDto.CustomerLineRef ?? existingItem.CustomerLineRef;
                            existingItem.UnitPrice = itemDto.UnitPrice;
                            existingItem.DiscountTypeId = itemDto.DiscountTypeId;
                            existingItem.DiscountValue = itemDto.DiscountValue;
                            existingItem.TaxAmount = itemDto.TaxAmount;
                            existingItem.DeliveryLeadTime = itemDto.DeliveryLeadTime;
                            existingItem.ModifiedBy = request.ModifiedBy;
                            existingItem.ModifiedDate = DateTime.UtcNow;
                        }
                    }
                }
                else if (!itemDto.IsDeleted)
                {
                    quote.QuoteItems.Add(new QuoteItem
                    {
                        RfqitemId = itemDto.RfqItemId,
                        ProductId = itemDto.ProductId,
                        ItemDescription = itemDto.ItemDescription,
                        Quantity = itemDto.Quantity,
                        UnitOfMeasure = itemDto.UnitOfMeasure,
                        CustomerLineRef = itemDto.CustomerLineRef,
                        UnitPrice = itemDto.UnitPrice,
                        DiscountTypeId = itemDto.DiscountTypeId,
                        DiscountValue = itemDto.DiscountValue,
                        TaxAmount = itemDto.TaxAmount,
                        DeliveryLeadTime = itemDto.DeliveryLeadTime,
                        CreatedBy = request.ModifiedBy,
                        CreatedDate = DateTime.UtcNow
                    });
                }
            }

            await CalculateQuoteTotals(quote);

            await _context.SaveChangesAsync();

            return await GetQuoteByIdAsync(quote.Id);
        }

        private async Task CalculateQuoteTotals(Quote quote)
        {
            // Load setup for discounts if needed
            // We need to know if DiscountType is Percentage or Fixed.
            // Assuming we can load them.

            var discountTypeIds = new List<long>();
            if (quote.DiscountTypeId.HasValue) discountTypeIds.Add(quote.DiscountTypeId.Value);
            foreach (var item in quote.QuoteItems)
            {
                if (item.DiscountTypeId.HasValue) discountTypeIds.Add(item.DiscountTypeId.Value);
            }

            var discountTypes = await _context.SetupMasters
                .Where(s => discountTypeIds.Contains(s.SetupId))
                .ToDictionaryAsync(s => s.SetupId, s => s.SetupCode); // Assuming SetupCode is PERCENTAGE or FIXED

            decimal quoteSubTotal = 0;

            foreach (var item in quote.QuoteItems)
            {
                // FIN-09: round the gross line value to currency scale before applying discount.
                decimal itemTotal = RoundCurrency(item.Quantity * item.UnitPrice);
                decimal itemDiscountAmount = 0;

                if (item.DiscountTypeId.HasValue && item.DiscountValue.HasValue && discountTypes.ContainsKey(item.DiscountTypeId.Value))
                {
                    string code = discountTypes[item.DiscountTypeId.Value].ToUpper();
                    if (code == "PERCENTAGE")
                    {
                        itemDiscountAmount = itemTotal * (item.DiscountValue.Value / 100);
                    }
                    else if (code == "FIXED")
                    {
                        itemDiscountAmount = item.DiscountValue.Value;
                    }
                }

                // FIN-09: round the discount to currency scale as well.
                itemDiscountAmount = RoundCurrency(itemDiscountAmount);

                // Ensure discount doesn't exceed total? Optional business rule.
                if (itemDiscountAmount > itemTotal) itemDiscountAmount = itemTotal;

                // Item TotalAmount usually stores the Net Amount? Or Gross? 
                // Based on previous code: TotalAmount = Quantity * UnitPrice. It didn't account for discount.
                // But usually TotalAmount on line item is (Qty * Price) - Discount.
                // Let's assume TotalAmount is the final line amount.
                item.Discount = itemDiscountAmount; // Store calculated amount in 'Discount' column?
                // QuoteItem has 'Discount' (decimal) and now 'DiscountValue' (decimal).
                // 'Discount' was likely the amount. 'DiscountValue' is the input value (e.g. 10 for 10%).
                // YES.
                // FIN-09: round each line net to currency scale before summing so the printed
                // line totals reconcile with the printed grand total.
                item.TotalAmount = RoundCurrency(itemTotal - itemDiscountAmount + (item.TaxAmount ?? 0m));
                quoteSubTotal += item.TotalAmount;
            }

            // Quote Header Discount
            decimal quoteDiscountAmount = 0;
            if (quote.DiscountTypeId.HasValue && quote.DiscountValue.HasValue && discountTypes.ContainsKey(quote.DiscountTypeId.Value))
            {
                string code = discountTypes[quote.DiscountTypeId.Value].ToUpper();
                if (code == "PERCENTAGE")
                {
                    quoteDiscountAmount = quoteSubTotal * (quote.DiscountValue.Value / 100);
                }
                else if (code == "FIXED")
                {
                    quoteDiscountAmount = quote.DiscountValue.Value;
                }
            }

            quoteDiscountAmount = RoundCurrency(quoteDiscountAmount);
            if (quoteDiscountAmount > quoteSubTotal) quoteDiscountAmount = quoteSubTotal;

            quote.TotalAmount = RoundCurrency(quoteSubTotal - quoteDiscountAmount);
            quote.FinancialCalculationVersion = 2;
        }

        // Rounds a monetary value to the 2-decimal currency scale used on printed documents
        // (FIN-09). Half-away-from-zero matches standard commercial/accounting rounding.
        private static decimal RoundCurrency(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

        /// <summary>
        /// Deterministic line order for every read surface (PDF + DTO): lines carrying the
        /// buyer's own reference come first in that reference's order — numerically when the
        /// reference is a number so SAP-style "00010" &lt; "00020" &lt; "00100" (ordinal text
        /// would put "2" after "10") — then unreferenced lines in stable insertion (Id) order.
        /// A quote line collection loaded via Include carries no ordering guarantee at all,
        /// so without this the printed line sequence could differ between two exports.
        /// </summary>
        internal static IReadOnlyList<QuoteItem> OrderQuoteLines(IEnumerable<QuoteItem> items) => items
            .OrderBy(i => string.IsNullOrWhiteSpace(i.CustomerLineRef) ? 1 : 0)
            .ThenBy(i => long.TryParse(i.CustomerLineRef, out var n) ? n : long.MaxValue)
            .ThenBy(i => i.CustomerLineRef, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Id)
            .ToList();

        // FIN-12: server-side guard rejecting non-positive quantities/prices and negative tax.
        private static void ValidateQuoteItemFinancials(decimal quantity, decimal unitPrice, decimal? taxAmount)
        {
            if (quantity <= 0)
                throw new ArgumentException($"Invalid line quantity ({quantity}). Quantity must be greater than zero.");
            if (unitPrice <= 0)
                throw new ArgumentException($"Invalid unit price ({unitPrice}). Unit price must be greater than zero.");
            if (taxAmount.HasValue && taxAmount.Value < 0)
                throw new ArgumentException($"Invalid tax amount ({taxAmount}). Tax cannot be negative.");
        }

        // FIN-05: determines whether a quote is still an editable DRAFT. Resolves the DRAFT
        // status via SetupMaster code (BU-scoped first) rather than trusting a magic number,
        // mirroring the resolution pattern used in OrderService. Falls back to the documented
        // legacy id map (see TransitionStatusAsync: DRAFT=42) only when no DRAFT row is configured.
        private const long DraftQuoteStatusIdFallback = 42;

        private async Task<bool> IsQuoteInDraftAsync(Quote quote)
        {
            // A brand-new quote with no status yet is treated as an editable draft.
            if (!quote.StatusId.HasValue) return true;

            var draftStatus = await _context.SetupMasters
                .FirstOrDefaultAsync(s => s.SetupType == "QuoteStatus" && s.SetupCode == "DRAFT"
                    && s.BusinessUnitId == quote.BusinessUnitId);
            draftStatus ??= await _context.SetupMasters
                .FirstOrDefaultAsync(s => s.SetupType == "QuoteStatus" && s.SetupCode == "DRAFT");

            if (draftStatus != null)
                return quote.StatusId.Value == draftStatus.SetupId;

            // No DRAFT QuoteStatus configured — fall back to the documented legacy id.
            return quote.StatusId.Value == DraftQuoteStatusIdFallback;
        }

        private async Task<QuoteResponseDTO> GetQuoteByIdAsync(long id)
        {
            var quote = await _context.Quotes
               .Include(q => q.QuoteItems)
               .Include(q => q.Customer)
               .Include(q => q.BusinessUnit)
               .Include(q => q.Currency)
               .Include(q => q.Status)
               .Include(q => q.DiscountType)
               .FirstOrDefaultAsync(q => q.Id == id);

            if (quote == null) return null;

            // Outcome reason display name + BU stale threshold (WP-A4 / WP-A2).
            string? outcomeReasonName = null;
            if (quote.OutcomeReasonId.HasValue)
            {
                outcomeReasonName = await _context.SetupMasters.AsNoTracking()
                    .Where(s => s.SetupId == quote.OutcomeReasonId.Value)
                    .Select(s => s.Description ?? s.SetupValue)
                    .FirstOrDefaultAsync();
            }
            var staleQuoteDays = await _context.Set<ERP_RFQ_Automation.Sla.SlaPolicy>().AsNoTracking()
                .Where(p => p.BusinessUnitId == quote.BusinessUnitId)
                .Select(p => (int?)p.StaleQuoteDays)
                .FirstOrDefaultAsync() ?? ERP_RFQ_Automation.Sla.SlaPolicy.Default(quote.BusinessUnitId).StaleQuoteDays;

            // Load Discount Types for items (nested include or separate load)
            var itemDiscountTypeIds = quote.QuoteItems
               .Where(i => i.DiscountTypeId.HasValue)
               .Select(i => i.DiscountTypeId.Value)
               .Distinct()
               .ToList();

            var itemDiscountTypes = new Dictionary<long, string>();
            if (itemDiscountTypeIds.Any())
            {
                itemDiscountTypes = await _context.SetupMasters
                   .Where(s => itemDiscountTypeIds.Contains(s.SetupId))
                   .ToDictionaryAsync(s => s.SetupId, s => s.Description);
            }


            return new QuoteResponseDTO
            {
                Id = quote.Id,
                QuoteNo = quote.QuoteNo,
                RfqId = quote.Rfqid,
                RfqNo = quote.Rfq?.Rfqno, // Add Include if needed
                CustomerId = quote.CustomerId,
                CustomerName = quote.Customer?.Name,
                BusinessUnitId = quote.BusinessUnitId,
                BusinessUnitName = quote.BusinessUnit?.BusinessUnitName,
                QuoteDate = quote.QuoteDate,
                ValidUntil = quote.ValidUntil,
                StatusId = quote.StatusId,
                StatusValue = quote.Status?.SetupValue,
                StatusCode = quote.Status?.SetupCode,
                SentOn = quote.SentOn,
                RespondedOn = quote.RespondedOn,
                OutcomeOn = quote.OutcomeOn,
                OutcomeReasonId = quote.OutcomeReasonId,
                OutcomeReasonName = outcomeReasonName,
                OutcomeNote = quote.OutcomeNote,
                IsStale = ERP_RFQ_Automation.Sla.SlaComputed.IsStale(quote.Status?.SetupCode, quote.SentOn, quote.RespondedOn, staleQuoteDays),
                DaysSinceSent = ERP_RFQ_Automation.Sla.SlaComputed.DaysSinceSent(quote.SentOn),
                CurrencyId = quote.CurrencyId,
                CurrencyCode = quote.Currency?.Code,
                TotalAmount = quote.TotalAmount,
                HeaderRemarks = quote.HeaderRemarks,
                CreatedBy = quote.CreatedBy,
                CreatedDate = quote.CreatedDate,
                ModifiedBy = quote.ModifiedBy,
                ModifiedDate = quote.ModifiedDate,
                DiscountTypeId = quote.DiscountTypeId,
                DiscountTypeName = quote.DiscountType?.Description,
                DiscountValue = quote.DiscountValue,
                CustomerEmail = quote.Customer?.ContactEmail, // Map email
                QuoteItems = OrderQuoteLines(quote.QuoteItems).Select(i => new QuoteItemResponseDTO
                {
                    Id = i.Id,
                    QuoteId = i.QuoteId,
                    RfqItemId = i.RfqitemId,
                    ProductId = i.ProductId,
                    ProductName = i.Product?.ProductName,
                    ItemDescription = i.ItemDescription,
                    Quantity = i.Quantity,
                    UnitOfMeasure = i.UnitOfMeasure,
                    CustomerLineRef = i.CustomerLineRef,
                    UnitPrice = i.UnitPrice,
                    TotalAmount = i.TotalAmount,
                    Discount = i.Discount, // Calculated Amount
                    DiscountTypeId = i.DiscountTypeId,
                    DiscountValue = i.DiscountValue, // Input Value
                    DiscountTypeName = i.DiscountTypeId.HasValue && itemDiscountTypes.ContainsKey(i.DiscountTypeId.Value) ? itemDiscountTypes[i.DiscountTypeId.Value] : null,
                    TaxAmount = i.TaxAmount,
                    DeliveryLeadTime = i.DeliveryLeadTime
                }).ToList()
            };
        }

        public async Task<byte[]> GenerateQuotePdfAsync(long quoteId, long businessUnitId)
        {
            var quote = await _context.Quotes
                .Include(q => q.QuoteItems)
                    .ThenInclude(i => i.Product)
                .Include(q => q.Customer)
                .Include(q => q.BusinessUnit)
                .Include(q => q.Currency)
                .Include(q => q.Status)
                .Include(q => q.Rfq)
                    .ThenInclude(r => r.Lead)
                .FirstOrDefaultAsync(q => q.Id == quoteId && q.BusinessUnitId == businessUnitId);

            if (quote == null)
                throw new KeyNotFoundException($"Quote with ID {quoteId} not found.");

            var isDraft = string.Equals(quote.Status?.SetupCode, "DRAFT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(quote.Status?.SetupValue, "Draft", StringComparison.OrdinalIgnoreCase);
            if (isDraft && (!quote.CurrencyId.HasValue || !quote.ValidUntil.HasValue
                || quote.QuoteItems.Count == 0 || quote.QuoteItems.Any(item => item.UnitPrice <= 0)))
                throw new InvalidOperationException(
                    "Commercial Review Required: pricing, currency, validity, and every line price must be complete before PDF export.");

            // Fetch dynamic configurations from the new QuoteConfiguration table
            var config = await _quoteConfigRepository.GetByBusinessUnitIdAsync(quote.BusinessUnitId);

            string logoBase64 = config?.Logo;
            string primaryColor = config?.PrimaryColor ?? "#1e3a8a";
            string termsContent = config?.TermsAndConditions ??
                                "1. Prices are valid for 30 days from the date of the quote.\n" +
                                "2. Payment terms: Net 30 days from invoice date.\n" +
                                "3. Delivery dates are estimates and subject to confirmation.\n" +
                                "4. All products remain the property of the seller until fully paid.\n" +
                                "5. Any applicable taxes or duties are not included unless specified.\n" +
                                "6. Warranty and liability are as per the manufacturer's standard terms.\n" +
                                "7. This quote is confidential and intended solely for the recipient.";

            string companyAddress = config?.CompanyAddress ?? "123 Business Rd, Tech City, 54321";
            string companyPhone = config?.CompanyPhone ?? "+1 800 555 0199";
            string companyEmail = config?.CompanyEmail ?? quote.Rfq?.Lead?.Clientemail ?? "sales@company.com";
            string footerText = config?.FooterText ?? "Professional Business Solutions";

            byte[] logoBytes = null;
            if (!string.IsNullOrEmpty(logoBase64))
            {
                try
                {
                    // Remove data:image/png;base64, prefix if exists
                    if (logoBase64.Contains(",")) logoBase64 = logoBase64.Split(',')[1];
                    logoBytes = Convert.FromBase64String(logoBase64);
                }
                catch { }
            }

            // Calculate Totals for Display
            // FIN-09: sum per-line values already rounded to currency scale so the printed
            // subtotal reconciles with the printed per-line totals and grand total.
            decimal subTotal = quote.QuoteItems.Sum(i => RoundCurrency(i.Quantity * i.UnitPrice));
            decimal totalItemDiscounts = quote.QuoteItems.Sum(i => RoundCurrency(i.Discount ?? 0));
            decimal totalTax = quote.QuoteItems.Sum(i => RoundCurrency(i.TaxAmount ?? 0));

            decimal headerDiscount = 0;
            if (quote.DiscountTypeId.HasValue && quote.DiscountValue.HasValue)
            {
                decimal itemsNetTotal = subTotal - totalItemDiscounts + totalTax;
                headerDiscount = itemsNetTotal - (quote.TotalAmount ?? 0);
                if (headerDiscount < 0) headerDiscount = 0;
            }

            // The buyer's own RFQ number: Lead.Rfqno is the value the customer sent us;
            // Rfq.Rfqno equals it when it existed and is a synthetic internal serial otherwise,
            // so it is only a fallback. A procurement buyer files our quote under THEIR number.
            string customerRfqReference = quote.Rfq?.Lead?.Rfqno ?? quote.Rfq?.Rfqno;

            // Deterministic print order: buyer's line references first (numeric-aware), then
            // unreferenced lines by insertion order. See OrderQuoteLines.
            var orderedItems = OrderQuoteLines(quote.QuoteItems);

            QuestPDF.Settings.License = LicenseType.Community;

            var document = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(40);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(x => x.FontFamily(Fonts.Arial).FontSize(10).FontColor(Colors.Grey.Darken4));

                    // 0. Accent Bar (Top)
                    page.Header().Column(col =>
                    {
                        col.Item().Height(3).Background(primaryColor);
                        col.Item().PaddingBottom(20).PaddingTop(20).Row(row =>
                        {
                            // Company Info (Left)
                            row.RelativeItem().Column(c =>
                            {
                                if (logoBytes != null)
                                {
                                    c.Item().Height(55).Image(logoBytes);
                                }
                                else
                                {
                                    c.Item().Text(quote.BusinessUnit?.BusinessUnitName ?? "Company Name")
                                        .FontSize(22).Bold().FontColor(primaryColor);
                                }

                                c.Item().PaddingTop(8).Text(footerText)
                                    .FontSize(9).Italic().FontColor(Colors.Grey.Darken1);

                                c.Item().PaddingTop(10).Column(details =>
                                {
                                    details.Spacing(1);
                                    details.Item().Text(companyAddress).FontSize(8).FontColor(Colors.Grey.Medium);
                                    details.Item().Text($"P: {companyPhone}").FontSize(8).FontColor(Colors.Grey.Medium);
                                    details.Item().Text($"E: {companyEmail}").FontSize(8).FontColor(Colors.Grey.Medium);
                                });
                            });

                            // Quote Info (Right)
                            row.ConstantItem(180).AlignRight().Column(c =>
                            {
                                c.Item().Text("QUOTATION")
                                    .FontSize(22).ExtraBold().FontColor(Colors.Grey.Lighten2);

                                c.Item().PaddingTop(15).Column(info =>
                                {
                                    info.Spacing(2);
                                    info.Item().Text(t => { t.Span("Reference No: ").SemiBold(); t.Span(quote.QuoteNo); });
                                    if (!string.IsNullOrWhiteSpace(customerRfqReference))
                                        info.Item().Text(t => { t.Span("Your RFQ Reference: ").SemiBold(); t.Span(customerRfqReference); });
                                    info.Item().Text(t => { t.Span("Quote Date: ").SemiBold(); t.Span($"{quote.QuoteDate:MMM dd, yyyy}"); });
                                    info.Item().Text(t => { t.Span("Valid Until: ").SemiBold(); t.Span($"{quote.ValidUntil:MMM dd, yyyy}"); });
                                });


                            });
                        });
                    });

                    // 2. Content
                    page.Content().PaddingVertical(10).Column(col =>
                    {
                        col.Spacing(30);

                        // Address Section
                        col.Item().Row(row =>
                        {
                            void AddressBlock(string label, string name, string line1, string line2, string cityCountry, string email = null)
                            {
                                row.RelativeItem().Border(1).BorderColor(Colors.Grey.Lighten4).Padding(12).Column(c =>
                                {
                                    c.Item().Text(label).FontSize(8).ExtraBold().FontColor(primaryColor);
                                    c.Item().PaddingTop(5).Text(name).Bold().FontSize(11);
                                    c.Item().Text(line1).FontSize(9);
                                    if (!string.IsNullOrEmpty(line2)) c.Item().Text(line2).FontSize(9);
                                    c.Item().Text(cityCountry).FontSize(9);
                                    if (email != null) c.Item().PaddingTop(5).Text(email).FontSize(8).Italic();
                                });
                            }

                            AddressBlock("BILL TO",
                                quote.Customer?.Name ?? "Customer",
                                quote.Customer?.BillingAddressLine1 ?? "N/A",
                                quote.Customer?.BillingAddressLine2,
                                $"{quote.Customer?.BillingCity ?? ""}, {quote.Customer?.BillingCountry ?? ""}",
                                quote.Customer?.ContactEmail);

                            row.ConstantItem(30); // Gap

                            AddressBlock("SHIP TO",
                                quote.Customer?.Name ?? "Customer",
                                quote.Customer?.ShippingAddressLine1 ?? quote.Customer?.BillingAddressLine1 ?? "N/A",
                                quote.Customer?.ShippingAddressLine2,
                                $"{quote.Customer?.ShippingCity ?? quote.Customer?.BillingCity ?? ""}, {quote.Customer?.ShippingCountry ?? quote.Customer?.BillingCountry ?? ""}");
                        });

                        // Items Table
                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(columns =>
                            {
                                columns.ConstantColumn(50);   // buyer's line ref ("00010", "OPT-29") — wider than the old synthetic index
                                columns.RelativeColumn(5);
                                columns.ConstantColumn(50);
                                columns.ConstantColumn(40);   // unit of measure
                                columns.ConstantColumn(80);
                                columns.ConstantColumn(80);
                            });

                            table.Header(header =>
                            {
                                IContainer CellStyle(IContainer container) => container.Background(primaryColor).PaddingVertical(10).PaddingHorizontal(5).DefaultTextStyle(x => x.SemiBold().FontSize(9).FontColor(Colors.White));

                                header.Cell().Element(CellStyle).Text("Your Ref");
                                header.Cell().Element(CellStyle).Text("Description");
                                header.Cell().Element(CellStyle).AlignRight().Text("Qty");
                                header.Cell().Element(CellStyle).Text("UOM");
                                header.Cell().Element(CellStyle).AlignRight().Text("Unit Price");
                                header.Cell().Element(CellStyle).AlignRight().Text("Total");
                            });

                            foreach (var item in orderedItems.Select((x, i) => new { x, i }))
                            {
                                var backgroundColor = item.i % 2 == 0 ? Colors.White : Colors.Grey.Lighten5;

                                IContainer RowStyle(IContainer container) => container.Background(backgroundColor).BorderBottom(1).BorderColor(Colors.Grey.Lighten4).PaddingVertical(8).PaddingHorizontal(5);

                                // The buyer's own line reference lets them match this line against
                                // their RFQ; the synthetic 1,2,3 is only the legacy-row fallback.
                                table.Cell().Element(RowStyle).Text(
                                    string.IsNullOrWhiteSpace(item.x.CustomerLineRef) ? (item.i + 1).ToString() : item.x.CustomerLineRef);
                                table.Cell().Element(RowStyle).Column(c =>
                                {
                                    c.Item().Text(item.x.ItemDescription).SemiBold();
                                    if (item.x.Discount > 0)
                                        c.Item().Text($"Discount: {quote.Currency?.Code} {item.x.Discount:N2}").FontSize(8).Italic().FontColor(Colors.Red.Medium);
                                });
                                table.Cell().Element(RowStyle).AlignRight().Text(item.x.Quantity.ToString("N0"));
                                table.Cell().Element(RowStyle).Text(item.x.UnitOfMeasure ?? string.Empty);
                                table.Cell().Element(RowStyle).AlignRight().Text(item.x.UnitPrice.ToString("N2"));
                                table.Cell().Element(RowStyle).AlignRight().Text(item.x.TotalAmount.ToString("N2")).Bold();
                            }
                        });

                        // Lower Section: Terms and Financials
                        col.Item().Row(row =>
                        {
                            // Terms (Left)
                            row.RelativeItem(1.5f).Column(c =>
                            {
                                c.Item().PaddingTop(10).Text("Terms & Conditions").Bold().FontSize(10).FontColor(primaryColor);
                                c.Item().PaddingTop(5).Text(termsContent).FontSize(8).LineHeight(1.2f).FontColor(Colors.Grey.Darken1);

                                c.Item().PaddingTop(30).Text("Thank you for your business!").Italic().FontSize(10).FontColor(Colors.Grey.Medium);
                            });

                            row.ConstantItem(40);

                            // Financials (Right)
                            row.RelativeItem(1f).Column(c =>
                            {
                                var currency = quote.Currency?.Code ?? "USD";

                                void FinancialRow(string label, decimal value, bool isTotal = false)
                                {
                                    c.Item().PaddingVertical(isTotal ? 8 : 3).Row(r =>
                                    {
                                        var text = r.RelativeItem().Text(label).FontSize(isTotal ? 11 : 9);
                                        if (isTotal) text.Bold();

                                        var valueText = r.RelativeItem().AlignRight().Text($"{currency} {value:N2}").FontSize(isTotal ? 12 : 9);
                                        if (isTotal) valueText.Bold();
                                    });
                                }

                                c.Item().PaddingTop(10).Column(inner =>
                                {
                                    FinancialRow("Subtotal", subTotal);
                                    if (totalItemDiscounts > 0) FinancialRow("Item Discounts", -totalItemDiscounts);
                                    if (headerDiscount > 0) FinancialRow("Additional Discount", -headerDiscount);
                                    if (totalTax > 0) FinancialRow("Tax / VAT", totalTax);

                                    inner.Item().PaddingVertical(5).LineHorizontal(1).LineColor(Colors.Grey.Lighten3);

                                    inner.Item().Background(primaryColor).Padding(10).Row(r =>
                                    {
                                        r.RelativeItem().Text("GRAND TOTAL").FontSize(12).Bold().FontColor(Colors.White);
                                        r.RelativeItem().AlignRight().Text($"{currency} {(quote.TotalAmount ?? 0):N2}").FontSize(14).Bold().FontColor(Colors.White);
                                    });
                                });
                            });
                        });
                    });

                    // 3. Footer
                    page.Footer().PaddingTop(20).Column(col =>
                    {
                        col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
                        col.Item().PaddingTop(10).Row(row =>
                        {
                            row.RelativeItem().Text(x =>
                            {
                                x.Span("Generated by ").FontSize(8).FontColor(Colors.Grey.Medium);
                                x.Span("System").FontSize(8).SemiBold().FontColor(Colors.Grey.Medium);
                                x.Span($" | {DateTime.Now:MMMM dd, yyyy HH:mm}").FontSize(8).FontColor(Colors.Grey.Medium);
                            });

                            row.RelativeItem().AlignRight().Text(x =>
                            {
                                x.Span("Page ").FontSize(8).FontColor(Colors.Grey.Medium);
                                x.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Medium);
                                x.Span(" of ").FontSize(8).FontColor(Colors.Grey.Medium);
                                x.TotalPages().FontSize(8).FontColor(Colors.Grey.Medium);
                            });
                        });
                    });
                });
            });

            return document.GeneratePdf();
        }

        public async Task<QuoteSendResult> SendQuoteEmailAsync(long quoteId, long businessUnitId, string recipientEmail, string? customSubject = null, string? customBody = null, QuoteSendOptions? options = null)
        {
            if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
            options ??= new QuoteSendOptions();

            var quote = await _context.Quotes
                .Include(q => q.BusinessUnit)
                .Include(q => q.Rfq)
                    .ThenInclude(r => r.Lead)
                .FirstOrDefaultAsync(q => q.Id == quoteId && q.BusinessUnitId == businessUnitId)
                ?? throw new KeyNotFoundException("Quote not found");

            // WP-B3 below-floor gate: recompute floors for the quote's RFQ and hold
            // the ENTIRE send when any current line price is under its floor. The
            // approve_below_floor_quote tool re-enters here with BypassFloorHold=true
            // once a manager approves, so the held send cannot re-hold itself.
            if (!options.BypassFloorHold && _belowFloorGuard is not null)
            {
                var check = await _belowFloorGuard.CheckQuoteSendAsync(quoteId, businessUnitId, CancellationToken.None);
                if (check.IsBelowFloor)
                {
                    var approval = await _belowFloorGuard.CreateSendHoldAsync(
                        quoteId, businessUnitId, recipientEmail, customSubject, customBody, check,
                        options.RequestedByUserId, options.RequestedBy, CancellationToken.None);
                    return QuoteSendResult.HeldForApproval(approval.Id, approval.Summary);
                }
            }

            var subject = !string.IsNullOrEmpty(customSubject)
                ? customSubject
                : $"Quote #{quote.QuoteNo} from {quote.BusinessUnit?.BusinessUnitName ?? "Our Company"}";

            var body = !string.IsNullOrEmpty(customBody)
                ? customBody.Replace("\n", "<br/>")
                : $@"
                <p>Dear Customer,</p>
                <p>Please find attached the quote #{quote.QuoteNo}.</p>
                <p>Thank you for your business.</p>
                <br/>
                <p>Best Regards,</p>
                <p>{quote.BusinessUnit?.BusinessUnitName ?? "Sales Team"}</p>
            ";

            var deliveryKey = $"quote:{quote.Id}:delivery:v1";
            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                var ownsTransaction = _context.Database.CurrentTransaction is null;
                await using var transaction = ownsTransaction
                    ? await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable)
                    : null;
                try
                {
                    if (_context.Database.IsNpgsql())
                        await _context.Database.ExecuteSqlInterpolatedAsync(
                            $"SELECT pg_advisory_xact_lock(hashtextextended({$"quote-delivery:{quote.BusinessUnitId}:{quote.Id}"}, 0))");
                    var existingDelivery = await _context.QuoteDeliveryRequests.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.BusinessUnitId == quote.BusinessUnitId && x.IdempotencyKey == deliveryKey);
                    if (existingDelivery is not null)
                    {
                        if (!string.Equals(existingDelivery.RecipientEmail, recipientEmail.Trim(), StringComparison.OrdinalIgnoreCase)
                            || existingDelivery.Subject != subject || existingDelivery.Body != body)
                            throw new InvalidOperationException("The quote delivery key was already used with different content.");
                        if (transaction is not null) await transaction.CommitAsync();
                        if (existingDelivery.DeadLetteredOn.HasValue)
                            return QuoteSendResult.Failed(existingDelivery.LastErrorCode ?? "Delivery failed permanently.");
                        return QuoteSendResult.Queued(existingDelivery.CompletedOn.HasValue, true);
                    }
                    _context.QuoteDeliveryRequests.Add(new QuoteDeliveryRequest
                    {
                        BusinessUnitId = quote.BusinessUnitId,
                        QuoteId = quote.Id,
                        IdempotencyKey = deliveryKey,
                        RecipientEmail = recipientEmail.Trim(),
                        Subject = subject,
                        Body = body,
                        FromEmail = quote.Rfq?.Lead?.Clientemail,
                        AttachmentFileName = $"Quote_{quote.QuoteNo}.pdf",
                        RequestedOn = DateTime.UtcNow,
                        AvailableOn = DateTime.UtcNow,
                        Version = 1
                    });
                    await _context.SaveChangesAsync();
                    if (transaction is not null) await transaction.CommitAsync();
                }
                catch
                {
                    if (transaction is not null) await transaction.RollbackAsync();
                    throw;
                }
                return QuoteSendResult.Queued(false, false);
            });
        }

        public async Task FinalizeQuoteDeliveryAsync(long quoteId, long businessUnitId, CancellationToken ct = default)
        {
            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var quote = await _context.Quotes
                    .Include(q => q.Rfq).ThenInclude(r => r.Lead)
                    .SingleOrDefaultAsync(q => q.Id == quoteId && q.BusinessUnitId == businessUnitId, ct)
                    ?? throw new KeyNotFoundException("Quote not found");
                if (quote.SentOn.HasValue)
                {
                    await transaction.CommitAsync(ct);
                    return;
                }

                quote.SentOn = DateTime.UtcNow;
                quote.ModifiedDate = quote.SentOn;
                if (_lifecycle is not null)
                {
                    await _lifecycle.TransitionQuoteInCurrentTransactionAsync(
                        businessUnitId, quote.Id,
                        new LifecycleActor("system:quote-delivery", "quote-delivery-worker"),
                        new LifecycleTransitionCommand(
                            "SENT", quote.LifecycleVersion, null, null, "quote-email-delivered",
                            Guid.NewGuid().ToString("N"), $"quote:{quote.Id}:delivery",
                            $"quote-delivered:{quote.Id}"),
                        false, ct);
                }
                else
                {
                    quote.StatusId = await ResolveQuoteStatusIdAsync("SENT", businessUnitId);
                    await _context.SaveChangesAsync(ct);
                }
                await RecordQuoteSentWorkAsync(quote, new QuoteSendOptions
                {
                    RequestedBy = "system:quote-delivery",
                }, ct);
                await transaction.CommitAsync(ct);
            });
        }

        private async Task RecordQuoteSentWorkAsync(Quote quote, QuoteSendOptions options, CancellationToken ct)
        {
            var lead = quote.Rfq?.Lead;
            if (_sales is null || lead?.AssignTo is not > 0 || !quote.SentOn.HasValue) return;
            var actor = string.IsNullOrWhiteSpace(options.RequestedBy) ? "system:quote-send" : options.RequestedBy.Trim();
            var correlation = $"quote-send:{quote.Id}";
            await _sales.AppendActivityAsync(quote.BusinessUnitId, new AppendCommercialActivityCommand(
                lead.AssignTo.Value, CommercialActivityType.QuoteSent, "Quote", quote.Id,
                lead.CustomerId, null, quote.SentOn.Value, "SENT", $"quote:{quote.Id}:sent",
                actor, correlation, $"quote:{quote.Id}:sent-activity"), ct);
            var staleDays = await _context.Set<SlaPolicy>().AsNoTracking()
                .Where(x => x.BusinessUnitId == quote.BusinessUnitId).Select(x => (int?)x.StaleQuoteDays)
                .SingleOrDefaultAsync(ct) ?? SlaPolicy.Default(quote.BusinessUnitId).StaleQuoteDays;
            await _sales.CreateFollowUpAsync(quote.BusinessUnitId, new CreateFollowUpTaskCommand(
                lead.AssignTo.Value, "Quote", quote.Id, lead.CustomerId,
                quote.SentOn.Value.AddDays(staleDays), 2, "QUOTE_RESPONSE",
                actor, correlation, $"quote:{quote.Id}:sent-follow-up"), ct);
        }

        // ==================================================================
        // Revisions-lite (WP-B4)
        // ==================================================================

        public async Task<QuoteResponseDTO> ReviseQuoteAsync(long quoteId, long businessUnitId, string actor)
        {
            var isolation = _context.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;
            await using var transaction = await _context.Database.BeginTransactionAsync(isolation);
            if (_context.Database.IsNpgsql())
            {
                await _context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(73001, {checked((int)businessUnitId)})");
                await _context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"Quotes\" WHERE \"BusinessUnitID\" = {businessUnitId} AND \"ID\" = {quoteId} FOR UPDATE");
            }
            var source = await _context.Quotes
                .Include(q => q.QuoteItems)
                .FirstOrDefaultAsync(q => q.Id == quoteId && q.BusinessUnitId == businessUnitId);
            if (source == null) throw new KeyNotFoundException($"Quote with ID {quoteId} not found.");

            if (await IsQuoteInDraftAsync(source))
                throw new InvalidOperationException(
                    $"Quote '{source.QuoteNo}' is still a draft — edit it directly instead of creating a revision.");

            var successor = await _context.Quotes.AsNoTracking()
                .Where(q => q.RevisionOfQuoteId == quoteId)
                .Select(q => new { q.QuoteNo, q.RevisionNo })
                .FirstOrDefaultAsync();
            if (successor != null)
                throw new InvalidOperationException(
                    $"Quote '{source.QuoteNo}' has already been revised as '{successor.QuoteNo}' (Rev {successor.RevisionNo}). " +
                    "Revise the latest revision instead.");

            // Chain lock: award/outcome on ANY revision closes the whole chain.
            var chain = await LoadRevisionChainAsync(source.Id, source.RevisionOfQuoteId);
            var closed = chain.FirstOrDefault(c => c.OutcomeOn.HasValue);
            if (closed != null)
                throw new InvalidOperationException(
                    $"This quote chain is closed — an outcome was already recorded on '{closed.QuoteNo}'. " +
                    "No further revisions can be created.");

            var now = DateTime.UtcNow;
            var revision = new Quote
            {
                QuoteNo = NextRevisionQuoteNo(source.QuoteNo, source.RevisionNo + 1),
                Rfqid = source.Rfqid,
                CustomerId = source.CustomerId,
                BusinessUnitId = source.BusinessUnitId,
                QuoteDate = now,
                ValidUntil = source.ValidUntil,
                StatusId = await ResolveQuoteStatusIdAsync("DRAFT", source.BusinessUnitId),
                CurrencyId = source.CurrencyId,
                HeaderRemarks = source.HeaderRemarks,
                DiscountTypeId = source.DiscountTypeId,
                DiscountValue = source.DiscountValue,
                CreatedBy = actor,
                CreatedDate = now,
                RevisionOfQuoteId = source.Id,
                RevisionNo = source.RevisionNo + 1,
                QuoteItems = source.QuoteItems.Select(i => new QuoteItem
                {
                    RfqitemId = i.RfqitemId,
                    ProductId = i.ProductId,
                    ItemDescription = i.ItemDescription,
                    Quantity = i.Quantity,
                    UnitOfMeasure = i.UnitOfMeasure,
                    CustomerLineRef = i.CustomerLineRef,
                    UnitPrice = i.UnitPrice,
                    DiscountTypeId = i.DiscountTypeId,
                    DiscountValue = i.DiscountValue,
                    TaxAmount = i.TaxAmount,
                    DeliveryLeadTime = i.DeliveryLeadTime,
                    CreatedBy = actor,
                    CreatedDate = now
                }).ToList()
            };
            revision.InheritCommercialIdentity(source);

            await CalculateQuoteTotals(revision);

            _context.Quotes.Add(revision);
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            return await GetQuoteByIdAsync(revision.Id);
        }

        public async Task<QuoteRevisionInfoDTO> GetRevisionInfoAsync(long quoteId, long businessUnitId)
        {
            var quote = await _context.Quotes
                .FirstOrDefaultAsync(q => q.Id == quoteId && q.BusinessUnitId == businessUnitId);
            if (quote == null) throw new KeyNotFoundException($"Quote with ID {quoteId} not found.");

            string? predecessorNo = null;
            if (quote.RevisionOfQuoteId.HasValue)
            {
                predecessorNo = await _context.Quotes.AsNoTracking()
                    .Where(q => q.Id == quote.RevisionOfQuoteId.Value)
                    .Select(q => q.QuoteNo)
                    .FirstOrDefaultAsync();
            }

            var successor = await _context.Quotes.AsNoTracking()
                .Where(q => q.RevisionOfQuoteId == quoteId)
                .Select(q => new { q.Id, q.QuoteNo })
                .FirstOrDefaultAsync();

            var chain = await LoadRevisionChainAsync(quote.Id, quote.RevisionOfQuoteId);
            var chainLocked = quote.OutcomeOn.HasValue || chain.Any(c => c.OutcomeOn.HasValue);
            var isDraft = await IsQuoteInDraftAsync(quote);

            return new QuoteRevisionInfoDTO
            {
                QuoteId = quote.Id,
                QuoteNo = quote.QuoteNo,
                RevisionNo = quote.RevisionNo,
                RevisionOfQuoteId = quote.RevisionOfQuoteId,
                RevisionOfQuoteNo = predecessorNo,
                SupersededByQuoteId = successor?.Id,
                SupersededByQuoteNo = successor?.QuoteNo,
                ChainLocked = chainLocked,
                CanRevise = !isDraft && successor == null && !chainLocked
            };
        }

        private sealed record RevisionChainMember(long Id, string QuoteNo, DateTime? OutcomeOn, long? RevisionOfQuoteId);

        /// <summary>
        /// All members of a quote's revision chain (the quote itself included):
        /// walks predecessor links up to the root, then successor links down.
        /// Chains are short (revisions-lite); a visited-set + hop cap guards
        /// against pathological/cyclic data.
        /// </summary>
        private async Task<List<RevisionChainMember>> LoadRevisionChainAsync(long quoteId, long? revisionOfQuoteId)
        {
            const int maxHops = 50;
            var members = new List<RevisionChainMember>();
            var visited = new HashSet<long> { quoteId };

            // The quote itself.
            var self = await _context.Quotes.AsNoTracking()
                .Where(q => q.Id == quoteId)
                .Select(q => new RevisionChainMember(q.Id, q.QuoteNo, q.OutcomeOn, q.RevisionOfQuoteId))
                .FirstOrDefaultAsync();
            if (self != null) members.Add(self);

            // Walk up to the root.
            var upId = revisionOfQuoteId;
            for (var hop = 0; upId.HasValue && hop < maxHops; hop++)
            {
                if (!visited.Add(upId.Value)) break;
                var member = await _context.Quotes.AsNoTracking()
                    .Where(q => q.Id == upId.Value)
                    .Select(q => new RevisionChainMember(q.Id, q.QuoteNo, q.OutcomeOn, q.RevisionOfQuoteId))
                    .FirstOrDefaultAsync();
                if (member == null) break;
                members.Add(member);
                upId = member.RevisionOfQuoteId;
            }

            // Walk down through successors (linked list: one successor per member).
            long? downId = quoteId;
            for (var hop = 0; downId.HasValue && hop < maxHops; hop++)
            {
                var member = await _context.Quotes.AsNoTracking()
                    .Where(q => q.RevisionOfQuoteId == downId.Value)
                    .Select(q => new RevisionChainMember(q.Id, q.QuoteNo, q.OutcomeOn, q.RevisionOfQuoteId))
                    .FirstOrDefaultAsync();
                if (member == null || !visited.Add(member.Id)) break;
                members.Add(member);
                downId = member.Id;
            }

            return members;
        }

        /// <summary>"QT-0725-0003" → "QT-0725-0003-R2"; "QT-0725-0003-R2" → "QT-0725-0003-R3".</summary>
        private static string NextRevisionQuoteNo(string quoteNo, int revisionNo)
        {
            var baseNo = System.Text.RegularExpressions.Regex.Replace(quoteNo ?? "", @"-R\d+$", "");
            return $"{baseNo}-R{revisionNo}";
        }

        public async Task<QuoteResponseDTO> TransitionStatusAsync(long id, string statusCode, string modifiedBy)
        {
            var quote = await _context.Quotes.FindAsync(id);
            if (quote == null) throw new KeyNotFoundException($"Quote with ID {id} not found.");

            if (_lifecycle is not null)
            {
                var code = statusCode.Trim().ToUpperInvariant();
                await _lifecycle.TransitionQuoteAsync(
                    quote.BusinessUnitId,
                    id,
                    new LifecycleActor(modifiedBy, "quote-service-legacy-adapter"),
                    new LifecycleTransitionCommand(
                        code,
                        quote.LifecycleVersion,
                        LifecyclePolicy.RequiresReason("Quote", LifecyclePolicy.Canonicalize("Quote", code), false)
                            ? "LEGACY_STATUS_TRANSITION"
                            : null,
                        null,
                        "quote-service-legacy-adapter",
                        Guid.NewGuid().ToString("N"),
                        $"quote:{id}:status:{code}",
                        $"quote-status:{id}:v{quote.LifecycleVersion}:{code}"),
                    false,
                    CancellationToken.None);
                return await GetQuoteByIdAsync(id);
            }

            // All codes resolve through SetupMaster (SetupType "QuoteStatus" +
            // SetupCode, BU-scoped first) with the documented legacy id map as the
            // last-resort fallback — no more hardcoded 42/43/44/45 branches.
            var legacyCode = statusCode.ToUpperInvariant();
            long? statusId;
            switch (legacyCode)
            {
                case "DRAFT":
                case "SENT":
                case "ACCEPTED":
                case "REJECTED":
                case "EXPIRED": // seeded create-if-absent by QuoteOutcomeService (WP-A4)
                    statusId = await ResolveQuoteStatusIdAsync(legacyCode, quote.BusinessUnitId);
                    if (statusId is null)
                        throw new ArgumentException(
                            $"No '{legacyCode}' QuoteStatus is configured for this business unit.");
                    break;

                case "ORDERED":
                    // Preserve the historical lenient match (code OR display value).
                    var orderedStatus = await _context.SetupMasters
                        .FirstOrDefaultAsync(sm => sm.SetupType == "QuoteStatus" &&
                            (sm.SetupCode == "ORDERED" || sm.SetupValue == "ORDERED" || sm.SetupValue == "Ordered"));
                    statusId = orderedStatus?.SetupId
                        ?? await ResolveQuoteStatusIdAsync("ACCEPTED", quote.BusinessUnitId); // historical fallback
                    break;

                default:
                    throw new ArgumentException($"Invalid status code: {statusCode}");
            }

            quote.StatusId = statusId;
            quote.ModifiedBy = modifiedBy;
            quote.ModifiedDate = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return await GetQuoteByIdAsync(id);
        }

        public async Task ResolveRevisionImpactAsync(long quoteId, long businessUnitId, string actor,
            string idempotencyKey, CancellationToken ct = default)
        {
            await using var transaction = _context.Database.IsNpgsql() && _context.Database.CurrentTransaction is null
                ? await _context.Database.BeginTransactionAsync(ct)
                : null;

            if (_context.Database.IsNpgsql())
            {
                var lockKey = $"quote-impact-resolution:{businessUnitId}:{quoteId}";
                await _context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
            }

            if (!await _context.Quotes.AsNoTracking()
                .AnyAsync(x => x.Id == quoteId && x.BusinessUnitId == businessUnitId, ct))
                throw new KeyNotFoundException();

            var impacts = await _context.Set<ERP_RFQ_Automation.LeadIdentity.LeadRevisionImpact>()
                .AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && x.AggregateType == "QUOTE"
                    && x.AggregateId == quoteId && x.Status == "OPEN")
                .Where(impact => !_context.Set<ERP_RFQ_Automation.LeadIdentity.LeadIdentityAuditEvent>()
                    .Any(audit => audit.BusinessUnitId == businessUnitId
                        && audit.EventType == "REVISION_IMPACT_RESOLVED"
                        && audit.CorrelationId == "quote-impact:" + impact.Id))
                .OrderBy(x => x.Id)
                .Select(impact => new
                {
                    Impact = impact,
                    OccurrenceId = _context.Set<ERP_RFQ_Automation.LeadIdentity.LeadRevision>()
                        .Where(revision => revision.BusinessUnitId == businessUnitId && revision.Id == impact.LeadRevisionId)
                        .Select(revision => revision.EstablishedByOccurrenceId)
                        .Single()
                })
                .ToListAsync(ct);
            if (impacts.Count == 0)
            {
                if (transaction is not null) await transaction.CommitAsync(ct);
                return;
            }

            foreach (var row in impacts)
            {
                var impact = row.Impact;
                _context.Add(new ERP_RFQ_Automation.LeadIdentity.LeadIdentityAuditEvent
                {
                    BusinessUnitId = businessUnitId,
                    LeadId = impact.LeadId,
                    OccurrenceId = row.OccurrenceId,
                    EventType = "REVISION_IMPACT_RESOLVED",
                    PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { impactId = impact.Id, quoteId }),
                    ActorType = "User",
                    ActorId = actor,
                    CorrelationId = $"quote-impact:{impact.Id}",
                    IdempotencyKey = $"{idempotencyKey}:{impact.Id}",
                    OccurredAtUtc = DateTimeOffset.UtcNow
                });
            }
            await _context.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }

        public Task<QuoteResponseDTO> GetQuoteAsync(long id) => GetQuoteByIdAsync(id);
    }
}
