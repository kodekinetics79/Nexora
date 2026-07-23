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

namespace ERP_RFQ_Automation.Services
{
    public interface IQuoteService
    {
        Task<byte[]> GenerateQuotePdfAsync(long quoteId);

        /// <summary>
        /// Emails the quote PDF and stamps SENT/SentOn — unless a line is priced
        /// below its pricing-engine floor (WP-B3), in which case NOTHING is sent:
        /// the send is parked as a pending approve_below_floor_quote approval and
        /// the result says so. options.BypassFloorHold (approved-hold execution
        /// only) skips the check.
        /// </summary>
        Task<QuoteSendResult> SendQuoteEmailAsync(long quoteId, string recipientEmail, string? customSubject = null, string? customBody = null, QuoteSendOptions? options = null);
        Task<QuoteResponseDTO> CreateQuoteAsync(QuoteCreateRequestDTO request);
        Task<QuoteResponseDTO> UpdateQuoteAsync(long id, QuoteUpdateRequestDTO request);
        Task<QuoteResponseDTO> TransitionStatusAsync(long id, string statusCode, string modifiedBy);

        /// <summary>
        /// Revisions-lite (WP-B4): clones a non-DRAFT quote (+items) as a new DRAFT
        /// revision (RevisionNo+1, linked back via RevisionOfQuoteId). Throws
        /// InvalidOperationException (→ 409) when the quote is a draft, already
        /// superseded, or its chain is locked by a recorded outcome.
        /// </summary>
        Task<QuoteResponseDTO> ReviseQuoteAsync(long quoteId, long businessUnitId, string actor);

        /// <summary>Revision-chain facts for one quote (chip + Revise button state).</summary>
        Task<QuoteRevisionInfoDTO> GetRevisionInfoAsync(long quoteId, long businessUnitId);
    }

    public class QuoteService : IQuoteService
    {
        private readonly ErpRfqAutomationContext _context;
        private readonly IEmailService _emailService;
        private readonly IQuoteConfigurationRepository _quoteConfigRepository;
        private readonly ERP_RFQ_Automation.Intelligence.Pricing.IBelowFloorGuard? _belowFloorGuard;

