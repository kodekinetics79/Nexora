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
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.CommercialCases.Participation;
using ERP_RFQ_Automation.CommercialCases.Promotion;
using ERP_RFQ_Automation.LeadIdentity;

namespace ERP_RFQ_Automation.Services
{
    /// <summary>Outcome of one tenant's delivered-quote catch-up: quotes marked SENT, and
    /// quotes whose status update failed and was deferred on the ledger row.</summary>
    public sealed record DeliveredQuoteReconciliation(int Finalized, int Deferred);

    public interface IQuoteService
    {
        /// <summary>
        /// Renders the customer-facing quotation PDF.
        ///
        /// <para>R5 GATE. The PDF <i>is</i> the commercial document — once it exists it can be
        /// downloaded, forwarded and relied on — so it is produced only when the quote's CURRENT
        /// prices are covered by a recorded price attestation. Fails closed with
        /// <see cref="ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationRequiredException"/>
        /// otherwise; the message is the rep-facing reason and is safe to show verbatim.</para>
        /// </summary>
        /// <param name="boundAttestationFingerprint">
        /// When supplied (the quote-delivery path), the price fingerprint this render was
        /// AUTHORISED for. The document is refused unless the quote's prices still hash to it —
        /// closing the time-of-check/time-of-use window between queueing an email and sending it.
        /// Null means "no earlier authorisation to bind to"; the attestation must still be valid.
        /// </param>
        Task<byte[]> GenerateQuotePdfAsync(long quoteId, long businessUnitId,
            string? boundAttestationFingerprint = null, CancellationToken ct = default);

        /// <summary>
        /// Transactionally queues durable PDF delivery — unless a line is priced
        /// below its pricing-engine floor (WP-B3), in which case NOTHING is sent:
        /// the send is parked as a pending approve_below_floor_quote approval and
        /// the result says so. options.BypassFloorHold (approved-hold execution
        /// only) skips the check.
        /// </summary>
        Task<QuoteSendResult> SendQuoteEmailAsync(long quoteId, long businessUnitId, string recipientEmail, string? customSubject = null, string? customBody = null, QuoteSendOptions? options = null);

        /// <summary>
        /// Everything that will refuse this quote's send, BEFORE the rep opens the send dialog.
        /// See <see cref="QuoteService.EvaluateSendReadinessAsync"/>.
        /// </summary>
        Task<QuoteSendReadinessDTO> EvaluateSendReadinessAsync(long quoteId, long businessUnitId, CancellationToken ct = default);
        Task FinalizeQuoteDeliveryAsync(long quoteId, long businessUnitId, CancellationToken ct = default);

        /// <summary>
        /// Marks SENT every quote of the tenant whose delivery ledger row is sealed
        /// (<c>CompletedOn</c> set: the provider accepted the message) but whose
        /// <c>SentOn</c> is still null. Never sends anything. A quote that cannot be marked
        /// is deferred on its ledger row (<c>AvailableOn</c>, <c>LastErrorCode</c>) and retried.
        /// </summary>
        Task<DeliveredQuoteReconciliation> ReconcileDeliveredQuotesAsync(long businessUnitId, CancellationToken ct = default);
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

        /// <summary>
        /// R7: moves an already-issued quote's validity date out, with a mandatory reason that is
        /// recorded as an auditable row (<c>QuoteValidityExtensions</c>), NOT as a log line.
        ///
        /// <para>The commercial offer is unchanged, so the revision number is deliberately not
        /// touched: a buyer who asks "can you hold your price two more weeks" must not receive
        /// something that reads as a new offer. Prices, currency, lines and status are untouched
        /// too — only the expiry moves.</para>
        ///
        /// <para>Replay-safe on <paramref name="idempotencyKey"/> and serialised per quote with
        /// the same advisory-lock + row-lock pattern <see cref="ReviseQuoteAsync"/> uses.</para>
        /// </summary>
        /// <exception cref="KeyNotFoundException">Quote not found in this tenant.</exception>
        /// <exception cref="ArgumentException">Blank/over-long reason, or a date that is not an extension.</exception>
        /// <exception cref="InvalidOperationException">Quote is a draft, superseded, or already closed (→ 409).</exception>
        Task<QuoteValidityExtensionResultDTO> ExtendQuoteValidityAsync(
            long quoteId, long businessUnitId, DateTime newValidUntil, string reason,
            string actor, long? actorUserId, string idempotencyKey, CancellationToken ct = default);

        /// <summary>Every recorded validity move on one quote, newest first (R7 "visible").</summary>
        Task<IReadOnlyList<QuoteValidityExtensionDTO>> GetValidityExtensionsAsync(
            long quoteId, long businessUnitId, CancellationToken ct = default);
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
        private readonly Microsoft.Extensions.Logging.ILogger<QuoteService>? _logger;
        private readonly ERP_RFQ_Automation.Notifications.Runtime.IOutboundSenderResolver? _outboundSenders;

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
            CommercialLearningService? commercialLearning = null,
            ERP_RFQ_Automation.Notifications.Runtime.IOutboundSenderResolver? outboundSenders = null,
            // Optional and last, so every existing caller and fixture keeps compiling unchanged.
            // It exists for one reason: a sent quote that cannot be chased must say so.
            Microsoft.Extensions.Logging.ILogger<QuoteService>? logger = null)
        {
            _logger = logger;
            _context = context;
            _emailService = emailService;
            _quoteConfigRepository = quoteConfigRepository;
            _belowFloorGuard = belowFloorGuard;
            _lifecycle = lifecycle;
            _sales = sales;
            _lineResolution = lineResolution;
            _commercialLearning = commercialLearning;
            _outboundSenders = outboundSenders;
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

        /// <summary>
        /// Attribution for a created quote. The controller stamps this from the validated token
        /// before the request ever reaches here, so the fallback is a belt-and-braces default
        /// rather than an expected path — but the DTO no longer forces a value, and a service
        /// should not depend on a caller having remembered. Never a blank string: Quote.CreatedBy
        /// is non-nullable, and an empty actor is a worse audit record than a named default.
        /// </summary>
        private static string Actor(string? candidate)
            => string.IsNullOrWhiteSpace(candidate) ? "System" : candidate.Trim();

        public async Task<QuoteResponseDTO> CreateQuoteAsync(QuoteCreateRequestDTO request)
        {
            var quoteNo = request.QuoteNo;
            if (string.IsNullOrWhiteSpace(quoteNo))
            {
                quoteNo = await GenerateNextQuoteNumber(request.BusinessUnitId);
            }

            // FIN-12: reject non-positive quantity/price up front. R19: and an unusable tax category.
            foreach (var itemDto in request.QuoteItems)
                ValidateQuoteItemFinancials(itemDto.Quantity, itemDto.UnitPrice,
                    itemDto.TaxCategory, itemDto.TaxCategoryReason);

            Rfq? sourceRfq = null;
            if (request.RfqId is > 0)
            {
                sourceRfq = await _context.Rfqs
                    .SingleOrDefaultAsync(item => item.Id == request.RfqId.Value
                        && item.BusinessUnitId == request.BusinessUnitId)
                    ?? throw new ArgumentException("The selected RFQ was not found in this tenant.");
                if (!sourceRfq.CommercialCaseId.HasValue || string.IsNullOrWhiteSpace(sourceRfq.NexoraSerial))
                    throw new InvalidOperationException(
                        "The selected RFQ is not linked to a commercial case, so a quote cannot inherit governed lineage from it.");
            }

            var quote = new Quote
            {
                QuoteNo = quoteNo,
                // Back-fill carries the customer's own number and marks its origin; both are
                // null/PIPELINE for a quote this system produced, so the default path is unchanged.
                ExternalQuoteReference = string.IsNullOrWhiteSpace(request.ExternalQuoteReference)
                    ? null : request.ExternalQuoteReference.Trim(),
                Origin = Models.QuoteOrigin.IsKnown(request.Origin)
                    ? request.Origin! : Models.QuoteOrigin.Pipeline,
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
                CreatedBy = Actor(request.CreatedBy),
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
                    // R17: TaxAmount is NOT copied from the request. CalculateQuoteTotals below
                    // derives it from the tenant's rate and the category the caller stated.
                    TaxCategory = QuoteLineTaxCategories.Normalize(i.TaxCategory),
                    TaxCategoryReason = i.TaxCategoryReason?.Trim(),
                    DeliveryLeadTime = i.DeliveryLeadTime,
                    CreatedBy = Actor(request.CreatedBy),
                    CreatedDate = DateTime.UtcNow
                }).ToList()
            };

            // The controller validates access and customer consistency; the service owns the
            // atomic persistence invariant. Every RFQ-origin quote therefore receives the case
            // and Nexora Serial before the first INSERT, including backfill callers and any future
            // application-service caller that bypasses the HTTP controller.
            if (sourceRfq is not null) quote.InheritCommercialIdentity(sourceRfq);

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

                // A DRAFT is blocked only by NO_QUOTE_REVIEW — no lines at all, or a deadline
                // already past. It is deliberately NOT blocked by ACTIONABLE_WITH_BLOCKERS.
                //
                // This gate previously demanded VIABLE_READY, which requires zero unfulfilled
                // demand: full stock or an approved supplier offer for every quoted line. That
                // contradicts what this method builds three lines below — a draft with
                // UnitPrice 0, no currency, no validity and the header remark "Commercial Review
                // Required: pricing, inventory, lead time, tax, freight and validity remain
                // pending". The draft exists precisely to hold work that is not yet resolved, so
                // requiring it to be resolved first made the draft unreachable for any line
                // needing sourcing — the normal case. Supply coverage is a condition of quote
                // RELEASE, not of starting one.
                //
                // Identity integrity is not waived: customer, canonical Lead and Nexora Serial
                // are each checked explicitly above and below this block.
                if (intelligence.CommercialDecision == "NO_QUOTE_REVIEW")
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

