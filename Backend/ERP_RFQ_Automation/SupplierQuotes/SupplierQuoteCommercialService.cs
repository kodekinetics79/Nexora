using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.SupplierQuotes;

public sealed class SupplierQuoteCommercialService(ErpRfqAutomationContext context)
{
    public async Task<SupplierQuoteProjectionResult> ProjectAsync(ProjectSupplierQuoteCommand command,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(command.BusinessUnitId);
        Required(command.IdempotencyKey, nameof(command.IdempotencyKey));
        Required(command.Actor, nameof(command.Actor));
        Required(command.CorrelationId, nameof(command.CorrelationId));
        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            var quote = await context.SupplierQuotes
                .Include(x => x.Revisions).ThenInclude(x => x.Lines)
                .Include(x => x.Revisions).ThenInclude(x => x.Evidence)
                .Include(x => x.Revisions).ThenInclude(x => x.ReviewDecisions)
                .SingleOrDefaultAsync(x => x.BusinessUnitId == command.BusinessUnitId &&
                    x.Id == command.SupplierQuoteId, cancellationToken)
                ?? throw new SupplierQuoteNotFoundException("The Supplier Quote was not found in this tenant.");
            if (quote.Version != command.ExpectedVersion)
                throw new SupplierQuoteConflictException("The Supplier Quote changed; reload before comparison.");
            if (quote.InboxStatus != SupplierQuoteInboxStatuses.ReadyForComparison)
                throw new SupplierQuoteValidationException("Complete critical field review before comparison.");

            var revision = quote.Revisions.Single(x => x.RevisionNumber == quote.CurrentRevisionNumber);
            var correctedByEvidence = revision.ReviewDecisions
                .GroupBy(x => x.SupplierQuoteFieldEvidenceId)
                .Select(x => x.OrderByDescending(y => y.ReviewedOn).ThenByDescending(y => y.Id).First())
                .Where(x => x.Status == SupplierQuoteReviewStatuses.Corrected)
                .ToDictionary(x => x.SupplierQuoteFieldEvidenceId);
            string? Corrected(long? lineId, string fieldName) => revision.Evidence
                .Where(x => x.SupplierQuoteLineId == lineId && x.FieldName.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Id).Select(x => correctedByEvidence.GetValueOrDefault(x.Id)?.CorrectedValue)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            decimal EffectiveDecimal(SupplierQuoteLine line, string fieldName, decimal original) =>
                Corrected(line.Id, fieldName) is { } value
                    ? decimal.Parse(value, System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture) : original;
            decimal? EffectiveNullableDecimal(SupplierQuoteLine line, string fieldName, decimal? original) =>
                Corrected(line.Id, fieldName) is { } value
                    ? decimal.Parse(value, System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture) : original;
            int? EffectiveInt(SupplierQuoteLine line, string fieldName, int? original) =>
                Corrected(line.Id, fieldName) is { } value
                    ? int.Parse(value, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture) : original;
            var effectiveValidUntil = Corrected(null, "ValidUntil") is { } correctedValidity
                ? DateTime.Parse(correctedValidity, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind) : revision.ValidUntil;
            var effectiveCurrencyId = Corrected(null, "CurrencyId") is { } correctedCurrency
                ? long.Parse(correctedCurrency, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture) : revision.CurrencyId;
            if (effectiveValidUntil is null || effectiveValidUntil <= DateTime.UtcNow)
                throw new SupplierQuoteValidationException("A current, unexpired Supplier Quote validity date is required.");
            if (revision.Lines.Count == 0)
                throw new SupplierQuoteValidationException("The Supplier Quote has no commercial lines.");

            var replayRows = await context.SupplierQuotedItems
                .Where(x => x.BusinessUnitId == command.BusinessUnitId &&
                    x.SourceSupplierQuoteRevisionId == revision.Id)
                .OrderBy(x => x.Id).ToArrayAsync(cancellationToken);
            if (replayRows.Length > 0)
            {
                var lastProjection = replayRows.Max(x => x.ModifiedDate ?? x.CreatedDate);
                var latestCorrection = revision.ReviewDecisions
                    .Where(x => x.Status == SupplierQuoteReviewStatuses.Corrected)
                    .Select(x => (DateTime?)x.ReviewedOn).Max();
                if (!latestCorrection.HasValue || latestCorrection <= lastProjection)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new SupplierQuoteProjectionResult(quote.Id, revision.Id,
                        replayRows.Select(x => x.Id).ToArray(), true);
                }
                var replayIds = replayRows.Select(x => x.Id).ToArray();
                if (await context.Set<SourcingAward>().AsNoTracking().AnyAsync(x =>
                        x.BusinessUnitId == command.BusinessUnitId && x.SupplierQuotedItemId.HasValue &&
                        replayIds.Contains(x.SupplierQuotedItemId.Value) && x.Status != "CANCELLED" &&
                        x.Status != "REJECTED", cancellationToken))
                    throw new SupplierQuoteValidationException(
                        "A selected Supplier offer cannot be corrected in place; capture a new Supplier Quote revision.");
                if (replayRows.Length != revision.Lines.Count)
                    throw new SupplierQuoteConflictException(
                        "The commercial projection no longer matches the canonical Supplier Quote revision.");

                var totalCorrectedQuantity = revision.Lines.Sum(x => EffectiveDecimal(x, "Quantity", x.Quantity));
                var correctedOn = DateTime.UtcNow;
                foreach (var line in revision.Lines)
                {
                    var row = replayRows.SingleOrDefault(x => x.SourceSupplierQuoteLineId == line.Id)
                        ?? throw new SupplierQuoteConflictException(
                            "The commercial projection lost canonical Supplier Quote line identity.");
                    var quantity = EffectiveDecimal(line, "Quantity", line.Quantity);
                    var unitPrice = EffectiveDecimal(line, "UnitPrice", line.UnitPrice);
                    var freight = decimal.Round(revision.FreightAmount * quantity / totalCorrectedQuantity, 4);
                    var tax = decimal.Round(revision.TaxAmount * quantity / totalCorrectedQuantity, 4);
                    row.Quantity = quantity;
                    row.UnitPrice = unitPrice;
                    row.CurrencyId = effectiveCurrencyId;
                    row.LeadTimeDays = EffectiveInt(line, "LeadTimeDays", line.LeadTimeDays);
                    row.AvailableQuantity = EffectiveNullableDecimal(line, "AvailableQuantity", line.AvailableQuantity);
                    row.FreightCost = freight;
                    row.TaxAmount = tax;
                    row.LandedUnitCost = decimal.Round((unitPrice * quantity + freight + tax) / quantity, 4);
                    row.ValidUntil = effectiveValidUntil;
                    row.RequestHash = Hash(new { RevisionId = revision.Id, LineId = line.Id,
                        CorrectionReviewedOn = latestCorrection.Value });
                    row.ModifiedBy = command.Actor.Trim();
                    row.ModifiedDate = correctedOn;
                    row.Version++;
                }
                context.ProcurementEvents.Add(new ProcurementEvent
                {
                    BusinessUnitId = command.BusinessUnitId,
                    AggregateType = "SupplierQuote",
                    AggregateId = quote.Id,
                    AggregateVersion = quote.Version,
                    EventType = "SUPPLIER_QUOTE_PROJECTION_CORRECTED",
                    Actor = command.Actor.Trim(),
                    CorrelationId = command.CorrelationId.Trim(),
                    IdempotencyKey = $"{command.IdempotencyKey.Trim()}:correction:{latestCorrection.Value.Ticks}",
                    PayloadJson = JsonSerializer.Serialize(new { RevisionId = revision.Id,
                        LineIds = replayRows.Select(x => x.Id) }),
                    OccurredOn = correctedOn
                });
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new SupplierQuoteProjectionResult(quote.Id, revision.Id,
                    replayRows.Select(x => x.Id).ToArray(), false);
            }