        // The below-floor guard is optional (defaults to null) so existing direct
        // constructions (tests, pre-wiring DI) keep working; without it the send
        // path simply performs no floor check (pre-WP-B3 behaviour).
        public QuoteService(
            ErpRfqAutomationContext context,
            IEmailService emailService,
            IQuoteConfigurationRepository quoteConfigRepository,
            ERP_RFQ_Automation.Intelligence.Pricing.IBelowFloorGuard? belowFloorGuard = null)
        {
            _context = context;
            _emailService = emailService;
            _quoteConfigRepository = quoteConfigRepository;
            _belowFloorGuard = belowFloorGuard;
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
            quote.StatusId = request.StatusId;
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
                QuoteItems = quote.QuoteItems.Select(i => new QuoteItemResponseDTO
                {
                    Id = i.Id,
                    QuoteId = i.QuoteId,
                    RfqItemId = i.RfqitemId,
                    ProductId = i.ProductId,
                    ProductName = i.Product?.ProductName,
                    ItemDescription = i.ItemDescription,
                    Quantity = i.Quantity,
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

        public async Task<byte[]> GenerateQuotePdfAsync(long quoteId)
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
                .FirstOrDefaultAsync(q => q.Id == quoteId);

            if (quote == null)
                throw new KeyNotFoundException($"Quote with ID {quoteId} not found.");

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
                                columns.ConstantColumn(30);
                                columns.RelativeColumn(5);
                                columns.ConstantColumn(50);
                                columns.ConstantColumn(80);
                                columns.ConstantColumn(80);
                            });

                            table.Header(header =>
                            {
                                IContainer CellStyle(IContainer container) => container.Background(primaryColor).PaddingVertical(10).PaddingHorizontal(5).DefaultTextStyle(x => x.SemiBold().FontSize(9).FontColor(Colors.White));

                                header.Cell().Element(CellStyle).Text("#");
                                header.Cell().Element(CellStyle).Text("Description");
                                header.Cell().Element(CellStyle).AlignRight().Text("Qty");
                                header.Cell().Element(CellStyle).AlignRight().Text("Unit Price");
                                header.Cell().Element(CellStyle).AlignRight().Text("Total");
                            });

                            foreach (var item in quote.QuoteItems.Select((x, i) => new { x, i }))
                            {
                                var backgroundColor = item.i % 2 == 0 ? Colors.White : Colors.Grey.Lighten5;

                                IContainer RowStyle(IContainer container) => container.Background(backgroundColor).BorderBottom(1).BorderColor(Colors.Grey.Lighten4).PaddingVertical(8).PaddingHorizontal(5);

                                table.Cell().Element(RowStyle).Text((item.i + 1).ToString());
                                table.Cell().Element(RowStyle).Column(c =>
                                {
                                    c.Item().Text(item.x.ItemDescription).SemiBold();
                                    if (item.x.Discount > 0)
                                        c.Item().Text($"Discount: {quote.Currency?.Code} {item.x.Discount:N2}").FontSize(8).Italic().FontColor(Colors.Red.Medium);
                                });
                                table.Cell().Element(RowStyle).AlignRight().Text(item.x.Quantity.ToString("N0"));
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

        public async Task<QuoteSendResult> SendQuoteEmailAsync(long quoteId, string recipientEmail, string? customSubject = null, string? customBody = null, QuoteSendOptions? options = null)
        {
            options ??= new QuoteSendOptions();

            // WP-B3 below-floor gate: recompute floors for the quote's RFQ and hold
            // the ENTIRE send when any current line price is under its floor. The
            // approve_below_floor_quote tool re-enters here with BypassFloorHold=true
            // once a manager approves, so the held send cannot re-hold itself.
            if (!options.BypassFloorHold && _belowFloorGuard is not null)
            {
                var check = await _belowFloorGuard.CheckQuoteSendAsync(quoteId, CancellationToken.None);
                if (check.IsBelowFloor)
                {
                    var approval = await _belowFloorGuard.CreateSendHoldAsync(
                        quoteId, recipientEmail, customSubject, customBody, check,
                        options.RequestedByUserId, options.RequestedBy, CancellationToken.None);
                    return QuoteSendResult.HeldForApproval(approval.Id, approval.Summary);
                }
            }

            var pdfBytes = await GenerateQuotePdfAsync(quoteId);
            var quote = await _context.Quotes
                .Include(q => q.BusinessUnit)
                .Include(q => q.Rfq)
                    .ThenInclude(r => r.Lead)
                .FirstOrDefaultAsync(q => q.Id == quoteId);

            if (quote == null) throw new KeyNotFoundException("Quote not found");

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

            await _emailService.SendEmailAsync(
                to: recipientEmail,
                subject: subject,
                body: body,
                attachments: new List<(string FileName, byte[] FileContent, string ContentType)>
                {
                    ($"Quote_{quote.QuoteNo}.pdf", pdfBytes, "application/pdf")
                },
                fromEmail: quote.Rfq?.Lead?.Clientemail ?? "",
                businessUnitId: quote.BusinessUnitId
            );

            // Mark SENT (resolved via SetupMaster; legacy id 43 fallback) and stamp
            // SentOn so the SLA engine can compute staleness / auto-expiry (WP-A4).
            quote.StatusId = await ResolveQuoteStatusIdAsync("SENT", quote.BusinessUnitId);
            quote.SentOn = DateTime.UtcNow;
            quote.ModifiedDate = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return QuoteSendResult.Sent();
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
                    UnitPrice = i.UnitPrice,
                    DiscountTypeId = i.DiscountTypeId,
                    DiscountValue = i.DiscountValue,
                    TaxAmount = i.TaxAmount,
                    DeliveryLeadTime = i.DeliveryLeadTime,
                    CreatedBy = actor,
                    CreatedDate = now
                }).ToList()
            };

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

            // All codes resolve through SetupMaster (SetupType "QuoteStatus" +
            // SetupCode, BU-scoped first) with the documented legacy id map as the
            // last-resort fallback — no more hardcoded 42/43/44/45 branches.
            var code = statusCode.ToUpperInvariant();
            long? statusId;
            switch (code)
            {
                case "DRAFT":
                case "SENT":
                case "ACCEPTED":
                case "REJECTED":
                case "EXPIRED": // seeded create-if-absent by QuoteOutcomeService (WP-A4)
                    statusId = await ResolveQuoteStatusIdAsync(code, quote.BusinessUnitId);
                    if (statusId is null)
                        throw new ArgumentException(
                            $"No '{code}' QuoteStatus is configured for this business unit.");
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
    }
}
