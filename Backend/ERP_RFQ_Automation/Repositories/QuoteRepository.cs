using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Fx;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Repositories
{
    public class QuoteRepository : IQuoteRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public QuoteRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<(IEnumerable<QuoteResponseDTO>, int TotalItems)> GetAllAsync(long businessUnitId, int pageNumber, int pageSize, string? search = null, string? state = null)
        {
            IQueryable<Quote> query = _context.Quotes
                .AsNoTracking()
                .Where(q => q.BusinessUnitId == businessUnitId)
                .Include(q => q.Customer)
                .Include(q => q.Rfq).ThenInclude(r => r.Lead)
                .Include(q => q.Status)
                .Where(q => q.Status.SetupCode != "ORDERED")
                .Include(q => q.Currency)
                .Include(q => q.BusinessUnit)
                .Include(q => q.DiscountType);

            var normalizedState = state?.Trim().ToLowerInvariant();
            if (normalizedState == "draft")
                query = query.Where(q => q.Status.SetupCode == "DRAFT" || q.Status.SetupValue.ToUpper() == "DRAFT");
            else if (normalizedState == "sent")
                query = query.Where(q => q.Status.SetupCode == "SENT" || q.Status.SetupValue.ToUpper() == "SENT");
            else if (normalizedState == "follow-up")
            {
                var followUpStaleDays = await GetStaleQuoteDaysAsync(businessUnitId);
                var cutoff = DateTime.UtcNow.AddDays(-followUpStaleDays);
                query = query.Where(q => (q.Status.SetupCode == "SENT" || q.Status.SetupValue.ToUpper() == "SENT")
                    && q.SentOn != null && q.SentOn <= cutoff && q.RespondedOn == null && q.OutcomeOn == null);
            }
            else if (normalizedState == "outcomes")
                query = query.Where(q => q.Status.SetupCode == "ACCEPTED" || q.Status.SetupCode == "REJECTED" || q.Status.SetupCode == "EXPIRED");

            if (!string.IsNullOrWhiteSpace(search))
            {
                search = search.Trim().ToLower();
                query = query.Where(q => q.QuoteNo.ToLower().Contains(search) ||
                                         (q.NexoraSerial != null && q.NexoraSerial.ToLower().Contains(search)) ||
                                         (q.Customer != null && q.Customer.Name.ToLower().Contains(search)));
            }

            var totalItems = await query.CountAsync();

            var quotes = await query
                .OrderByDescending(q => q.CreatedDate)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            // Batch load item counts for all Quotes in a single query
            var quoteIds = quotes.Select(q => q.Id).ToList();
            var itemCounts = await _context.QuoteItems
                .AsNoTracking()
                .Where(qi => quoteIds.Contains(qi.QuoteId))
                .GroupBy(qi => qi.QuoteId)
                .Select(g => new { QuoteId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.QuoteId, x => x.Count);

            // Outcome reason names (loose FK to SetupMaster) + the BU's stale
            // threshold, batch-loaded once for the page (WP-A4 / WP-A2).
            var reasonNames = await LoadReasonNamesAsync(quotes);
            var staleDays = await GetStaleQuoteDaysAsync(businessUnitId);

            var dtos = quotes.Select(q => MapToDTO(q, itemCounts.TryGetValue(q.Id, out var count) ? count : 0, reasonNames, staleDays)).ToList();

            return (dtos, totalItems);
        }

        /// <summary>Batch-resolves OutcomeReasonId -> display name for a set of quotes.</summary>
        private async Task<Dictionary<long, string>> LoadReasonNamesAsync(IEnumerable<Quote> quotes)
        {
            var reasonIds = quotes.Where(q => q.OutcomeReasonId.HasValue)
                .Select(q => q.OutcomeReasonId!.Value).Distinct().ToList();
            if (reasonIds.Count == 0) return new Dictionary<long, string>();

            return await _context.SetupMasters.AsNoTracking()
                .Where(s => reasonIds.Contains(s.SetupId))
                .ToDictionaryAsync(s => s.SetupId, s => s.Description ?? s.SetupValue);
        }

        /// <summary>The BU's configured stale threshold, or the SLA default (7 days).</summary>
        private async Task<int> GetStaleQuoteDaysAsync(long businessUnitId)
        {
            return await _context.Set<ERP_RFQ_Automation.Sla.SlaPolicy>().AsNoTracking()
                .Where(p => p.BusinessUnitId == businessUnitId)
                .Select(p => (int?)p.StaleQuoteDays)
                .FirstOrDefaultAsync()
                ?? ERP_RFQ_Automation.Sla.SlaPolicy.Default(businessUnitId).StaleQuoteDays;
        }

        public async Task<QuoteResponseDTO> GetByIdAsync(long id, long businessUnitId)
        {
            var quote = await _context.Quotes
                .AsNoTracking()
                .Include(q => q.Customer)
                .Include(q => q.Rfq).ThenInclude(r => r.Lead)
                .Include(q => q.Status)
                .Include(q => q.Currency)
                .Include(q => q.DiscountType)
                .Include(q => q.QuoteItems).ThenInclude(qi => qi.Product)
                .Include(q => q.QuoteItems).ThenInclude(qi => qi.DiscountType)
                .FirstOrDefaultAsync(q => q.Id == id && q.BusinessUnitId == businessUnitId);

            if (quote == null)
                throw new KeyNotFoundException($"Quote with ID {id} not found.");

            var reasonNames = await LoadReasonNamesAsync(new[] { quote });
            var staleDays = await GetStaleQuoteDaysAsync(businessUnitId);

            var dto = MapToDTO(quote, -1, reasonNames, staleDays); // -1 indicates detail view, load all items
            dto.RevisionImpact = await _context.Set<ERP_RFQ_Automation.LeadIdentity.LeadRevisionImpact>()
                .AsNoTracking()
                .Where(impact => impact.BusinessUnitId == businessUnitId && impact.AggregateType == "QUOTE" && impact.AggregateId == id)
                .Where(impact => impact.Status == "OPEN" && impact.ResolvedAtUtc == null)
                .Where(impact => !_context.Set<ERP_RFQ_Automation.LeadIdentity.LeadIdentityAuditEvent>()
                    .Any(audit => audit.BusinessUnitId == businessUnitId
                        && audit.EventType == "REVISION_IMPACT_RESOLVED"
                        && audit.CorrelationId == "quote-impact:" + impact.Id))
                .OrderByDescending(impact => impact.Id)
                .Select(impact => impact.ImpactType)
                .FirstOrDefaultAsync();
            return dto;
        }

        // ... methods AddAsync, UpdateAsync, DeleteAsync skipped (unchanged) ...

        private QuoteResponseDTO MapToDTO(Quote q, int itemCount = -1, Dictionary<long, string>? reasonNames = null, int staleQuoteDays = 7)
        {
            return new QuoteResponseDTO
            {
                Id = q.Id,
                QuoteNo = q.QuoteNo,
                RfqId = q.Rfqid,
                RfqNo = q.Rfq?.Rfqno,
                LeadId = q.Rfq?.LeadId,
                SourceLeadRevision = q.SourceLeadRevision,
                SourceRfqRevision = q.SourceRfqRevision,
                CommercialCaseId = q.CommercialCaseId ?? q.Rfq?.CommercialCaseId ?? q.Rfq?.Lead?.CommercialCaseId,
                NexoraSerial = q.NexoraSerial ?? q.Rfq?.NexoraSerial ?? q.Rfq?.Lead?.CommercialCaseReference,
                ContactId = q.ContactId ?? q.Rfq?.ContactId,
                LifecycleVersion = q.LifecycleVersion,
                Version = q.RevisionNo,
                CustomerId = q.CustomerId,
                CustomerName = q.Customer?.Name,
                CustomerEmail = q.Customer?.ContactEmail,
                BusinessUnitId = q.BusinessUnitId,
                BusinessUnitName = q.BusinessUnit?.BusinessUnitName,
                QuoteDate = q.QuoteDate,
                ValidUntil = q.ValidUntil,
                StatusId = q.StatusId,
                StatusValue = q.Status?.SetupValue,
                StatusCode = q.Status?.SetupCode,
                SentOn = q.SentOn,
                RespondedOn = q.RespondedOn,
                OutcomeOn = q.OutcomeOn,
                OutcomeReasonId = q.OutcomeReasonId,
                OutcomeReasonName = q.OutcomeReasonId.HasValue && reasonNames != null && reasonNames.TryGetValue(q.OutcomeReasonId.Value, out var rn) ? rn : null,
                OutcomeNote = q.OutcomeNote,
                IsStale = ERP_RFQ_Automation.Sla.SlaComputed.IsStale(q.Status?.SetupCode, q.SentOn, q.RespondedOn, staleQuoteDays),
                DaysSinceSent = ERP_RFQ_Automation.Sla.SlaComputed.DaysSinceSent(q.SentOn),
                CurrencyId = q.CurrencyId,
                CurrencyCode = q.Currency?.Code,
                TotalAmount = q.TotalAmount,
                HeaderRemarks = q.HeaderRemarks,
                CreatedBy = q.CreatedBy,
                CreatedDate = q.CreatedDate,
                ModifiedBy = q.ModifiedBy,
                ModifiedDate = q.ModifiedDate,
                DiscountTypeId = q.DiscountTypeId,
                DiscountTypeName = q.DiscountType?.Description, // or SetupName/SetupValue
                DiscountValue = q.DiscountValue,
                ItemCount = itemCount >= 0 ? itemCount : q.QuoteItems?.Count ?? 0,
                QuoteItems = itemCount >= 0 ? new List<QuoteItemResponseDTO>() : QuoteService.OrderQuoteLines(q.QuoteItems).Select(i => new QuoteItemResponseDTO
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
                    Discount = i.Discount,
                    TaxAmount = i.TaxAmount,
                    DeliveryLeadTime = i.DeliveryLeadTime,
                    DiscountTypeId = i.DiscountTypeId,
                    DiscountTypeName = i.DiscountType?.Description,
                    DiscountValue = i.DiscountValue
                }).ToList()
            };
        }

        public async Task AddAsync(Quote quote)
        {
            // Calculate totals
            foreach (var item in quote.QuoteItems)
            {
                item.TotalAmount = (item.Quantity * item.UnitPrice) - (item.Discount ?? 0) + (item.TaxAmount ?? 0);
            }
            quote.TotalAmount = quote.QuoteItems.Sum(i => i.TotalAmount);
            quote.FinancialCalculationVersion = 2;
            quote.CreatedDate = DateTime.UtcNow;

            _context.Quotes.Add(quote);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateAsync(Quote quote)
        {
            // SEC-14: scope the update lookup to the quote's business unit so a write can never
            // resolve another tenant's row even if the caller's BU guard is bypassed.
            var existing = await _context.Quotes
                .Include(q => q.QuoteItems)
                .FirstOrDefaultAsync(q => q.Id == quote.Id && q.BusinessUnitId == quote.BusinessUnitId);

            if (existing == null)
                throw new KeyNotFoundException($"Quote with ID {quote.Id} not found.");

            // Update Header
            existing.QuoteNo = quote.QuoteNo;
            if (existing.CustomerId != quote.CustomerId
                || quote.ContactId.HasValue && existing.ContactId != quote.ContactId
                || quote.CommercialCaseId.HasValue && existing.CommercialCaseId != quote.CommercialCaseId
                || quote.NexoraSerial != null && existing.NexoraSerial != quote.NexoraSerial)
                throw new InvalidOperationException("Quote commercial identity cannot be changed by an ordinary update.");
            existing.QuoteDate = quote.QuoteDate;
            existing.ValidUntil = quote.ValidUntil;
            if (existing.StatusId != quote.StatusId)
                throw new InvalidOperationException("Quote status changes require the governed lifecycle service.");
            existing.CurrencyId = quote.CurrencyId;
            existing.HeaderRemarks = quote.HeaderRemarks;
            existing.ModifiedBy = quote.ModifiedBy;
            existing.ModifiedDate = DateTime.UtcNow;

            // Update Items logic handled via Controller or specific Item management methods
            // For simplicity in this base update, strict replacement or specific item management is needed
            // Here assuming Controller manages the list merging, or we implement full merge here

            // Full merge logic for items:
            // 1. Remove items not in new list
            // 2. Add new items
            // 3. Update existing items

            // NOTE: Implementing naive replacement for now, controller should handle refined logic or passing correct object graph
            _context.QuoteItems.RemoveRange(existing.QuoteItems);
            foreach (var item in quote.QuoteItems)
            {
                item.QuoteId = existing.Id; // Ensure link
                _context.QuoteItems.Add(item);
            }

            // Recalculate Total
            foreach (var item in quote.QuoteItems)
            {
                item.TotalAmount = (item.Quantity * item.UnitPrice) - (item.Discount ?? 0) + (item.TaxAmount ?? 0);
            }
            existing.TotalAmount = quote.QuoteItems.Sum(i => i.TotalAmount);
            existing.FinancialCalculationVersion = 2;

            await _context.SaveChangesAsync();
        }

        public async Task DeleteAsync(long id, long businessUnitId)
        {
            var quote = await _context.Quotes.FirstOrDefaultAsync(q => q.Id == id && q.BusinessUnitId == businessUnitId);
            if (quote != null)
            {
                _context.Quotes.Remove(quote);
                await _context.SaveChangesAsync();
            }
        }

        public async Task<QuoteStatsDTO> GetQuoteStatsAsync(long businessUnitId)
        {
            var quotes = await _context.Quotes
                .AsNoTracking()
                .Where(q => q.BusinessUnitId == businessUnitId)
                .Select(q => new { q.Id, q.StatusId, q.ValidUntil, q.TotalAmount, q.CurrencyId })
                .ToListAsync();

            var now = DateTime.UtcNow;

            // WP-B2 fix: these counts previously used bare magic ids 31/32, which do
            // NOT match the seeded QuoteStatus rows (DRAFT=42, SENT=43, ACCEPTED=44,
            // REJECTED=45 — see QuoteService.LegacyQuoteStatusIds), so both stats were
            // silently 0. Statuses are now resolved by SetupType "QuoteStatus" +
            // SetupCode, with the documented legacy ids as fallback — never magic ids.
            var acceptedStatusIds = await GetQuoteStatusIdsAsync("ACCEPTED", legacyId: 44);
            var pendingStatusIds = await GetQuoteStatusIdsAsync("SENT", legacyId: 43); // pending = sent, awaiting the customer

            // FX fix: this used to be `quotes.Sum(q => q.TotalAmount ?? 0)`, which added AED, USD
            // and EUR quote totals together as bare decimals and reported the result as one
            // number. Quote.TotalAmount is denominated by Quote.CurrencyId, so that sum was only
            // ever correct for a single-currency tenant. Amounts are now converted into the
            // business unit's base currency using approved, effective-dated rates, and the total
            // FAILS CLOSED (null + a surfaced reason + a per-currency breakdown) when any quote's
            // currency has no approved rate — matching the Procurement read-path philosophy of
            // never emitting an uncomparable figure.
            var fx = new FxConversionService(_context);
            var total = await fx.TotalAsync(
                businessUnitId,
                quotes.Select(q => new FxAmount(q.TotalAmount ?? 0m, q.CurrencyId)).ToArray(),
                now);

            return new QuoteStatsDTO
            {
                TotalQuotes = quotes.Count,
                AcceptedQuotes = quotes.Count(q => q.StatusId.HasValue && acceptedStatusIds.Contains(q.StatusId.Value)),
                PendingQuotes = quotes.Count(q => q.StatusId.HasValue && pendingStatusIds.Contains(q.StatusId.Value)),
                ExpiredQuotes = quotes.Count(q => q.ValidUntil.HasValue && q.ValidUntil.Value < now),
                TotalQuotedAmount = total.Total,
                TotalQuotedAmountCurrency = total.TargetCurrencyCode,
                TotalQuotedAmountConverted = total.Converted,
                TotalQuotedAmountUnavailableReason = total.UnavailableReason,
                QuotedAmountsByCurrency = total.Components
                    .Select(c => new QuoteStatsCurrencyBreakdownDTO
                    {
                        CurrencyId = c.CurrencyId,
                        CurrencyCode = c.CurrencyCode,
                        Subtotal = c.Subtotal,
                        QuoteCount = c.RowCount,
                        Converted = c.Converted,
                        ExchangeRate = c.Rate,
                        ConvertedSubtotal = c.ConvertedSubtotal,
                        Reason = c.Reason
                    })
                    .ToList()
            };
        }

        /// <summary>
        /// All SetupMaster ids carrying this QuoteStatus code (any BU) + the documented
        /// legacy fallback id, so pre-SetupMaster tenants are still counted correctly —
        /// the same pattern as SlaSweepWorker.GetStatusIdsAsync.
        /// </summary>
        private async Task<List<long>> GetQuoteStatusIdsAsync(string code, long legacyId)
        {
            var ids = await _context.SetupMasters.AsNoTracking()
                .Where(s => s.SetupType == "QuoteStatus" && s.SetupCode == code)
                .Select(s => s.SetupId)
                .ToListAsync();
            if (!ids.Contains(legacyId)) ids.Add(legacyId);
            return ids;
        }
    }
}