            var rfqLines = await context.Rfqitems.Where(x => x.Rfqid == quote.RfqId &&
                    revision.Lines.Select(line => line.RfqItemId).Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, cancellationToken);
            if (rfqLines.Count != revision.Lines.Count)
                throw new SupplierQuoteValidationException("Every Supplier Quote line must retain its RFQ line.");
            var supplier = await context.Suppliers.AsNoTracking().SingleAsync(x =>
                x.Buid == command.BusinessUnitId && x.Id == quote.SupplierId, cancellationToken);
            var prior = await context.SupplierQuotedItems.Where(x => x.BusinessUnitId == command.BusinessUnitId &&
                x.SourceSupplierQuoteId == quote.Id && x.IsActive).ToListAsync(cancellationToken);
            foreach (var row in prior)
            {
                row.IsActive = false;
                row.ModifiedBy = command.Actor.Trim();
                row.ModifiedDate = DateTime.UtcNow;
                row.Version++;
            }

            var totalQuantity = revision.Lines.Sum(x => EffectiveDecimal(x, "Quantity", x.Quantity));
            var now = DateTime.UtcNow;
            var rows = revision.Lines.OrderBy(x => x.LineNumber).Select(line =>
            {
                var quantity = EffectiveDecimal(line, "Quantity", line.Quantity);
                var unitPrice = EffectiveDecimal(line, "UnitPrice", line.UnitPrice);
                var available = EffectiveNullableDecimal(line, "AvailableQuantity", line.AvailableQuantity);
                var leadTime = EffectiveInt(line, "LeadTimeDays", line.LeadTimeDays);
                var freight = decimal.Round(revision.FreightAmount * quantity / totalQuantity, 4);
                var tax = decimal.Round(revision.TaxAmount * quantity / totalQuantity, 4);
                var landed = decimal.Round((unitPrice * quantity + freight + tax) / quantity, 4);
                var rfqLine = rfqLines[line.RfqItemId];
                return new SupplierQuotedItem
                {
                    BusinessUnitId = command.BusinessUnitId,
                    SupplierId = quote.SupplierId,
                    SupplierSolicitationId = quote.SupplierSolicitationId,
                    RfqId = quote.RfqId,
                    RfqItemId = line.RfqItemId,
                    ProductId = rfqLine.ProductId,
                    SourceSupplierQuoteId = quote.Id,
                    SourceSupplierQuoteRevisionId = revision.Id,
                    SourceSupplierQuoteLineId = line.Id,
                    CommercialDemandLineId = line.CommercialDemandLineId,
                    SourcingCaseId = quote.SourcingCaseId,
                    ItemName = line.PartNumber ?? rfqLine.ProductShortName ?? rfqLine.ItemMaterialCode,
                    Description = line.Description,
                    Quantity = quantity,
                    UnitPrice = unitPrice,
                    CurrencyId = effectiveCurrencyId,
                    QuoteReference = quote.SupplierQuoteReference,
                    LeadTimeDays = leadTime,
                    AvailableQuantity = available,
                    FreightCost = freight,
                    TaxAmount = tax,
                    DutyCost = 0,
                    OtherCost = 0,
                    DiscountAmount = 0,
                    MinimumOrderQuantity = line.MinimumOrderQuantity,
                    ReliabilitySnapshot = supplier.SuccessRate,
                    LandedUnitCost = landed,
                    QuoteRevision = revision.RevisionNumber,
                    ResponseIdempotencyKey = $"canonical:{revision.Id}:{line.Id}",
                    RequestHash = Hash(new { RevisionId = revision.Id, LineId = line.Id, command.IdempotencyKey }),
                    QuoteDate = revision.CapturedOn,
                    ValidUntil = effectiveValidUntil,
                    CreatedBy = command.Actor.Trim(),
                    CreatedDate = now,
                    IsActive = true
                };
            }).ToArray();
            context.SupplierQuotedItems.AddRange(rows);
            await context.SaveChangesAsync(cancellationToken);
            context.ProcurementEvents.Add(new ProcurementEvent
            {
                BusinessUnitId = command.BusinessUnitId,
                AggregateType = "SupplierQuote",
                AggregateId = quote.Id,
                AggregateVersion = quote.Version,
                EventType = "SUPPLIER_QUOTE_PROJECTED_FOR_COMPARISON",
                Actor = command.Actor.Trim(),
                CorrelationId = command.CorrelationId.Trim(),
                IdempotencyKey = command.IdempotencyKey.Trim(),
                PayloadJson = JsonSerializer.Serialize(new { RevisionId = revision.Id, LineIds = rows.Select(x => x.Id) }),
                OccurredOn = now
            });
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new SupplierQuoteProjectionResult(quote.Id, revision.Id, rows.Select(x => x.Id).ToArray(), false);
        });
    }

    public async Task<CustomerQuotePricingResult> ApplyPricingAsync(ApplyCustomerQuotePricingCommand command,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(command.BusinessUnitId);
        if (command.TargetMarginPercent is < 0 or >= 95)
            throw new SupplierQuoteValidationException("Target margin must be between 0 and 95 percent.");
        Required(command.Rationale, nameof(command.Rationale));
        var requestHash = Hash(new { command.QuoteItemId, command.SourcingAwardId,
            command.TargetMarginPercent, Rationale = command.Rationale.Trim() });
        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            var existing = await context.CustomerQuoteSourcingDecisions.AsNoTracking().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.IdempotencyKey == command.IdempotencyKey,
                cancellationToken);
            if (existing is not null)
            {
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(existing.RequestHash), Convert.FromHexString(requestHash)))
                    throw new SupplierQuoteConflictException("The idempotency key was used for another pricing decision.");
                await transaction.CommitAsync(cancellationToken);
                return ToResult(existing, true);
            }

            var quoteItem = await context.QuoteItems.Include(x => x.Quote).ThenInclude(x => x.Status)
                .SingleOrDefaultAsync(x => x.Id == command.QuoteItemId &&
                    x.Quote.BusinessUnitId == command.BusinessUnitId, cancellationToken)
                ?? throw new SupplierQuoteNotFoundException("The Customer Quote line was not found in this tenant.");
            if (!string.Equals(quoteItem.Quote.Status?.SetupCode, "DRAFT", StringComparison.OrdinalIgnoreCase) &&
                quoteItem.Quote.StatusId != 42)
                throw new SupplierQuoteValidationException("Supplier pricing can only be applied to a draft Customer Quote.");
            var award = await context.Set<SourcingAward>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.Id == command.SourcingAwardId &&
                x.RfqItemId == quoteItem.RfqitemId && (x.Status == "APPROVED" || x.Status == "SPLIT_APPROVED"),
                cancellationToken) ?? throw new SupplierQuoteValidationException(
                    "An approved Supplier award for this Customer Quote line is required.");
            if (award.Quantity != quoteItem.Quantity)
                throw new SupplierQuoteValidationException(
                    "Automated pricing requires one approved Supplier award covering the full Customer Quote line; split stock or multi-award fulfilment requires an approved blended-cost decision.");
            var projected = await context.SupplierQuotedItems.AsNoTracking().SingleAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.Id == award.SupplierQuotedItemId && x.IsActive,
                cancellationToken);
            if (projected.ValidUntil <= DateTime.UtcNow || projected.SourceSupplierQuoteId is null ||
                projected.SourceSupplierQuoteRevisionId is null || projected.SourceSupplierQuoteLineId is null ||
                projected.CommercialDemandLineId is null || projected.SourcingCaseId is null)
                throw new SupplierQuoteValidationException("The award must retain current canonical Supplier Quote lineage.");
            var reviewDecisions = await context.SupplierQuoteReviewDecisions.AsNoTracking().Where(x =>
                    x.BusinessUnitId == command.BusinessUnitId &&
                    x.SupplierQuoteRevisionId == projected.SourceSupplierQuoteRevisionId.Value)
                .OrderByDescending(x => x.ReviewedOn).ThenByDescending(x => x.Id)
                .ToArrayAsync(cancellationToken);
            var latestCorrection = reviewDecisions.GroupBy(x => x.SupplierQuoteFieldEvidenceId)
                .Select(x => x.First()).Where(x => x.Status == SupplierQuoteReviewStatuses.Corrected)
                .Select(x => (DateTime?)x.ReviewedOn).Max();
            if (latestCorrection > (projected.ModifiedDate ?? projected.CreatedDate))
                throw new SupplierQuoteValidationException(
                    "The awarded Supplier offer has a newer correction; capture and approve a new Supplier Quote revision before pricing.");
            if (projected.AvailableQuantity < award.Quantity || projected.Quantity < award.Quantity)
                throw new SupplierQuoteValidationException("The current Supplier offer no longer covers the awarded quantity.");
            var canonicalQuote = await context.SupplierQuotes.AsNoTracking().SingleAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.Id == projected.SourceSupplierQuoteId.Value,
                cancellationToken);
            if (canonicalQuote.InboxStatus != SupplierQuoteInboxStatuses.ReadyForComparison)
                throw new SupplierQuoteValidationException(
                    "The Supplier Quote is no longer ready for commercial use; complete its latest evidence review.");
            var supplier = await context.Suppliers.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Buid == command.BusinessUnitId && x.Id == projected.SupplierId && x.IsActive != false,
                cancellationToken);
            if (supplier is null || supplier.GovernanceStatus is not (SupplierGovernanceStatuses.Approved or
                    SupplierGovernanceStatuses.Preferred or SupplierGovernanceStatuses.Provisional) ||
                supplier.ReadinessStatus != SupplierReadinessStatuses.Ready ||
                supplier.ComplianceStatus is SupplierComplianceStatuses.Blocked or SupplierComplianceStatuses.Failed or
                    SupplierComplianceStatuses.Restricted ||
                supplier.RiskStatus is SupplierRiskStatuses.Blocked or SupplierRiskStatuses.High)
                throw new SupplierQuoteValidationException("The Supplier is no longer eligible for commercial use.");
            var rfq = await context.Rfqs.AsNoTracking().SingleAsync(x => x.BusinessUnitId == command.BusinessUnitId &&
                x.Id == quoteItem.Quote.Rfqid, cancellationToken);
            if (string.IsNullOrWhiteSpace(rfq.NexoraSerial))
                throw new SupplierQuoteValidationException("The Customer Quote RFQ has no Nexora Serial lineage.");
            if (quoteItem.Quote.CurrencyId.HasValue && quoteItem.Quote.CurrencyId != projected.CurrencyId)
                throw new SupplierQuoteValidationException("Customer Quote and Supplier award currencies differ; record an approved exchange rate before pricing.");
            var requiredOn = await context.Rfqitems.AsNoTracking().Where(x => x.Id == quoteItem.RfqitemId &&
                    x.Rfq.BusinessUnitId == command.BusinessUnitId)
                .Select(x => x.RequiredDesiredDate).SingleAsync(cancellationToken);
            if (requiredOn.HasValue && (!projected.QuoteDate.HasValue || !projected.LeadTimeDays.HasValue ||
                    projected.QuoteDate.Value.AddDays(projected.LeadTimeDays.Value) > requiredOn.Value))
                throw new SupplierQuoteValidationException("The Supplier offer no longer meets the Customer required delivery date.");

            var landed = award.LandedUnitCost ?? throw new SupplierQuoteValidationException("The award has no landed-cost evidence.");
            var customerUnitPrice = decimal.Round(landed / (1m - command.TargetMarginPercent / 100m), 6,
                MidpointRounding.AwayFromZero);
            var decision = new CustomerQuoteSourcingDecision
            {
                BusinessUnitId = command.BusinessUnitId,
                QuoteId = quoteItem.QuoteId,
                QuoteItemId = quoteItem.Id,
                RfqId = rfq.Id,
                RfqItemId = quoteItem.RfqitemId!.Value,
                CommercialDemandLineId = projected.CommercialDemandLineId.Value,
                SourcingCaseId = projected.SourcingCaseId.Value,
                SourcingAwardId = award.Id,
                SupplierQuotedItemId = projected.Id,
                SupplierQuoteId = projected.SourceSupplierQuoteId.Value,
                SupplierQuoteRevisionId = projected.SourceSupplierQuoteRevisionId.Value,
                SupplierQuoteLineId = projected.SourceSupplierQuoteLineId.Value,
                NexoraSerial = rfq.NexoraSerial,
                Quantity = award.Quantity!.Value,
                SupplierLandedUnitCost = landed,
                TargetMarginPercent = command.TargetMarginPercent,
                CustomerUnitPrice = customerUnitPrice,
                CurrencyId = projected.CurrencyId!.Value,
                IdempotencyKey = Required(command.IdempotencyKey, nameof(command.IdempotencyKey)),
                RequestHash = requestHash,
                Rationale = command.Rationale.Trim(),
                CreatedOn = DateTime.UtcNow,
                CreatedBy = Required(command.Actor, nameof(command.Actor)),
                CorrelationId = Required(command.CorrelationId, nameof(command.CorrelationId))
            };
            quoteItem.UnitPrice = customerUnitPrice;
            quoteItem.TotalAmount = decimal.Round(quoteItem.Quantity * customerUnitPrice - (quoteItem.Discount ?? 0) +
                (quoteItem.TaxAmount ?? 0), 2, MidpointRounding.AwayFromZero);
            quoteItem.ModifiedBy = command.Actor.Trim();
            quoteItem.ModifiedDate = DateTime.UtcNow;
            quoteItem.Quote.CurrencyId ??= projected.CurrencyId;
            var otherLineTotal = await context.QuoteItems.Where(x => x.QuoteId == quoteItem.QuoteId &&
                x.Id != quoteItem.Id).SumAsync(x => x.TotalAmount, cancellationToken);
            quoteItem.Quote.TotalAmount = otherLineTotal + quoteItem.TotalAmount;
            quoteItem.Quote.ModifiedBy = command.Actor.Trim();
            quoteItem.Quote.ModifiedDate = DateTime.UtcNow;
            context.CustomerQuoteSourcingDecisions.Add(decision);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ToResult(decision, false);
        });
    }

    private void EnsureTenant(long businessUnitId)
    {
        if (businessUnitId <= 0 || context.ScopedTenantId != businessUnitId)
            throw new UnauthorizedAccessException("The authenticated tenant context is required.");
    }

    private static string Required(string value, string name)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > 1000)
            throw new SupplierQuoteValidationException($"{name} is required and is too long.");
        return trimmed;
    }

    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();
    private static CustomerQuotePricingResult ToResult(CustomerQuoteSourcingDecision x, bool replayed) =>
        new(x.Id, x.QuoteId, x.QuoteItemId, x.SupplierLandedUnitCost, x.TargetMarginPercent,
            x.CustomerUnitPrice, x.NexoraSerial, replayed);
}