                // Lead-origin RFQs already crossed the sole participation gate: an immutable,
                // committed LeadParticipationDecision and its promotion receipt. Do not require
                // (or trust) a second mutable RFQ-line decision, because that would let the quote
                // silently diverge from the approved revision. Legacy/manual RFQs that predate the
                // governed spine retain their explicit RFQ-line participation fallback.
                Rfqitem[] markedForQuote;
                var isGovernedLeadOrigin = rfq.PromotionId.HasValue || rfq.ParticipationDecisionId.HasValue ||
                    rfq.SourceLeadRevisionId.HasValue;
                if (isGovernedLeadOrigin)
                {
                    if (!rfq.PromotionId.HasValue || !rfq.ParticipationDecisionId.HasValue ||
                        !rfq.SourceLeadRevisionId.HasValue)
                        throw new InvalidOperationException(
                            "The governed RFQ has incomplete promotion lineage. Reconcile its immutable Lead decision before preparing a Quote Draft.");

                    var receipt = await _context.Set<RfqPromotion>().AsNoTracking()
                        .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId
                            && x.Id == rfq.PromotionId.Value
                            && x.LeadId == rfq.LeadId
                            && x.LeadRevisionId == rfq.SourceLeadRevisionId.Value
                            && x.ParticipationDecisionId == rfq.ParticipationDecisionId.Value, ct)
                        ?? throw new InvalidOperationException(
                            "The governed RFQ promotion receipt could not be verified.");
                    var decision = await _context.Set<LeadParticipationDecision>().AsNoTracking()
                        .Include(x => x.Lines)
                        .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId
                            && x.Id == receipt.ParticipationDecisionId
                            && x.LeadId == receipt.LeadId
                            && x.LeadRevisionId == receipt.LeadRevisionId, ct)
                        ?? throw new InvalidOperationException(
                            "The governed RFQ participation decision could not be verified.");
                    if (!decision.IsCommitted)
                        throw new InvalidOperationException(
                            "The governed RFQ participation decision is not committed.");

                    var approvedRevisionLines = decision.Lines
                        .Where(x => x.Choice == LeadLineParticipationChoice.Bid)
                        .Select(x => x.LeadItemRevisionId).ToHashSet();
                    if (approvedRevisionLines.Count == 0)
                        throw new InvalidOperationException(
                            "The committed Lead participation decision contains no approved lines for quotation.");
                    if (rfq.Rfqitems.Any(x => !x.SourceLeadItemRevisionId.HasValue ||
                            !approvedRevisionLines.Contains(x.SourceLeadItemRevisionId.Value)) ||
                        rfq.Rfqitems.Select(x => x.SourceLeadItemRevisionId!.Value).Distinct().Count()
                            != approvedRevisionLines.Count)
                        throw new InvalidOperationException(
                            "The RFQ line scope no longer matches its immutable approved Lead lines.");
                    markedForQuote = rfq.Rfqitems
                        .Where(x => x.SourceLeadItemRevisionId.HasValue &&
                            approvedRevisionLines.Contains(x.SourceLeadItemRevisionId.Value))
                        .ToArray();
                }
                else
                {
                    markedForQuote = rfq.Rfqitems.Where(item => item.IsMarkedForQuote).ToArray();
                }
                if (markedForQuote.Length == 0)
                    throw new InvalidOperationException(
                        isGovernedLeadOrigin
                            ? "Approve at least one line through the Lead participation decision before preparing a Customer Quote Draft."
                            : "Mark at least one RFQ line as Quote before preparing a Customer Quote Draft.");

                // Only the lines being quoted must be complete. A line we are declining is
                // allowed to be missing a part number — that is frequently WHY it is declined.
                var invalidLines = markedForQuote
                    .Where(item => item.Quantity <= 0
                        || string.IsNullOrWhiteSpace(item.UnitOfMeasure)
                        || string.IsNullOrWhiteSpace(item.ItemMaterialCode)
                           && string.IsNullOrWhiteSpace(item.ManufacturerPartNumber)
                           && string.IsNullOrWhiteSpace(item.ProductShortDescription))
                    .Select(item => string.IsNullOrWhiteSpace(item.LineItemNo) ? $"line {item.Id}" : $"line {item.LineItemNo}")
                    .ToArray();
                if (invalidLines.Length > 0)
                    throw new InvalidOperationException($"Review required request data for {string.Join(", ", invalidLines)} before preparing a Quote Draft.");

                // The LATEST quote on this RFQ, not the only one.
                //
                // A revision is a new Quote row carrying the SAME Rfqid (see CreateRevisionAsync),
                // which FR-QTM-08 requires. This read used SingleOrDefaultAsync on that column, so
                // the first revision made preparing a draft for that RFQ throw "Sequence contains
                // more than one element" — permanently, for the life of the inquiry. The revision
                // feature and this read could not both work.
                var existing = await _context.Quotes
                    .Include(item => item.Status)
                    .Where(item => item.Rfqid == rfqId && item.BusinessUnitId == businessUnitId)
                    .OrderByDescending(item => item.RevisionNo)
                    .ThenByDescending(item => item.Id)
                    .FirstOrDefaultAsync(ct);
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
                    QuoteNo = await GenerateNextQuoteNumber(businessUnitId),
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
                    QuoteItems = markedForQuote.OrderBy(item => item.Id).Select(item => new QuoteItem
                    {
                        RfqitemId = item.Id,
                        ProductId = item.ProductId,
                        ItemDescription = item.ProductShortDescription ?? item.ProductShortName ?? item.ItemText ?? item.ItemMaterialCode,
                        Quantity = item.Quantity!.Value,
                        // The draft gate above refuses a blank UnitOfMeasure — keep what it
                        // validated instead of throwing it away, and carry the buyer's own
                        // line number so the printed quote can echo their reference back.
                        UnitOfMeasure = item.UnitOfMeasure,
                        CustomerLineRef = item.LineItemNo,
                        UnitPrice = 0m,
                        TotalAmount = 0m,
                        // R17/R19: an unpriced draft line has nothing to derive tax FROM, so both
                        // the amount and the applied rate stay null and the send gate refuses the
                        // quote until it has been priced. The treatment defaults to a domestic
                        // standard-rated sale; the rep changes it on the lines that are not.
                        TaxAmount = null,
                        TaxCategory = QuoteLineTaxCategories.Standard,
                        TaxRatePercentApplied = null,
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

        /// <summary>
        /// Format: QT-MMYY-0001, allocated within one tenant.
        ///
        /// <para><b>Known limitation, deliberately left visible.</b> This is still read-max-plus-one
        /// and is therefore not concurrency-safe on its own. It is now backstopped by the unique
        /// index <c>UX_Quotes_BusinessUnitID_QuoteNo</c>, so a collision fails loudly instead of
        /// producing two customer documents bearing the same number. Replacing all three
        /// generators with a single row-locked allocator (the <c>LegalDocumentCounters</c> pattern
        /// already used by finance) is tracked as separate work — doing it here, untested, would
        /// be the more dangerous change.</para>
        ///
        /// <para>The tenant scope is the repair: this previously filtered on prefix ALONE, so
        /// tenant B's quotes advanced tenant A's counter and every tenant could see how many
        /// quotes the others had issued.</para>
        /// </summary>
        private async Task<string> GenerateNextQuoteNumber(long businessUnitId)
        {
            var now = DateTime.UtcNow;
            var prefix = $"QT-{now:MM}{now:yy}-";

            // Get the last quote number with this prefix, WITHIN THIS TENANT.
            var lastQuote = await _context.Quotes
                .Where(q => q.BusinessUnitId == businessUnitId && q.QuoteNo.StartsWith(prefix))
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

            // R5, prevention half. "Send" only ENQUEUES: the quote stays DRAFT — and therefore
            // editable by the check above — until the worker has actually emailed it. That window
            // is a time-of-check/time-of-use hole: a price edited inside it would be attested
            // under one set of numbers and delivered under another. The dispatcher detects that
            // and fails closed, but detection alone means the customer's quote silently never
            // arrives; refusing the edit keeps the rep in control of the outcome.
            var deliveries = await _context.QuoteDeliveryRequests.AsNoTracking().IgnoreQueryFilters()
                .Where(x => x.BusinessUnitId == quote.BusinessUnitId && x.QuoteId == quote.Id)
                .Select(x => new { x.CompletedOn, x.DeadLetteredOn, x.LastErrorCode })
                .ToListAsync();
            // The delivery ledger, not the status flag, is the authority on whether the customer
            // holds this quote. A sealed row means the provider accepted the message even if
            // SentOn has not caught up yet; an UNCERTAIN row means the customer may hold it.
            // Editing either would diverge the stored figures from a document that is, or may
            // be, in the customer's inbox.
            if (deliveries.Any(x => x.CompletedOn != null))
                throw new InvalidOperationException(
                    $"Quote '{quote.QuoteNo}' has been delivered to the customer and can no longer be edited. " +
                    "Create a new revision instead.");
            if (deliveries.Any(x => x.CompletedOn == null && x.DeadLetteredOn == null))
                throw new InvalidOperationException(
                    $"Quote '{quote.QuoteNo}' is queued for delivery to the customer and its prices are locked " +
                    "to the price source you confirmed. Wait for the delivery to complete, then create a new " +
                    "revision if the figures still need to change.");
            if (deliveries.Any(x => x.DeadLetteredOn != null && x.LastErrorCode != null
                    && x.LastErrorCode.StartsWith("DeliveryOutcomeUncertain", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"Quote '{quote.QuoteNo}' may already have reached the customer: its delivery was interrupted " +
                    "and never confirmed either way. It cannot be edited. Check with the customer; if the quote " +
                    "did not arrive, create a new revision and send that.");

            quote.QuoteNo = request.QuoteNo;
            quote.CustomerId = request.CustomerId;
            quote.QuoteDate = request.QuoteDate;
            quote.ValidUntil = request.ValidUntil;
            if (request.StatusId != quote.StatusId)
                throw new InvalidOperationException("Quote status changes require the governed lifecycle endpoint.");
            // A quote's currency is not editable from any screen in this product, so an absent
            // CurrencyId means "not supplied", never "set it to nothing". Assigning it
            // unconditionally silently ERASED the currency on every save from the Edit screen,
            // whose payload has never carried the field. The consequences are both bad and both
            // silent: a DRAFT then fails PDF export on a currency field the Edit screen does not
            // show, and a non-draft prints on the customer's document under the `?? "USD"`
            // fallback further down this file — a 3.75x misstatement of price on a SAR quote,
            // on our letterhead, with our VAT number on it.
            //
            // Changing a quote's currency mid-quote is not a journey this product has. If one is
            // ever added it must be an explicit, audited transition, not a side effect of Save.
            if (request.CurrencyId.HasValue)
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
                    ValidateQuoteItemFinancials(itemDto.Quantity, itemDto.UnitPrice,
                        itemDto.TaxCategory, itemDto.TaxCategoryReason);

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
                            // R17: the submitted TaxAmount is discarded — CalculateQuoteTotals
                            // re-derives it below from the price this edit just set. R19: the
                            // category is the caller's to state, preserved when not supplied so an
                            // older client cannot silently reset an export back to standard-rated.
                            existingItem.TaxCategory = itemDto.TaxCategory is null
                                ? existingItem.TaxCategory
                                : QuoteLineTaxCategories.Normalize(itemDto.TaxCategory);
                            existingItem.TaxCategoryReason = itemDto.TaxCategory is null
                                ? existingItem.TaxCategoryReason
                                : itemDto.TaxCategoryReason?.Trim();
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
                        // R17: derived below, never submitted.
                        TaxCategory = QuoteLineTaxCategories.Normalize(itemDto.TaxCategory),
                        TaxCategoryReason = itemDto.TaxCategoryReason?.Trim(),
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

        /// <summary>
        /// Recomputes every line total, every line's OUTPUT TAX, and the quote total.
        ///
        /// <para>R17: this is the one place a customer quote line's tax is set. It is derived —
        /// <c>taxable base x the tenant's OutputTaxRatePercent</c>, or zero when the user marked the
        /// line as anything other than standard-rated — and never read from the request. Deriving it
        /// HERE rather than at each call site is deliberate: the taxable base is the line net
        /// AFTER the line discount is resolved, and this method is the only code that knows whether
        /// a discount was a percentage or a fixed amount. Any other placement would tax the wrong
        /// base the first time someone discounts a line.</para>
        ///
        /// <para>When the tenant has no output tax rate configured, a standard-rated line's tax is
        /// left UNDERIVED — <c>TaxRatePercentApplied</c> stays null — and the send gate refuses the
        /// quote. Writing zero instead would be the original defect wearing a new hat.</para>
        /// </summary>
        private async Task CalculateQuoteTotals(Quote quote)
        {
            // One policy read per recalculation, so every line of one quote is taxed on one answer.
            // Absence of a policy row means the entity's defaults (see CommercialMatchingPolicy),
            // which for the KSA home jurisdiction is the 15% standard rate.
            var outputTaxRatePercent = await _context.ResolveOutputTaxRatePercentAsync(quote.BusinessUnitId);

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

            // ---------------------------------------------------------------- pass 1: line nets
            // Every line's net BEFORE the header discount, which is the weight the header discount
            // is allocated by and the base the header percentage is taken on. Tax is NOT derived
            // yet: it cannot be, because the taxable base is not known until the header discount
            // has been shared out below.
            var lineNets = new decimal[quote.QuoteItems.Count];
            var items = quote.QuoteItems.ToList();
            decimal netSubTotal = 0;

            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];

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

                // A discount larger than the line is a data error, not a negative supply.
                if (itemDiscountAmount > itemTotal) itemDiscountAmount = itemTotal;

                // 'Discount' is the resolved AMOUNT; 'DiscountValue' is the input the user typed
                // (10 meaning 10%). Only the line's own discount lives here — the header's share is
                // kept separately in HeaderDiscountAllocated so the two never merge into one
                // unattributable number.
                item.Discount = itemDiscountAmount;

                lineNets[index] = itemTotal - itemDiscountAmount;
                netSubTotal += lineNets[index];
            }

            // ------------------------------------------------------- header discount, on the NET
            // Taken on the tax-EXCLUSIVE subtotal. It used to be taken on a subtotal that already
            // included each line's tax, which quietly made the rep's "10%" worth 11.5% and put the
            // create screen and the server 150.00 apart on a 10,000.00 quote.
            decimal quoteDiscountAmount = 0;
            if (quote.DiscountTypeId.HasValue && quote.DiscountValue.HasValue && discountTypes.ContainsKey(quote.DiscountTypeId.Value))
            {
                string code = discountTypes[quote.DiscountTypeId.Value].ToUpper();
                if (code == "PERCENTAGE")
                {
                    quoteDiscountAmount = netSubTotal * (quote.DiscountValue.Value / 100);
                }
                else if (code == "FIXED")
                {
                    quoteDiscountAmount = quote.DiscountValue.Value;
                }
            }

            quoteDiscountAmount = RoundCurrency(quoteDiscountAmount);
            if (quoteDiscountAmount > netSubTotal) quoteDiscountAmount = netSubTotal;
            if (quoteDiscountAmount < 0) quoteDiscountAmount = 0;

            // ------------------------------------------------------------------- allocation
            // Share the header discount across lines in proportion to their net, using largest
            // remainder so the shares sum EXACTLY to the header discount. Rounding each share
            // independently would leave a residual that shows up as the printed total disagreeing
            // with the sum of the printed lines.
            var allocations = AllocateProRata(quoteDiscountAmount, lineNets);

            // ----------------------------------------------------- pass 2: taxable base and tax
            decimal quoteNetTotal = 0;
            decimal quoteTaxTotal = 0;

            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                var allocated = allocations[index];
                item.HeaderDiscountAllocated = allocated;

                // R17/R19: derive the line's output tax from the net consideration the customer
                // actually pays for this line — after its own discount AND its share of the header
                // discount. Deriving it before the header discount overstated the VAT on every
                // discounted quote, and VAT stated on a document is VAT that is owed.
                var taxCategory = QuoteLineTaxCategories.Normalize(item.TaxCategory);
                item.TaxCategory = taxCategory;
                var taxableBase = OutputTaxFormula.TaxableBase(lineNets[index], allocated);
                var derivedTax = OutputTaxFormula.Derive(taxableBase, outputTaxRatePercent, taxCategory);
                item.TaxAmount = derivedTax;
                // Null here — and only here — means "never derived", which is what the send gate
                // refuses on. A zero-rated line records the 0 it was actually taxed at.
                item.TaxRatePercentApplied = derivedTax is null
                    ? null
                    : OutputTaxFormula.EffectiveRatePercent(outputTaxRatePercent, taxCategory);

                // FIN-09 / calculation version 2: the stored line total is the taxable base PLUS
                // the line's tax. Presentation is a separate question — the printed document shows
                // the base and states tax once at the end (QuoteItem.TaxableBase).
                item.TotalAmount = RoundCurrency(taxableBase + (derivedTax ?? 0m));
                quoteNetTotal += taxableBase;
                quoteTaxTotal += derivedTax ?? 0m;
            }

            quote.TotalAmount = RoundCurrency(quoteNetTotal + quoteTaxTotal);
            quote.FinancialCalculationVersion = 2;
        }

        /// <summary>
        /// Splits <paramref name="amount"/> across <paramref name="weights"/> in proportion to each
        /// weight, at currency scale, guaranteeing the parts sum EXACTLY back to the amount.
        ///
        /// <para>Largest-remainder: round every share down, then hand the leftover halalas out one
        /// at a time to the lines whose truncated remainder was biggest, tie-broken by the larger
        /// weight so the residual lands on the line best able to absorb it. Rounding each share
        /// independently instead would leave the document's total disagreeing with the sum of its
        /// own lines by a halala or two — the class of defect a buyer's accounts-payable clerk
        /// finds and a seller cannot explain.</para>
        ///
        /// <para>A zero or negative total weight means there is nothing to apportion against, so
        /// nothing is allocated rather than the amount being dumped on an arbitrary line.</para>
        /// </summary>
        private static decimal[] AllocateProRata(decimal amount, decimal[] weights)
        {
            var allocations = new decimal[weights.Length];
            if (weights.Length == 0 || amount <= 0m) return allocations;

            decimal totalWeight = 0m;
            foreach (var weight in weights) totalWeight += weight > 0m ? weight : 0m;
            if (totalWeight <= 0m) return allocations;

            const decimal unit = 0.01m;
            var remainders = new (int Index, decimal Remainder, decimal Weight)[weights.Length];
            decimal allocated = 0m;

            for (var index = 0; index < weights.Length; index++)
            {
                var weight = weights[index] > 0m ? weights[index] : 0m;
                var exact = amount * weight / totalWeight;
                // Truncate toward zero, never away: the sum of the floors can only be short of the
                // amount, so the leftover is always distributable and never has to be clawed back.
                var floored = Math.Floor(exact / unit) * unit;
                allocations[index] = floored;
                allocated += floored;
                remainders[index] = (index, exact - floored, weight);
            }

            var leftover = decimal.Round(amount - allocated, 2, MidpointRounding.AwayFromZero);
            if (leftover <= 0m) return allocations;

            foreach (var candidate in remainders
                         .Where(r => r.Weight > 0m)
                         .OrderByDescending(r => r.Remainder)
                         .ThenByDescending(r => r.Weight))
            {
                if (leftover < unit) break;
                allocations[candidate.Index] += unit;
                leftover -= unit;
            }

            return allocations;
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

        /// <summary>
        /// R7: whether a quote's lifecycle state admits an extend-validity command. Shared by
        /// every read surface so the list, the detail page and the service cannot disagree about
        /// when the control is offered. The superseded-by-revision condition is checked only in
        /// <see cref="ExtendQuoteValidityAsync"/> — see QuoteResponseDTO.CanExtendValidity.
        /// </summary>
        internal static bool IsValidityExtendable(string? statusCode, string? statusValue, DateTime? outcomeOn) =>
            !outcomeOn.HasValue
            && LifecyclePolicy.Canonicalize("Quote", statusCode, statusValue) == "SENT";

        // FIN-12: server-side guard rejecting non-positive quantities/prices.
        //
        // R17: the client's TaxAmount is NOT validated here, because it is no longer used at all.
        // Validating it was the whole defect — the check rejected only negative amounts, so null
        // and zero passed and no tax was ever computed. The line's tax is derived in
        // CalculateQuoteTotals from the tenant's policy rate and the category below.
        //
        // R19: what the client DOES get to state is the tax CATEGORY, and a category that departs
        // from the standard rate must say why.
        private static void ValidateQuoteItemFinancials(decimal quantity, decimal unitPrice,
            string? taxCategory, string? taxCategoryReason)
        {
            if (quantity <= 0)
                throw new ArgumentException($"Invalid line quantity ({quantity}). Quantity must be greater than zero.");
            if (unitPrice <= 0)
                throw new ArgumentException($"Invalid unit price ({unitPrice}). Unit price must be greater than zero.");
            if (!QuoteLineTaxCategories.IsKnown(taxCategory))
                throw new ArgumentException($"Invalid tax category '{taxCategory}'. Use one of: " +
                    $"{string.Join(", ", QuoteLineTaxCategories.All)}.");
            if (QuoteLineTaxCategories.RequiresReason(taxCategory) && string.IsNullOrWhiteSpace(taxCategoryReason))
                throw new ArgumentException(
                    $"A line taxed as {QuoteLineTaxCategories.Normalize(taxCategory)} must state why it departs " +
                    "from the standard rate.");
            if (taxCategoryReason is { Length: > 500 })
                throw new ArgumentException("The tax category reason must not exceed 500 characters.");
        }

        /// <summary>
        /// R17: why this quote's output tax is not fit to leave the building, or null when it is.
        ///
        /// <para>Shared by the send gate and the PDF gate so the email path and the document path
        /// cannot disagree about what "taxed" means, and so a rep who is refused a send gets the
        /// same sentence when they try to download the PDF instead.</para>
        /// </summary>
        private async Task<string?> EvaluateTaxDerivationAsync(long quoteId, long businessUnitId,
            CancellationToken ct)
        {
            var lines = await _context.QuoteItems.AsNoTracking()
                .Where(item => item.QuoteId == quoteId && item.Quote.BusinessUnitId == businessUnitId)
                .ToListAsync(ct);
            return TaxDerivationBlocker(lines,
                await _context.ResolveOutputTaxRatePercentAsync(businessUnitId, ct));
        }

        /// <summary>
        /// The pure half of the draft-completeness gate. A DRAFT missing its currency, its
        /// validity date, its lines, or a price on any line cannot be rendered — and until this
        /// was factored out it lived only inside the PDF renderer, so a send passed every
        /// synchronous check, answered "queued", and died in the outbox where no rep could see
        /// it. Production holds two customer quotes today, both DRAFT, both with NULL currency,
        /// one totalling 0.00: every one of them would take that path.
        ///
        /// <para>Names the specific missing thing. "Complete the quote" is not an instruction.</para>
        /// </summary>
        internal static string? DraftCompletenessBlocker(
            bool isDraft, long? currencyId, DateTime? validUntil, ICollection<QuoteItem> items)
        {
            const string prefix = "Commercial Review Required: ";

            // The currency is checked for EVERY status, not only drafts. This gate used to
            // return null for any non-draft, on the assumption that a quote past DRAFT had
            // already passed it on the way out. Backfilled and legacy quotes never took that
            // path: production holds two EXPIRED quotes with CurrencyID NULL and totals of
            // 740.00 and 59,200,000.00, and the renderer below filled the gap with a hardcoded
            // "USD" — so their PDFs printed a US-dollar grand total on the tenant's letterhead.
            // A price with no currency is not a price; refuse the document rather than invent
            // the unit. Checked first so the reader is told the one thing that can never be
            // fixed by editing the lines.
            if (!currencyId.HasValue)
                return prefix + (isDraft
                    ? "this quote has no currency. Set the currency on the quote before sending it."
                    : "this quote has no currency on record, so its document cannot be produced or sent. "
                      + "Issue it as a new revision with the currency set.");

            if (!isDraft) return null;
            if (items.Count == 0)
                return prefix + "this quote has no lines. Add the lines you are quoting for.";
            if (!validUntil.HasValue)
                return prefix + "this quote has no validity date. Set how long the prices hold before sending it.";
            if (items.Any(item => item.UnitPrice <= 0))
                return prefix + "one or more lines have no price. Price every line before sending the quote.";
            return null;
        }

        /// <summary>
        /// The pure half of the issuer-identity gate: a document that cannot name its sender is
        /// not a document. Returns the refusal and the screen that fixes it — the rep who meets
        /// this did nothing wrong.
        ///
        /// <para>A missing VAT registration number is deliberately NOT here; see the note at the
        /// call site.</para>
        /// </summary>
        internal static (string Message, string SetupLabel, string SetupPath)? IssuerIdentityBlocker(
            string? sellerLegalName, string? companyAddress, string? companyPhone, string? companyEmail)
        {
            if (string.IsNullOrWhiteSpace(sellerLegalName))
                return ("This quotation cannot be produced because the business unit sending it has no "
                    + "name on file. Add the legal entity name under Setup → Business Units, then "
                    + "download the quote again.", "Setup → Business Units", "/setup/business-unit");
            if (string.IsNullOrWhiteSpace(companyAddress)
                && string.IsNullOrWhiteSpace(companyPhone)
                && string.IsNullOrWhiteSpace(companyEmail))
                return ("This quotation cannot be produced because it would not tell the customer how "
                    + "to reach you: no company address, telephone or email is configured. Fill in "
                    + "Setup → Quote Format, then download the quote again.",
                    "Setup → Quote Format", "/setup/quote-format");
            return null;
        }

        /// <summary>
        /// The pure half of the tax gate: the first line that has no derived tax, phrased for the
        /// person who has to fix it. Lines are named by the buyer's own reference where there is
        /// one, because "line 3" means nothing to a rep looking at a bid list numbered 00010,
        /// 00020, 00030.
        /// </summary>
        internal static string? TaxDerivationBlocker(IEnumerable<QuoteItem> items, decimal? outputTaxRatePercent)
        {
            var ordered = OrderQuoteLines(items);
            if (ordered.Count == 0) return null;
            for (var index = 0; index < ordered.Count; index++)
            {
                var item = ordered[index];
                var label = string.IsNullOrWhiteSpace(item.CustomerLineRef)
                    ? (index + 1).ToString()
                    : item.CustomerLineRef!;
                if (OutputTaxFormula.DerivationBlocker(label, outputTaxRatePercent, item.TaxCategory,
                        item.TaxCategoryReason, item.TaxRatePercentApplied) is { } blocker)
                    return blocker;
            }
            return null;
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
                   .ThenInclude(i => i.Rfqitem) // the buyer's requested manufacturer/part/date
               .Include(q => q.QuoteItems)
                   .ThenInclude(i => i.Product)
               .Include(q => q.Customer)
               .Include(q => q.BusinessUnit)
               .Include(q => q.Currency)
               .Include(q => q.Status)
               .Include(q => q.DiscountType)
               // The RFQ was never included, so RfqNo answered null on every quote response for as
               // long as the "// Add Include if needed" note beside it has been there. The note was
               // right and nobody acted on it, which is how a TODO becomes a defect: the field is
               // declared, the screen has somewhere to put it, and the API quietly says there is
               // nothing to show. LeadId reads through the same navigation.
               .Include(q => q.Rfq)
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
                RfqNo = quote.Rfq?.Rfqno,

                // The commercial identity, which this projection used to drop on the floor.
                //
                // Quote.InheritCommercialIdentity stamps CommercialCaseId, NexoraSerial, CustomerId
                // and ContactId, a PostgreSQL trigger refuses the row if they do not match the RFQ,
                // and then EVERY caller of this method -- GET /api/Quote/{id} and the quote-draft
                // response among them -- was handed NexoraSerial = null. A quote that carries the
                // serial and a quote that never received one looked identical from outside, which
                // is the worst way to lose a field: the screen shows nothing, the API says nothing
                // is there, and the row has been correct the whole time.
                //
                // Found while asserting a seam: the test read this DTO, saw null, and would have
                // reported a fabricated identity defect had it not been re-pointed at the row.
                LeadId = quote.Rfq?.LeadId,
                CommercialCaseId = quote.CommercialCaseId,
                NexoraSerial = quote.NexoraSerial,
                ContactId = quote.ContactId,
                SourceLeadRevision = quote.SourceLeadRevision,
                SourceRfqRevision = quote.SourceRfqRevision,

                // The optimistic-concurrency token and the revision number the customer sees. This
                // projection left both at their default 0 while the repository's projection of the
                // same DTO set them, so which endpoint a caller reached decided whether the quote
                // claimed to be version 0 or its real version. A client that echoes back a 0 as
                // ExpectedVersion is refused by the lifecycle guard for a reason that is not its
                // fault, so this is stated here rather than defaulted.
                LifecycleVersion = quote.LifecycleVersion,
                Version = quote.RevisionNo,
                ItemCount = quote.QuoteItems.Count,

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
                ValidityExtendedOn = quote.ValidityExtendedOn,
                CanExtendValidity = IsValidityExtendable(quote.Status?.SetupCode, quote.Status?.SetupValue, quote.OutcomeOn),
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
                    TaxCategory = i.TaxCategory,
                    TaxCategoryReason = i.TaxCategoryReason,
                    TaxRatePercentApplied = i.TaxRatePercentApplied,
                    // Both are stored, and both are unrecoverable from the rest of the payload —
                    // see QuoteItemResponseDTO. The PDF builder below already reads them; the
                    // screen could not, and invented its own arithmetic instead.
                    HeaderDiscountAllocated = i.HeaderDiscountAllocated,
                    TaxableBase = i.TaxableBase,
                    DeliveryLeadTime = i.DeliveryLeadTime,
                    // Read through the existing RfqitemId link — never copied onto QuoteItem.
                    // See QuoteItemResponseDTO for why these are projected rather than stored.
                    RequestedManufacturerName = i.Rfqitem?.ManufacturerName,
                    RequestedManufacturerPartNumber = i.Rfqitem?.ManufacturerPartNumber,
                    RequestedItemMaterialCode = i.Rfqitem?.ItemMaterialCode,
                    RequestedAlternatePartNumber = i.Rfqitem?.AlternatePartNumber,
                    RequestedDeliveryDate = i.Rfqitem?.RequiredDesiredDate,
                    RequestedLeadTimeDays = i.Rfqitem?.LeadTime,
                    RequestedCurrency = i.Rfqitem?.Currency
                }).ToList()
            };
        }

        /// <summary>
        /// R5 enforcement point for every priced document that leaves the building.
        ///
        /// <para>Recording an attestation only proves what the prices WERE when the rep confirmed
        /// them; this is where a reader proves what they still ARE — the same record-at-capture /
        /// verify-at-serve shape <c>FileController.ServeVerifiedAttachmentAsync</c> applies to
        /// attachment bytes, for the same reason: a document cannot be un-issued.</para>
        ///
        /// <para>Two independent conditions, both fail-closed:</para>
        /// <list type="number">
        /// <item>A recorded attestation must cover the quote's CURRENT prices. This is what the
        /// PDF endpoint was missing entirely — the commercial document could be pulled and
        /// forwarded with nobody having attested to a single price on it.</item>
        /// <item>When the caller carries a binding (the delivery worker), the current prices must
        /// still hash to the fingerprint the send was AUTHORISED for. Without this, the window
        /// between "send" enqueueing and the worker draining it — during which the quote is still
        /// DRAFT and still editable — lets an edited price reach the customer under an
        /// attestation that was made against different numbers.</item>
        /// </list>
        /// </summary>
        private async Task EnsureAttestedPricesAsync(
            long quoteId, long businessUnitId, string quoteNo,
            string? boundAttestationFingerprint, CancellationToken ct)
        {
            var state = await new ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationService(_context)
                .EvaluateAsync(quoteId, businessUnitId, ct);

            if (!state.Satisfied)
                throw new ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationRequiredException(
                    $"Quote '{quoteNo}' cannot be issued as a document yet. {state.Reason}");

            if (boundAttestationFingerprint is null) return;

            if (!ERP_RFQ_Automation.Intelligence.Pricing.PriceFingerprint.Matches(
                    state.CurrentFingerprint, boundAttestationFingerprint))
                throw new ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationRequiredException(
                    $"Quote '{quoteNo}' was not sent: its prices changed after this send was authorised, " +
                    "so the confirmation on file does not cover what would have gone out. Nothing was " +
                    "emailed to the customer. Confirm the price source again and send the quote again.")
                { BindingBroken = true };
        }

        public async Task<byte[]> GenerateQuotePdfAsync(long quoteId, long businessUnitId,
            string? boundAttestationFingerprint = null, CancellationToken ct = default)
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
            if (DraftCompletenessBlocker(isDraft, quote.CurrencyId, quote.ValidUntil, quote.QuoteItems) is { } incomplete)
                throw new InvalidOperationException(incomplete);

            // The gate above proves CurrencyId is set; this proves the id names a currency the
            // tenant actually has. Resolved once, here, so the renderer never has to choose
            // between a blank and a guess.
            var currencyCode = quote.Currency?.Code?.Trim();
            if (string.IsNullOrWhiteSpace(currencyCode))
                throw new InvalidOperationException(
                    $"Quote '{quote.QuoteNo}' cannot be issued as a document: its currency "
                    + $"(id {quote.CurrencyId}) is not on record for this tenant.");

            // R5: the completeness check above proves the document CAN be rendered; this one
            // proves it MAY be. Runs second so an incomplete draft gets the more specific and
            // more actionable message rather than being told to confirm prices that do not
            // exist yet.
            await EnsureAttestedPricesAsync(quoteId, businessUnitId, quote.QuoteNo, boundAttestationFingerprint, ct);

            // R17: same gate the send path applies, because the PDF *is* the commercial document —
            // once it exists it can be downloaded, forwarded and relied on, and a quotation with no
            // VAT separately stated is deemed VAT-inclusive under KSA law. Gating only the email
            // would leave the download as an unguarded way to hand a customer that document.
            if (TaxDerivationBlocker(quote.QuoteItems,
                    await _context.ResolveOutputTaxRatePercentAsync(businessUnitId, ct)) is { } taxBlocker)
                throw new InvalidOperationException(
                    $"Quote '{quote.QuoteNo}' cannot be issued as a document yet. {taxBlocker}");

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

            // THE ISSUER, read from the tenant's own records and nowhere else — the same rule the
            // delivery note already operates under (DeliveryNoteReadService.IssuerIdentity): every
            // field may be null, and a null is reported as a gap rather than filled with something
            // plausible, because the plausible one gets used.
            //
            // What this replaced was not a fallback but a wrong answer: `?? Lead.Clientemail` is
            // the address the ENQUIRY ARRIVED FROM, so a tenant with no seller email on file sent
            // the customer a quotation naming the customer as its sender, next to a placeholder
            // street address and a +1 800 number. TenantBaselineSeeder deliberately leaves
            // CompanyEmail null "so a blank sender line is a visible omission somebody will fix" —
            // it was never blank, so nobody ever fixed it.
            string companyAddress = config?.CompanyAddress;
            string companyPhone = config?.CompanyPhone;
            string companyEmail = config?.CompanyEmail;

            // Issuer identity is deliberately tenant-owned. Delivery runs under the production
            // tenant role and must not elevate to the platform control plane to render a PDF.
            string sellerLegalName = string.IsNullOrWhiteSpace(quote.BusinessUnit?.LegalName)
                ? quote.BusinessUnit?.BusinessUnitName
                : quote.BusinessUnit.LegalName;
            string sellerCommercialRegistration = quote.BusinessUnit?.CommercialRegistrationNumber;
            string sellerTaxRegistration = quote.BusinessUnit?.TaxRegistrationNumber;

            // A document that cannot name its sender is not a document. Refuse, and name the
            // screen — the rep who meets this did nothing wrong.
            if (IssuerIdentityBlocker(sellerLegalName, companyAddress, companyPhone, companyEmail) is { } issuerGap)
                throw new QuoteIssuerIdentityMissingException(issuerGap.Message);

            // Deliberately NOT a refusal. A VAT number is nullable by design, nothing has ever
            // populated it automatically, and a tenant that is not yet VAT-registered still sends
            // valid quotations. The delivery note prints the same gap on the face of the artefact
            // rather than blocking; two customer-facing documents disagreeing about whether a
            // missing registration is fatal would be worse than either rule alone.
            string footerText = config?.FooterText;

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

            // The header discount is READ from the per-line allocation, not reconstructed by
            // subtracting the stored total from a sum of line values. The reconstruction printed
            // the discount 15% too large, because the figures it subtracted had tax in them and the
            // one it compared against did not: a rep who entered 10% on a 10,000.00 quote saw
            // 1,150.00 on the customer's copy. Legacy rows written before the allocation column
            // existed carry null, and for those the old inference is still the only answer
            // available — so it stays, scoped to exactly those rows.
            decimal headerDiscount = quote.QuoteItems.Sum(i => RoundCurrency(i.HeaderDiscountAllocated ?? 0));
            if (headerDiscount == 0
                && quote.QuoteItems.All(i => i.HeaderDiscountAllocated is null)
                && quote.DiscountTypeId.HasValue && quote.DiscountValue.HasValue)
            {
                decimal itemsNetTotal = subTotal - totalItemDiscounts + totalTax;
                headerDiscount = itemsNetTotal - (quote.TotalAmount ?? 0);
                if (headerDiscount < 0) headerDiscount = 0;
            }

            // What the line column adds up to: every line's taxable base, tax excluded.
            decimal netExcludingTax = quote.QuoteItems.Sum(i => RoundCurrency(i.TaxableBase));

            // Name the rate on the document when every taxed line shares one — "VAT 15%" is what a
            // buyer's finance team checks against. When a quote mixes treatments (a zero-rated
            // export line beside a standard one) no single rate is true of the total, so the label
            // stays bare and the per-line breakdown below carries the detail.
            var appliedRates = quote.QuoteItems
                .Where(i => i.TaxRatePercentApplied is > 0m)
                .Select(i => i.TaxRatePercentApplied!.Value)
                .Distinct()
                .ToList();
            string taxRateLabel = appliedRates.Count == 1
                ? $" {decimal.Round(appliedRates[0], 2).ToString("0.##")}%"
                : string.Empty;

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
                                    c.Item().Text(sellerLegalName)
                                        .FontSize(22).Bold().FontColor(primaryColor);
                                }

                                if (!string.IsNullOrWhiteSpace(footerText))
                                    c.Item().PaddingTop(8).Text(footerText)
                                        .FontSize(9).Italic().FontColor(Colors.Grey.Darken1);

                                c.Item().PaddingTop(10).Column(details =>
                                {
                                    details.Spacing(1);
                                    // Each line appears only when it has something to say. A
                                    // stray "P: " with nothing after it reads as a rendering
                                    // fault; the refusal above already guarantees at least one
                                    // of these three is present.
                                    if (!string.IsNullOrWhiteSpace(companyAddress))
                                        details.Item().Text(companyAddress).FontSize(8).FontColor(Colors.Grey.Medium);
                                    if (!string.IsNullOrWhiteSpace(companyPhone))
                                        details.Item().Text($"P: {companyPhone}").FontSize(8).FontColor(Colors.Grey.Medium);
                                    if (!string.IsNullOrWhiteSpace(companyEmail))
                                        details.Item().Text($"E: {companyEmail}").FontSize(8).FontColor(Colors.Grey.Medium);

                                    // Registrations are printed as a NAMED GAP when absent rather
                                    // than omitted. A Saudi buyer's finance team looks for the
                                    // seller VAT number before it looks at the price; a line that
                                    // is simply missing reads as an oversight by the reader,
                                    // while "not on file" is unmistakably the sender's to fix —
                                    // and the sender sees it on their own copy.
                                    details.Item().PaddingTop(4).Text(
                                        "CR: " + (string.IsNullOrWhiteSpace(sellerCommercialRegistration)
                                            ? "not on file" : sellerCommercialRegistration))
                                        .FontSize(8).FontColor(Colors.Grey.Medium);
                                    details.Item().Text(
                                        "VAT: " + (string.IsNullOrWhiteSpace(sellerTaxRegistration)
                                            ? "not on file" : sellerTaxRegistration))
                                        .FontSize(8).FontColor(Colors.Grey.Medium);
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
                                // The line's own consideration, tax EXCLUDED. The stored TotalAmount
                                // carries the line's tax inside it (calculation version 2), so
                                // printing it here put VAT in the line column and then added the
                                // same VAT again in the summary below — the printed lines could not
                                // be added up to the printed subtotal.
                                table.Cell().Element(RowStyle).AlignRight().Text(item.x.TaxableBase.ToString("N2")).Bold();
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
                                // No fallback. This used to be `quote.Currency?.Code ?? "USD"`,
                                // which printed a US-dollar grand total on every currency-less
                                // quote — a 3.75x misstatement of a SAR price. The gate above now
                                // refuses the document instead, so currencyCode is never blank here.
                                var currency = currencyCode;

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
                                    // Read top to bottom this is the arithmetic itself: gross, what
                                    // came off it, the net the tax is charged on, the tax, the total.
                                    // "Total excluding VAT" is the line column's own sum, so a buyer
                                    // can add up the page and arrive here.
                                    FinancialRow("Subtotal", subTotal);
                                    if (totalItemDiscounts > 0) FinancialRow("Item Discounts", -totalItemDiscounts);
                                    if (headerDiscount > 0) FinancialRow("Additional Discount", -headerDiscount);
                                    FinancialRow("Total excluding VAT", netExcludingTax);
                                    if (totalTax > 0) FinancialRow($"VAT{taxRateLabel}", totalTax);

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

        /// <summary>
        /// Every reason this quote's send would be refused, answered BEFORE the rep opens the
        /// send dialog, each naming the screen that fixes it.
        ///
        /// <para><b>Why this exists.</b> Half the send chain runs asynchronously, inside
        /// <c>QuoteDeliveryDispatcher</c>, and its refusals reach nobody. A quote with no
        /// currency, a business unit with no legal name, or a tenant with no transmitting
        /// mailbox all pass every synchronous check, answer the rep "Quote delivery queued",
        /// and then die in the outbox. Worse, the delivery idempotency key is fixed per quote
        /// (<c>quote:{id}:delivery:v1</c>), so a dead-lettered row makes that quote
        /// PERMANENTLY unsendable — the rep's only remaining move is a new revision, and
        /// nothing tells them so.</para>
        ///
        /// <para>Measured on production 2026-09-02: <c>quote_delivery_requests</c> has zero
        /// rows and both existing customer quotes are DRAFT with NULL currency. Every one of
        /// them takes that path the first time somebody presses Send.</para>
        ///
        /// <para>This reads the SAME rules the sender and the renderer apply
        /// (<see cref="DraftCompletenessBlocker"/>, <see cref="TaxDerivationBlocker"/>,
        /// <see cref="IssuerIdentityBlocker"/>, <c>IOutboundSenderResolver</c>) rather than
        /// re-deriving them, because a preflight that re-derives a rule is a preflight that
        /// will eventually disagree with the thing it predicts.</para>
        ///
        /// <para>The price attestation is deliberately NOT a blocker: confirming the price
        /// source is a designed step of the send dialog, not a setup failure.</para>
        /// </summary>
        public async Task<QuoteSendReadinessDTO> EvaluateSendReadinessAsync(
            long quoteId, long businessUnitId, CancellationToken ct = default)
        {
            if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
            var quote = await _context.Quotes.AsNoTracking()
                .Include(q => q.QuoteItems)
                .Include(q => q.BusinessUnit)
                .Include(q => q.Status)
                .FirstOrDefaultAsync(q => q.Id == quoteId && q.BusinessUnitId == businessUnitId, ct)
                ?? throw new KeyNotFoundException($"Quote with ID {quoteId} not found.");

            var readiness = new QuoteSendReadinessDTO { QuoteId = quote.Id };
            void Block(string code, string message, string? label = null, string? path = null) =>
                readiness.Blockers.Add(new QuoteSendBlockerDTO
                {
                    Code = code, Message = message, SetupLabel = label, SetupPath = path
                });

            // Reported in the order the send applies them, so the first thing the rep reads is
            // the first thing that would actually stop them.
            if (await _context.Set<LeadRevisionImpact>().AsNoTracking().AnyAsync(x =>
                    x.BusinessUnitId == businessUnitId && x.AggregateType == "QUOTE" &&
                    x.AggregateId == quoteId && x.Status == "OPEN", ct))
                Block("CUSTOMER_REVISION_UNRESOLVED",
                    "This quote is stale because a customer revision was received. Review and resolve "
                    + "the revision impact before sending it.");

            var isDraft = string.Equals(quote.Status?.SetupCode, "DRAFT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(quote.Status?.SetupValue, "Draft", StringComparison.OrdinalIgnoreCase);
            if (DraftCompletenessBlocker(isDraft, quote.CurrencyId, quote.ValidUntil, quote.QuoteItems) is { } incomplete)
                Block("QUOTE_INCOMPLETE", incomplete);

            if (TaxDerivationBlocker(quote.QuoteItems,
                    await _context.ResolveOutputTaxRatePercentAsync(businessUnitId, ct)) is { } taxBlocker)
                Block("OUTPUT_TAX_NOT_DERIVED", taxBlocker,
                    "Setup → Commercial Policy", "/setup/commercial-policy");

            var config = await _quoteConfigRepository.GetByBusinessUnitIdAsync(businessUnitId);
            var sellerLegalName = string.IsNullOrWhiteSpace(quote.BusinessUnit?.LegalName)
                ? quote.BusinessUnit?.BusinessUnitName
                : quote.BusinessUnit.LegalName;
            if (IssuerIdentityBlocker(sellerLegalName, config?.CompanyAddress, config?.CompanyPhone,
                    config?.CompanyEmail) is { } issuerGap)
                Block("ISSUER_IDENTITY_INCOMPLETE", issuerGap.Message, issuerGap.SetupLabel, issuerGap.SetupPath);

            // The one gate whose failure the rep can do nothing about after the fact: a
            // non-transmitting sender dead-letters the delivery on its FIRST attempt, and the
            // fixed idempotency key then makes the quote unsendable for good. Same authority
            // the sender uses, so the two cannot disagree. Null only in unit harnesses that
            // compose no resolver — silence is honest there; a guess is not.
            if (_outboundSenders is not null)
            {
                var sender = await _outboundSenders.ResolveAsync(businessUnitId, ct);
                if (!sender.TransmitsMail)
                    Block("OUTBOUND_MAIL_NOT_CONFIGURED",
                        "Nothing can be emailed to customers yet: this tenant has no active SMTP mailbox "
                        + "and the platform sender does not transmit. Sending now would fail permanently "
                        + "and this quote could then only go out as a new revision.",
                        "Setup → Mailboxes", "/setup/mailboxes");
            }

            // A delivery that already ended terminally. UNCERTAIN is the restart case: the
            // customer may already hold this quote, and at-most-once means nothing is resent
            // automatically. Either way the fixed key means this quote itself can never be sent
            // again — say so, and say what to do instead.
            var delivery = await _context.QuoteDeliveryRequests.AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && x.QuoteId == quoteId)
                .OrderByDescending(x => x.Id)
                .FirstOrDefaultAsync(ct);
            if (delivery is { DeadLetteredOn: not null })
            {
                var uncertain = delivery.LastErrorCode?.StartsWith("DeliveryOutcomeUncertain",
                    StringComparison.OrdinalIgnoreCase) == true;
                readiness.DeliveryOutcome = uncertain ? "UNCERTAIN" : "NOT_DELIVERED";
                Block(uncertain ? "DELIVERY_OUTCOME_UNCERTAIN" : "DELIVERY_FAILED",
                    uncertain
                        ? "Delivery of this quote was interrupted and never confirmed either way, so the "
                          + "customer may or may not have received it. Nothing was resent automatically, on "
                          + "purpose. Check with the customer; if it did not arrive, issue this quote as a new "
                          + "revision and send that."
                        : "Delivery of this quote failed permanently and it cannot be sent again under the "
                          + "same quote number. Fix the reason above, then issue it as a new revision and send "
                          + "that.");
            }
            else if (delivery is { CompletedOn: null })
            {
                readiness.DeliveryInFlight = true;
                Block("DELIVERY_IN_FLIGHT",
                    "This quote is already queued for delivery. Wait for it to complete rather than "
                    + "sending it twice.");
            }
            else if (delivery is { CompletedOn: not null } && !quote.SentOn.HasValue)
            {
                // Sealed by the worker on provider acceptance, but the quote's own status has
                // not caught up yet (the process died between the two writes, or the status
                // update threw and is being retried). The customer HAS this quote. Say so —
                // never "uncertain", and never let it look sendable.
                readiness.DeliveryOutcome = "DELIVERED";
                Block("DELIVERY_STATUS_PENDING",
                    $"This quote was delivered to the customer on {delivery.CompletedOn:yyyy-MM-dd HH:mm} UTC. "
                    + "Its status is still being updated; nothing needs to be resent.");
            }

            // R5 price provenance. This is the LAST thing the sender checks and it was the one
            // thing readiness did not, so on 2026-09-04 readiness answered canSend=true and the
            // send came back 409 "the price source has not been confirmed". That is precisely the
            // defect this endpoint exists to prevent, one gate further along: a screen that decides
            // for itself what the sender would allow will eventually disagree with it.
            //
            // So this asks the same service the sender asks (EnsureAttestedPricesAsync above) and
            // reports its own sentence, rather than restating the rule in a second place where the
            // two can drift apart.
            var attestation = await new ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationService(_context)
                .EvaluateAsync(quote.Id, businessUnitId, ct);
            if (!attestation.Satisfied)
                Block("PRICE_ATTESTATION_REQUIRED",
                    attestation.Reason
                    ?? "Confirm where these prices came from - your sales manager, or a supplier quote - before sending.");

            readiness.CanSend = readiness.Blockers.Count == 0;
            return readiness;
        }

        public async Task<QuoteSendResult> SendQuoteEmailAsync(long quoteId, long businessUnitId, string recipientEmail, string? customSubject = null, string? customBody = null, QuoteSendOptions? options = null)
        {
            if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
            options ??= new QuoteSendOptions();

            var quote = await _context.Quotes
                .Include(q => q.BusinessUnit)
                // Currency and Customer are loaded for the customer-facing body below: a quote
                // e-mail that states neither what it is worth nor who it is for is a covering
                // note, not a quotation.
                .Include(q => q.Currency)
                .Include(q => q.Customer)
                .Include(q => q.Rfq)
                    .ThenInclude(r => r.Lead)
                .FirstOrDefaultAsync(q => q.Id == quoteId && q.BusinessUnitId == businessUnitId)
                ?? throw new KeyNotFoundException("Quote not found");

            // A customer amendment creates an explicit open impact rather than silently mutating
            // an existing quote. Sending while that impact is open would distribute a commercial
            // document known to be stale, even if its old prices still have a valid attestation.
            if (await _context.Set<LeadRevisionImpact>().AsNoTracking().AnyAsync(x =>
                    x.BusinessUnitId == businessUnitId && x.AggregateType == "QUOTE" &&
                    x.AggregateId == quoteId && x.Status == "OPEN"))
                throw new InvalidOperationException(
                    "This Quote Draft is stale because a customer revision was received. Review and resolve the revision impact before sending it.");

            // R5 PRICE-PROVENANCE GATE — the pre-release control. Every send, first issue or
            // revision, must be covered by a recorded confirmation of where the prices came
            // from (sales manager / supplier quote) whose snapshot still matches the quote's
            // current line prices. This runs BEFORE the below-floor check for two reasons:
            //   * it is the gate; a quote with no confirmation must not even reach the point
            //     of creating an approval hold, and
            //   * it closes the below-floor guard's fail-open hole — a line whose floor
            //     cannot be established (any first-time item) never blocks there, so before
            //     this gate a brand-new item left the building with nothing recorded at all.
            // Deliberately NOT bypassable through QuoteSendOptions: BypassFloorHold releases
            // the below-floor hold only. Constructed directly (the FxConversionService
            // precedent in BelowFloorGuard) so no constructor overload can omit the gate.
            var attestations = new ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationService(_context);
            var attestation = await attestations.EvaluateAsync(quoteId, businessUnitId, CancellationToken.None);
            if (!attestation.Satisfied)
                return QuoteSendResult.AwaitingPriceAttestation(attestation.Reason!);

            // R17 OUTPUT-TAX GATE. Every line must carry a tax the server derived. A line whose
            // TaxRatePercentApplied is null was never taxed at all — the business unit has no output
            // tax rate configured, or the line has never been priced — and a quotation with no VAT
            // separately stated on it is deemed VAT-INCLUSIVE under KSA law, so the seller funds
            // 15/115 ≈ 13.04% of that line out of its own margin. Refusing is not conservatism; a
            // 20% target margin becomes about 8% the moment that document is honoured.
            //
            // Placed after the price-provenance gate and before anything is queued, because the
            // derived tax is only meaningful against prices the attestation has already covered.
            if (await EvaluateTaxDerivationAsync(quoteId, businessUnitId, CancellationToken.None) is { } taxBlocker)
                return QuoteSendResult.AwaitingTaxDerivation(taxBlocker);

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

            // "Our Company" and "Sales Team" below were the same defect as the PDF's placeholder
            // identity, one layer out: a customer receiving mail from "Our Company" learns
            // nothing and trusts less. BusinessUnitName is non-null in the schema, so naming it
            // directly is not a narrowing — it removes a fallback that could only ever have
            // fired on a broken row, and would have hidden that breakage behind a bland phrase.
            var subject = !string.IsNullOrEmpty(customSubject)
                ? customSubject
                : $"Quote #{quote.QuoteNo} from {quote.BusinessUnit?.BusinessUnitName}";

            // The default body used to say only "please find attached", which told a buyer nothing
            // they could act on and nothing they could file. Everything added below is already
            // known at this point and is a FACT ABOUT THIS QUOTE -- no marketing, no invented
            // commitment. Each line is omitted entirely when its value is absent, because a
            // customer-facing e-mail must never read "valid until" followed by nothing, and the
            // greeting falls back rather than printing an empty name.
            //
            // CustomerRfqReference is the buyer's OWN number for the enquiry. It matters more than
            // ours: it is how they match this quote to the request they raised, and without it a
            // procurement desk has to open the attachment to find out what it answers.
            var greetingName = quote.Customer?.Name;
            var greeting = string.IsNullOrWhiteSpace(greetingName) ? "Dear Customer" : $"Dear {greetingName}";

            var facts = new List<string>();
            if (!string.IsNullOrWhiteSpace(quote.Rfq?.CustomerRfqReference))
                facts.Add($"<p>Your reference: {quote.Rfq!.CustomerRfqReference}</p>");
            if (quote.TotalAmount is decimal total && !string.IsNullOrWhiteSpace(quote.Currency?.Code))
                facts.Add($"<p>Total: {quote.Currency!.Code} {total:N2}</p>");
            if (quote.ValidUntil is DateTime validUntil)
                facts.Add($"<p>Valid until: {validUntil:d MMMM yyyy}</p>");

            var body = !string.IsNullOrEmpty(customBody)
                ? customBody.Replace("\n", "<br/>")
                : $@"
                <p>{greeting},</p>
                <p>Please find attached our quotation #{quote.QuoteNo}.</p>
                {string.Join("\n                ", facts)}
                <p>If anything here needs revisiting, reply to this message and we will pick it up.</p>
                <br/>
                <p>Kind regards,</p>
                <p>{quote.BusinessUnit?.BusinessUnitName}</p>
            ";

            var issuerEmail = await _context.QuoteConfigurations.AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId)
                .Select(x => x.CompanyEmail)
                .SingleOrDefaultAsync();

            var deliveryKey = $"quote:{quote.Id}:delivery:v1";
            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();
                var ownsTransaction = _context.Database.CurrentTransaction is null;
                await using var transaction = ownsTransaction
                    ? await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable)
                    : null;
                try
                {
                    if (_context.Database.IsNpgsql())
                        await _context.Database.ExecuteSqlInterpolatedAsync(
                            $"SELECT pg_advisory_xact_lock(hashtextextended({$"quote-delivery:{quote.BusinessUnitId}:{quote.Id}"}, 0))");
                    // R5 CONTENT BINDING. Re-read the fingerprint HERE, inside the serializable
                    // transaction that also inserts the delivery row and under the same advisory
                    // lock, so the value recorded is the priced content as it stands at the
                    // instant the send is authorised — not as it stood when the gate above ran.
                    // QuoteDeliverySender verifies against this before rendering the PDF.
                    var boundFingerprint = (await attestations
                        .EvaluateAsync(quoteId, businessUnitId, CancellationToken.None)).CurrentFingerprint;

                    var existingDelivery = await _context.QuoteDeliveryRequests.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.BusinessUnitId == quote.BusinessUnitId && x.IdempotencyKey == deliveryKey);
                    if (existingDelivery is not null)
                    {
                        if (!string.Equals(existingDelivery.RecipientEmail, recipientEmail.Trim(), StringComparison.OrdinalIgnoreCase)
                            || existingDelivery.Subject != subject || existingDelivery.Body != body)
                            throw new InvalidOperationException("The quote delivery key was already used with different content.");
                        // Same key, still in flight, but the PRICES moved since that send was
                        // authorised. Reporting "queued" here would be untrue: the dispatcher
                        // will refuse it and the customer will receive nothing. Say so now,
                        // while the rep is still looking at the screen.
                        if (existingDelivery.CompletedOn is null && existingDelivery.DeadLetteredOn is null
                            && existingDelivery.AttestedPriceFingerprint is not null
                            && !ERP_RFQ_Automation.Intelligence.Pricing.PriceFingerprint.Matches(
                                boundFingerprint, existingDelivery.AttestedPriceFingerprint))
                            throw new InvalidOperationException(
                                $"Quote '{quote.QuoteNo}' is already queued for delivery, but its prices have changed " +
                                "since that send was authorised. The queued email will be refused and nothing will be " +
                                "sent to the customer. Issue the changed prices as a new revision instead.");
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
                        // The customer enquiry address is a recipient identity, never the sender.
                        // QuoteDeliverySender treats this tenant-owned company address as Reply-To;
                        // the transport's verified From identity remains authoritative.
                        FromEmail = string.IsNullOrWhiteSpace(issuerEmail) ? null : issuerEmail.Trim(),
                        AttachmentFileName = $"Quote_{quote.QuoteNo}.pdf",
                        AttestedPriceFingerprint = boundFingerprint,
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
                _context.ChangeTracker.Clear();
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

        public async Task<DeliveredQuoteReconciliation> ReconcileDeliveredQuotesAsync(
            long businessUnitId, CancellationToken ct = default)
        {
            if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
            var now = DateTime.UtcNow;
            // Sealed deliveries (provider acceptance recorded) whose quote never reached SENT.
            // The join is on the quote's own SentOn so the query is the same "what does the
            // ledger say" question the dispatcher's tenant discovery asks, answered per tenant.
            var candidates = await (
                from delivery in _context.QuoteDeliveryRequests.AsNoTracking()
                join quote in _context.Quotes.AsNoTracking() on delivery.QuoteId equals quote.Id
                where delivery.BusinessUnitId == businessUnitId
                    && quote.BusinessUnitId == businessUnitId
                    && delivery.CompletedOn != null
                    && delivery.AvailableOn <= now
                    && quote.SentOn == null
                orderby delivery.CompletedOn
                select new { delivery.Id, delivery.QuoteId })
                .Take(50)
                .ToListAsync(ct);

            var finalized = 0;
            var deferred = 0;
            foreach (var candidate in candidates)
            {
                try
                {
                    await FinalizeQuoteDeliveryAsync(candidate.QuoteId, businessUnitId, ct);
                    finalized++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    // Defer on the ledger row, never on the quote: the row already proves the
                    // send, and its AvailableOn is what keeps a persistently refusing quote from
                    // being retried every five seconds. The error code is visible to the tenant
                    // through send-readiness and to the operator through the ledger.
                    deferred++;
                    _context.ChangeTracker.Clear();
                    var row = await _context.QuoteDeliveryRequests
                        .SingleOrDefaultAsync(x => x.Id == candidate.Id && x.BusinessUnitId == businessUnitId, ct);
                    if (row is null) continue;
                    var code = $"SentNotFinalized:{exception.GetType().Name}";
                    row.LastErrorCode = code.Length <= 160 ? code : code[..160];
                    row.AvailableOn = DateTime.UtcNow.AddMinutes(5);
                    row.Version++;
                    await _context.SaveChangesAsync(ct);
                }
            }
            return new DeliveredQuoteReconciliation(finalized, deferred);
        }

        private async Task RecordQuoteSentWorkAsync(Quote quote, QuoteSendOptions options, CancellationToken ct)
        {
            var lead = quote.Rfq?.Lead;
            if (_sales is null || !quote.SentOn.HasValue) return;

            // A quote that has gone to a customer needs somebody to chase it. Until now this
            // returned silently whenever the lead had no owner, so on a tenant carrying 179
            // unassigned leads a sent quote produced no activity record and no follow-up, and
            // nothing anywhere said so. The quote sent on 2026-09-04 is exactly that case.
            //
            // The tenant already answers "if nobody owns it, give it to ___" for routing
            // (BusinessUnit.DefaultLeadOwnerUserId), so the same answer is used here rather than
            // inventing a second rule. If the tenant has not set one there is genuinely nobody to
            // assign to, and that is recorded as a warning instead of being swallowed: no owner is
            // a setup gap the tenant can fix, not a reason to lose the chase.
            var owner = lead?.AssignTo is > 0
                ? lead.AssignTo!.Value
                : await _context.Set<BusinessUnit>().AsNoTracking()
                    .Where(x => x.Id == quote.BusinessUnitId)
                    .Select(x => x.DefaultLeadOwnerUserId)
                    .SingleOrDefaultAsync(ct) ?? 0;

            if (owner <= 0)
            {
                _logger?.LogWarning(
                    "Quote {QuoteId} was sent on business unit {BusinessUnitId} but neither its lead nor the " +
                    "business unit names an owner, so no follow-up was created. Set a fallback lead owner in " +
                    "Setup so sent quotes are always chased.",
                    quote.Id, quote.BusinessUnitId);
                return;
            }
            var actor = string.IsNullOrWhiteSpace(options.RequestedBy) ? "system:quote-send" : options.RequestedBy.Trim();
            var correlation = $"quote-send:{quote.Id}";
            await _sales.AppendActivityAsync(quote.BusinessUnitId, new AppendCommercialActivityCommand(
                owner, CommercialActivityType.QuoteSent, "Quote", quote.Id,
                lead?.CustomerId, null, quote.SentOn.Value, "SENT", $"quote:{quote.Id}:sent",
                actor, correlation, $"quote:{quote.Id}:sent-activity"), ct);
            var staleDays = await _context.Set<SlaPolicy>().AsNoTracking()
                .Where(x => x.BusinessUnitId == quote.BusinessUnitId).Select(x => (int?)x.StaleQuoteDays)
                .SingleOrDefaultAsync(ct) ?? SlaPolicy.Default(quote.BusinessUnitId).StaleQuoteDays;
            await _sales.CreateFollowUpAsync(quote.BusinessUnitId, new CreateFollowUpTaskCommand(
                owner, "Quote", quote.Id, lead?.CustomerId,
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
                    // The revision re-derives its own tax through CalculateQuoteTotals below; only
                    // the user's stated TREATMENT carries forward, because that is a commercial
                    // decision about the supply and not a number.
                    TaxCategory = i.TaxCategory,
                    TaxCategoryReason = i.TaxCategoryReason,
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

        // ==================================================================
        // Reasoned quote-validity extension (Decision Register R7)
        // ==================================================================

        /// <summary>Bound on the recorded reason — the same 500 chars as Quote.OutcomeNote.</summary>
        private const int MaxValidityReasonLength = 500;

        /// <summary>
        /// Sentinel guard shared with <c>SlaSweepWorker</c>: the extraction pipeline represents an
        /// unknown date as year 0001/1900, so anything before 2000 is not a date a human chose.
        /// </summary>
        private static readonly DateTime EarliestCommercialValidity = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Validity extension. The whole call is the retriable unit: it opens its own transaction
        /// partway down, and <c>NpgsqlRetryingExecutionStrategy</c> (Program.cs
        /// <c>EnableRetryOnFailure</c>) refuses a transaction opened outside a strategy delegate —
        /// so this threw "does not support user-initiated transactions" on every PostgreSQL request.
        /// </summary>
        public Task<QuoteValidityExtensionResultDTO> ExtendQuoteValidityAsync(
            long quoteId, long businessUnitId, DateTime newValidUntil, string reason,
            string actor, long? actorUserId, string idempotencyKey, CancellationToken ct = default)
        {
            if (!_context.Database.IsRelational() || _context.Database.CurrentTransaction is not null)
                return ExtendQuoteValidityCoreAsync(
                    quoteId, businessUnitId, newValidUntil, reason, actor, actorUserId, idempotencyKey, ct);

            var strategy = _context.Database.CreateExecutionStrategy();
            return strategy.ExecuteAsync(() =>
            {
                _context.ChangeTracker.Clear();
                return ExtendQuoteValidityCoreAsync(
                    quoteId, businessUnitId, newValidUntil, reason, actor, actorUserId, idempotencyKey, ct);
            });
        }

        private async Task<QuoteValidityExtensionResultDTO> ExtendQuoteValidityCoreAsync(
            long quoteId, long businessUnitId, DateTime newValidUntil, string reason,
            string actor, long? actorUserId, string idempotencyKey, CancellationToken ct = default)
        {
            if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
            if (string.IsNullOrWhiteSpace(actor))
                throw new ArgumentException("Authenticated actor is required.", nameof(actor));
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));

            // R7: "a reason nobody can read later is not a reason". Trimmed and bounded here,
            // and refused by a CHECK constraint in the database as well.
            var trimmedReason = (reason ?? string.Empty).Trim();
            if (trimmedReason.Length == 0)
                throw new ArgumentException(
                    "Enter why the validity is being extended — for example \"buyer requested a two-week hold " +
                    "while the technical evaluation completes\". The reason is recorded against the quote.",
                    nameof(reason));
            if (trimmedReason.Length > MaxValidityReasonLength)
                throw new ArgumentException(
                    $"The reason cannot be longer than {MaxValidityReasonLength} characters.", nameof(reason));

            var key = idempotencyKey.Trim();
            if (key.Length > 160) key = key[..160];

            var now = DateTime.UtcNow;
            var requested = newValidUntil.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(newValidUntil, DateTimeKind.Utc)
                : newValidUntil.ToUniversalTime();
            if (requested < EarliestCommercialValidity)
                throw new ArgumentException(
                    "Enter a real validity date. The date supplied is not one a person chose.", nameof(newValidUntil));
            if (requested <= now)
                throw new ArgumentException(
                    "The new validity date must be in the future — a date that has already passed would be " +
                    "expired by the next sweep, which is not an extension.", nameof(newValidUntil));

            // Same concurrency shape as ReviseQuoteAsync: a per-tenant advisory lock plus a row
            // lock on the quote, so two reps extending the same bid at once serialise rather than
            // racing each other's ValidUntil write.
            var isolation = _context.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;
            await using var transaction = await _context.Database.BeginTransactionAsync(isolation, ct);
            if (_context.Database.IsNpgsql())
            {
                await _context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({$"quote-validity:{businessUnitId}:{quoteId}"}, 0))", ct);
                await _context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"Quotes\" WHERE \"BusinessUnitID\" = {businessUnitId} AND \"ID\" = {quoteId} FOR UPDATE", ct);
            }

            var quote = await _context.Quotes
                .Include(q => q.Status)
                .FirstOrDefaultAsync(q => q.Id == quoteId && q.BusinessUnitId == businessUnitId, ct)
                ?? throw new KeyNotFoundException($"Quote with ID {quoteId} not found.");

            // Replay: the same command arriving twice records one extension, not two.
            var replay = await _context.QuoteValidityExtensions.AsNoTracking()
                .FirstOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == key, ct);
            if (replay is not null)
            {
                if (replay.QuoteId != quoteId)
                    throw new InvalidOperationException(
                        "That idempotency key was already used to extend a different quote.");
                await transaction.CommitAsync(ct);
                return Result(quote, replay, replayed: true);
            }

            if (await IsQuoteInDraftAsync(quote))
                throw new InvalidOperationException(
                    $"Quote '{quote.QuoteNo}' has not been issued to the customer yet. Set its validity date " +
                    "directly on the draft — there is nothing to extend until the quote has been sent.");

            var statusCode = LifecyclePolicy.Canonicalize("Quote", quote.Status?.SetupCode, quote.Status?.SetupValue);
            if (quote.OutcomeOn.HasValue || statusCode is not "SENT")
                throw new InvalidOperationException(
                    $"Quote '{quote.QuoteNo}' is no longer live ({FriendlyState(statusCode, quote.OutcomeOn)}). " +
                    "Validity can only be extended on a quote that has been sent and is still awaiting " +
                    "the customer's decision.");

            var successor = await _context.Quotes.AsNoTracking()
                .Where(q => q.RevisionOfQuoteId == quoteId && q.BusinessUnitId == businessUnitId)
                .Select(q => new { q.QuoteNo, q.RevisionNo })
                .FirstOrDefaultAsync(ct);
            if (successor is not null)
                throw new InvalidOperationException(
                    $"Quote '{quote.QuoteNo}' has been superseded by revision '{successor.QuoteNo}' " +
                    $"(Rev {successor.RevisionNo}). Extend the validity of the latest revision instead.");

            // "Extend" means later. Silently accepting an earlier date under a control the rep
            // pressed to hold the price open would shorten the offer they meant to lengthen.
            if (quote.ValidUntil.HasValue
                && quote.ValidUntil.Value >= EarliestCommercialValidity
                && requested <= quote.ValidUntil.Value)
                throw new InvalidOperationException(
                    $"Quote '{quote.QuoteNo}' is already valid until {quote.ValidUntil.Value:dd MMM yyyy}. " +
                    "Choose a later date to extend it.");

            var extension = new QuoteValidityExtension
            {
                BusinessUnitId = businessUnitId,
                QuoteId = quoteId,
                PreviousValidUntil = quote.ValidUntil,
                NewValidUntil = requested,
                Reason = trimmedReason,
                ExtendedByUserId = actorUserId,
                ExtendedBy = actor.Trim().Length <= 255 ? actor.Trim() : actor.Trim()[..255],
                ExtendedOn = now,
                IdempotencyKey = key
            };
            _context.QuoteValidityExtensions.Add(extension);

            quote.ValidUntil = requested;
            quote.ValidityExtendedOn = now;
            quote.ModifiedBy = extension.ExtendedBy;
            quote.ModifiedDate = now;
            // RevisionNo, RevisionOfQuoteId, StatusId, CurrencyId, TotalAmount and every line are
            // deliberately untouched. The commercial offer did not change — only its expiry did —
            // and a bumped revision number reads to the customer as a brand-new offer.

            await _context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return Result(quote, extension, replayed: false);

            static string FriendlyState(string statusCode, DateTime? outcomeOn) => statusCode switch
            {
                "ACCEPTED" or "ORDERED" => "it has been won",
                "REJECTED" => "it has been closed as lost or cancelled",
                "EXPIRED" => "it has expired",
                _ when outcomeOn.HasValue => "a final outcome has been recorded",
                _ => $"its status is {statusCode}"
            };

            static QuoteValidityExtensionResultDTO Result(Quote quote, QuoteValidityExtension row, bool replayed) => new()
            {
                QuoteId = quote.Id,
                QuoteNo = quote.QuoteNo,
                ValidUntil = quote.ValidUntil,
                ValidityExtendedOn = quote.ValidityExtendedOn,
                RevisionNo = quote.RevisionNo,
                Replayed = replayed,
                Extension = Map(row)
            };
        }

        public async Task<IReadOnlyList<QuoteValidityExtensionDTO>> GetValidityExtensionsAsync(
            long quoteId, long businessUnitId, CancellationToken ct = default)
        {
            if (!await _context.Quotes.AsNoTracking()
                    .AnyAsync(q => q.Id == quoteId && q.BusinessUnitId == businessUnitId, ct))
                throw new KeyNotFoundException($"Quote with ID {quoteId} not found.");

            return await _context.QuoteValidityExtensions.AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && x.QuoteId == quoteId)
                .OrderByDescending(x => x.ExtendedOn).ThenByDescending(x => x.Id)
                .Select(x => new QuoteValidityExtensionDTO
                {
                    Id = x.Id,
                    QuoteId = x.QuoteId,
                    PreviousValidUntil = x.PreviousValidUntil,
                    NewValidUntil = x.NewValidUntil,
                    Reason = x.Reason,
                    ExtendedBy = x.ExtendedBy,
                    ExtendedOn = x.ExtendedOn
                })
                .ToListAsync(ct);
        }

        private static QuoteValidityExtensionDTO Map(QuoteValidityExtension row) => new()
        {
            Id = row.Id,
            QuoteId = row.QuoteId,
            PreviousValidUntil = row.PreviousValidUntil,
            NewValidUntil = row.NewValidUntil,
            Reason = row.Reason,
            ExtendedBy = row.ExtendedBy,
            ExtendedOn = row.ExtendedOn
        };

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

        /// <summary>
        /// Resolves open revision impacts. Wrapped in the configured execution strategy for the
        /// same reason as <see cref="ExtendQuoteValidityAsync"/>: it opens a transaction of its own,
        /// which <c>NpgsqlRetryingExecutionStrategy</c> refuses outside a strategy delegate.
        /// </summary>
        public Task ResolveRevisionImpactAsync(long quoteId, long businessUnitId, string actor,
            string idempotencyKey, CancellationToken ct = default)
        {
            if (!_context.Database.IsRelational() || _context.Database.CurrentTransaction is not null)
                return ResolveRevisionImpactCoreAsync(quoteId, businessUnitId, actor, idempotencyKey, ct);

            var strategy = _context.Database.CreateExecutionStrategy();
            return strategy.ExecuteAsync(() =>
            {
                _context.ChangeTracker.Clear();
                return ResolveRevisionImpactCoreAsync(quoteId, businessUnitId, actor, idempotencyKey, ct);
            });
        }

        private async Task ResolveRevisionImpactCoreAsync(long quoteId, long businessUnitId, string actor,
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
