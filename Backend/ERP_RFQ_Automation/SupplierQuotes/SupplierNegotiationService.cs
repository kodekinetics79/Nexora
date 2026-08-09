using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.SupplierQuotes;

public sealed class SupplierNegotiationService(ErpRfqAutomationContext context)
{
    internal const string PolicyVersion = "supplier-negotiation-shadow-v1";
    internal const int MaxEvidenceFacts = 100;
    internal const int MaxPriorDecisions = 100;
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Dispositions = new(StringComparer.OrdinalIgnoreCase)
    {
        SupplierNegotiationDispositions.Prepared,
        SupplierNegotiationDispositions.Deferred,
        SupplierNegotiationDispositions.Dismissed
    };

    public async Task<SupplierNegotiationWorkspace> GetAsync(long businessUnitId, long supplierQuoteId,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        var input = await LoadInputAsync(businessUnitId, supplierQuoteId, cancellationToken);
        return BuildWorkspace(input);
    }

    public async Task<SupplierNegotiationDecisionView> DecideAsync(SupplierNegotiationCommand command,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(command.BusinessUnitId);
        if (command.ExpectedQuoteVersion <= 0)
            throw new SupplierQuoteValidationException("Expected quote version must be positive.");
        var recommendationCode = Required(command.RecommendationCode, 64, nameof(command.RecommendationCode))
            .ToUpperInvariant();
        var disposition = Required(command.Disposition, 24, nameof(command.Disposition)).ToUpperInvariant();
        if (!Dispositions.Contains(disposition))
            throw new SupplierQuoteValidationException("Disposition must be PREPARED, DEFERRED, or DISMISSED.");
        var reason = Required(command.Reason, 1000, nameof(command.Reason));
        var idempotencyKey = Required(command.IdempotencyKey, 160, nameof(command.IdempotencyKey));
        var actor = Required(command.Actor, 255, nameof(command.Actor));
        var correlationId = Required(command.CorrelationId, 160, nameof(command.CorrelationId));
        var requestHash = Hash(new
        {
            command.SupplierQuoteId,
            command.ExpectedQuoteVersion,
            RecommendationCode = recommendationCode,
            Disposition = disposition,
            Reason = reason
        });

        var replay = await context.SupplierNegotiationDecisions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.BusinessUnitId == command.BusinessUnitId && x.IdempotencyKey == idempotencyKey,
            cancellationToken);
        if (replay is not null) return Replay(replay, requestHash);

        var strategy = context.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                context.ChangeTracker.Clear();
                await using var transaction = await context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable, cancellationToken);
                var concurrentReplay = await context.SupplierNegotiationDecisions.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.BusinessUnitId == command.BusinessUnitId &&
                        x.IdempotencyKey == idempotencyKey, cancellationToken);
                if (concurrentReplay is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return Replay(concurrentReplay, requestHash);
                }

                var input = await LoadInputAsync(command.BusinessUnitId, command.SupplierQuoteId,
                    cancellationToken, trackingQuote: true);
                if (input.Quote.Version != command.ExpectedQuoteVersion)
                    throw new SupplierQuoteConflictException(
                        "The Supplier Quote changed; reload negotiation guidance before recording a decision.");
                var workspace = BuildWorkspace(input);
                var recommendation = workspace.Recommendations.SingleOrDefault(x =>
                    x.Code.Equals(recommendationCode, StringComparison.Ordinal));
                if (recommendation is null)
                    throw new SupplierQuoteValidationException(
                        "The requested recommendation is not supported by the current Supplier Quote evidence.");
                if (disposition == SupplierNegotiationDispositions.Prepared &&
                    workspace.BidFlags.Any(x => x.Blocking))
                    throw new SupplierQuoteValidationException(
                        "A prepared negotiation requires all blocking bid-quality findings to be resolved.");

                var now = DateTime.UtcNow;
                var snapshot = JsonSerializer.Serialize(new
                {
                    schemaVersion = "supplier-negotiation-evidence-v1",
                    mode = "SHADOW",
                    policyVersion = PolicyVersion,
                    supplierQuoteId = input.Quote.Id,
                    supplierQuoteRevisionId = input.CurrentRevision.Id,
                    revisionNumber = input.CurrentRevision.RevisionNumber,
                    expectedQuoteVersion = command.ExpectedQuoteVersion,
                    generatedAtUtc = now,
                    recommendation,
                    bidFlags = workspace.BidFlags
                }, SnapshotJson);
                var decision = new SupplierNegotiationDecision
                {
                    BusinessUnitId = command.BusinessUnitId,
                    SupplierQuoteId = input.Quote.Id,
                    SupplierQuoteRevisionId = input.CurrentRevision.Id,
                    RecommendationCode = recommendation.Code,
                    Disposition = disposition,
                    Reason = reason,
                    EvidenceSnapshotJson = snapshot,
                    PolicyVersion = PolicyVersion,
                    ExpectedQuoteVersion = command.ExpectedQuoteVersion,
                    IdempotencyKey = idempotencyKey,
                    RequestHash = requestHash,
                    Actor = actor,
                    DecidedOn = now,
                    CorrelationId = correlationId
                };
                context.SupplierNegotiationDecisions.Add(decision);
                input.Quote.Version++;
                input.Quote.UpdatedOn = now;
                input.Quote.UpdatedBy = actor;
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return ToView(decision, false, input.Quote.Version);
            });
        }
        catch (Exception error) when (IsConcurrentWriteFailure(error))
        {
            context.ChangeTracker.Clear();
            var raced = await context.SupplierNegotiationDecisions.AsNoTracking().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.IdempotencyKey == idempotencyKey,
                cancellationToken);
            if (raced is not null) return Replay(raced, requestHash);
            throw new SupplierQuoteConflictException(
                "The Supplier Quote changed; reload negotiation guidance before recording a decision.");
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            var raced = await context.SupplierNegotiationDecisions.AsNoTracking().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.IdempotencyKey == idempotencyKey,
                cancellationToken);
            if (raced is not null) return Replay(raced, requestHash);
            throw;
        }
    }

    private async Task<NegotiationInput> LoadInputAsync(long businessUnitId, long supplierQuoteId,
        CancellationToken cancellationToken, bool trackingQuote = false)
    {
        IQueryable<SupplierQuote> quoteQuery = context.SupplierQuotes;
        if (!trackingQuote) quoteQuery = quoteQuery.AsNoTracking();
        var quote = await quoteQuery.SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId &&
            x.Id == supplierQuoteId, cancellationToken)
            ?? throw new SupplierQuoteNotFoundException("The Supplier Quote was not found in this tenant.");
        if (trackingQuote)
        {
            await context.SupplierQuoteRevisions.Where(x => x.BusinessUnitId == businessUnitId &&
                x.SupplierQuoteId == quote.Id).LoadAsync(cancellationToken);
            var revisionIds = quote.Revisions.Select(x => x.Id).ToArray();
            await context.SupplierQuoteLines.Where(x => x.BusinessUnitId == businessUnitId &&
                revisionIds.Contains(x.SupplierQuoteRevisionId)).LoadAsync(cancellationToken);
            await context.SupplierQuoteFieldEvidence.Where(x => x.BusinessUnitId == businessUnitId &&
                revisionIds.Contains(x.SupplierQuoteRevisionId)).LoadAsync(cancellationToken);
            await context.SupplierQuoteReviewDecisions.Where(x => x.BusinessUnitId == businessUnitId &&
                revisionIds.Contains(x.SupplierQuoteRevisionId)).LoadAsync(cancellationToken);
        }
        else
        {
            var revisions = await context.SupplierQuoteRevisions.AsNoTracking().Where(x =>
                x.BusinessUnitId == businessUnitId && x.SupplierQuoteId == quote.Id)
                .ToArrayAsync(cancellationToken);
            var revisionIds = revisions.Select(x => x.Id).ToArray();
            var lines = await context.SupplierQuoteLines.AsNoTracking().Where(x =>
                x.BusinessUnitId == businessUnitId && revisionIds.Contains(x.SupplierQuoteRevisionId))
                .ToArrayAsync(cancellationToken);
            var evidence = await context.SupplierQuoteFieldEvidence.AsNoTracking().Where(x =>
                x.BusinessUnitId == businessUnitId && revisionIds.Contains(x.SupplierQuoteRevisionId))
                .ToArrayAsync(cancellationToken);
            var reviewRows = await context.SupplierQuoteReviewDecisions.AsNoTracking().Where(x =>
                x.BusinessUnitId == businessUnitId && revisionIds.Contains(x.SupplierQuoteRevisionId))
                .ToArrayAsync(cancellationToken);
            foreach (var revision in revisions)
            {
                revision.SupplierQuote = quote;
                revision.Lines = lines.Where(x => x.SupplierQuoteRevisionId == revision.Id).ToList();
                revision.Evidence = evidence.Where(x => x.SupplierQuoteRevisionId == revision.Id).ToList();
                revision.ReviewDecisions = reviewRows.Where(x => x.SupplierQuoteRevisionId == revision.Id).ToList();
                quote.Revisions.Add(revision);
            }
        }
        var currentRevision = quote.Revisions.SingleOrDefault(x =>
            x.RevisionNumber == quote.CurrentRevisionNumber)
            ?? throw new SupplierQuoteConflictException("The current Supplier Quote revision is unavailable.");
        var supplier = await context.Suppliers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Buid == businessUnitId && x.Id == quote.SupplierId, cancellationToken)
            ?? throw new SupplierQuoteNotFoundException("The Supplier was not found in this tenant.");
        var rfqItemIds = currentRevision.Lines.Select(x => x.RfqItemId).Distinct().ToArray();
        var now = DateTime.UtcNow;
        var competitorRows = await (
            from projection in context.SupplierQuotedItems.AsNoTracking()
            join supplierQuote in context.SupplierQuotes.AsNoTracking()
                on new { projection.BusinessUnitId, Id = projection.SourceSupplierQuoteId!.Value }
                equals new { supplierQuote.BusinessUnitId, supplierQuote.Id }
            join revision in context.SupplierQuoteRevisions.AsNoTracking()
                on new
                {
                    projection.BusinessUnitId,
                    SupplierQuoteId = projection.SourceSupplierQuoteId!.Value,
                    Id = projection.SourceSupplierQuoteRevisionId!.Value
                }
                equals new { revision.BusinessUnitId, revision.SupplierQuoteId, revision.Id }
            join line in context.SupplierQuoteLines.AsNoTracking()
                on new { projection.BusinessUnitId, Id = projection.SourceSupplierQuoteLineId!.Value }
                equals new { line.BusinessUnitId, line.Id }
            join candidateSupplier in context.Suppliers.AsNoTracking()
                on new { Id = projection.SupplierId, Buid = (long?)projection.BusinessUnitId }
                equals new { candidateSupplier.Id, candidateSupplier.Buid }
            where projection.BusinessUnitId == businessUnitId && projection.IsActive &&
                  projection.RfqId == quote.RfqId && projection.RfqItemId.HasValue &&
                  rfqItemIds.Contains(projection.RfqItemId.GetValueOrDefault()) &&
                  projection.SourceSupplierQuoteId.HasValue &&
                  projection.SourceSupplierQuoteRevisionId.HasValue &&
                  projection.SourceSupplierQuoteLineId.HasValue &&
                  projection.SourceSupplierQuoteId != quote.Id &&
                  projection.SupplierId != quote.SupplierId &&
                  projection.CurrencyId.HasValue && projection.LandedUnitCost > 0 &&
                  projection.ValidUntil > now && projection.AvailableQuantity >= projection.Quantity &&
                  projection.LeadTimeDays >= 0 &&
                  supplierQuote.RfqId == quote.RfqId &&
                  supplierQuote.SupplierId == projection.SupplierId &&
                  supplierQuote.InboxStatus == SupplierQuoteInboxStatuses.ReadyForComparison &&
                  supplierQuote.CurrentRevisionNumber == revision.RevisionNumber &&
                  revision.RevisionNumber == projection.QuoteRevision &&
                  line.SupplierQuoteRevisionId == revision.Id &&
                  line.RfqItemId == projection.RfqItemId && !line.IsAlternate &&
                  candidateSupplier.IsActive == true &&
                  candidateSupplier.VerificationStatus == SupplierVerificationStatuses.Verified &&
                  (candidateSupplier.GovernanceStatus == SupplierGovernanceStatuses.Approved ||
                   candidateSupplier.GovernanceStatus == SupplierGovernanceStatuses.Preferred ||
                   candidateSupplier.GovernanceStatus == SupplierGovernanceStatuses.Provisional) &&
                  candidateSupplier.ReadinessStatus == SupplierReadinessStatuses.Ready &&
                  candidateSupplier.ComplianceStatus != SupplierComplianceStatuses.Blocked &&
                  candidateSupplier.ComplianceStatus != SupplierComplianceStatuses.Failed &&
                  candidateSupplier.ComplianceStatus != SupplierComplianceStatuses.Restricted &&
                  candidateSupplier.RiskStatus != SupplierRiskStatuses.Blocked &&
                  candidateSupplier.RiskStatus != SupplierRiskStatuses.High
            select new NegotiationProjection(projection.Id, projection.SupplierId,
                projection.RfqItemId!.Value, projection.CurrencyId, projection.UnitPrice,
                projection.LandedUnitCost, projection.Quantity, projection.AvailableQuantity,
                projection.LeadTimeDays, projection.SourceSupplierQuoteId,
                projection.SourceSupplierQuoteRevisionId, revision.CapturedOn,
                candidateSupplier.IsActive == true))
            .ToArrayAsync(cancellationToken);
        var competitors = SelectCurrentCompetitors(competitorRows);
        var selectedSnapshots = await (
            from award in context.Set<SourcingAward>().AsNoTracking()
            join projection in context.SupplierQuotedItems.AsNoTracking()
                on award.SupplierQuotedItemId equals projection.Id
            where award.BusinessUnitId == businessUnitId && projection.BusinessUnitId == businessUnitId &&
                  projection.SourceSupplierQuoteId == quote.Id && award.Status != "CANCELLED" &&
                  award.Status != "REJECTED"
            select new SelectedOfferSnapshot(projection.RfqItemId, award.LandedUnitCost ??
                projection.LandedUnitCost ?? award.UnitPrice, award.CurrencyId ?? projection.CurrencyId,
                award.CreatedOn)).ToArrayAsync(cancellationToken);
        var decisionQuery = context.SupplierNegotiationDecisions.AsNoTracking().Where(x =>
            x.BusinessUnitId == businessUnitId && x.SupplierQuoteId == quote.Id);
        var decisionTotal = await decisionQuery.CountAsync(cancellationToken);
        var decisions = await decisionQuery
            .OrderByDescending(x => x.DecidedOn).ThenByDescending(x => x.Id)
            .Take(MaxPriorDecisions)
            .Select(x => new SupplierNegotiationDecisionView(x.Id, x.SupplierQuoteRevisionId,
                x.RecommendationCode, x.Disposition, x.Reason, x.PolicyVersion,
                x.ExpectedQuoteVersion, x.Actor, x.DecidedOn, x.CorrelationId, false, null))
            .ToArrayAsync(cancellationToken);
        var effectiveCurrencyId = EffectiveCorrection(currentRevision, null, "CURRENCYID") is { } correctedCurrency &&
            long.TryParse(correctedCurrency, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCurrency)
                ? parsedCurrency
                : currentRevision.CurrencyId;
        var currencyCode = await context.Currencies.AsNoTracking().Where(x =>
                x.BusinessUnitId == businessUnitId && x.Id == effectiveCurrencyId)
            .Select(x => x.Code).SingleOrDefaultAsync(cancellationToken)
            ?? $"Currency {effectiveCurrencyId}";
        return new NegotiationInput(quote, currentRevision, supplier, competitors, selectedSnapshots,
            decisions, currencyCode, now, decisionTotal,
            await context.ResolveSupplierInputTaxRecoverableAsync(businessUnitId, cancellationToken));
    }

    internal static SupplierNegotiationWorkspace BuildWorkspace(NegotiationInput input)
    {
        var effective = WithReviewedCorrections(input);
        var flags = EvaluateFlags(effective);
        var recommendations = EvaluateRecommendations(effective, flags);
        return new SupplierNegotiationWorkspace(effective.Quote.Id, effective.CurrentRevision.Id,
            effective.CurrentRevision.RevisionNumber, effective.Quote.Version, "SHADOW", PolicyVersion,
            new SupplierNegotiationRound(effective.CurrentRevision.RevisionNumber, effective.CurrencyCode,
                effective.CurrentRevision.ValidUntil, effective.CurrentRevision.Incoterms,
                effective.CurrentRevision.PaymentTerms, effective.CurrentRevision.FreightAmount,
                effective.CurrentRevision.TaxAmount, effective.CurrentRevision.CapturedOn),
            SupplierBidQualityFlagCodes.All, flags, recommendations, effective.PriorDecisions,
            effective.PriorDecisionTotal,
            effective.PriorDecisionTotal > effective.PriorDecisions.Count);
    }

    internal static NegotiationProjection[] SelectCurrentCompetitors(
        IEnumerable<NegotiationProjection> rows) => rows.Where(x => x.SupplierActive)
        .GroupBy(x => new { x.SupplierId, x.RfqItemId })
        .Select(x => x.OrderByDescending(y => y.CapturedOn)
            .ThenByDescending(y => y.SourceSupplierQuoteRevisionId)
            .ThenByDescending(y => y.Id).First())
        .ToArray();

    private static NegotiationInput WithReviewedCorrections(NegotiationInput input)
    {
        var quote = new SupplierQuote
        {
            Id = input.Quote.Id,
            BusinessUnitId = input.Quote.BusinessUnitId,
            SupplierId = input.Quote.SupplierId,
            SupplierSolicitationId = input.Quote.SupplierSolicitationId,
            SourcingCaseId = input.Quote.SourcingCaseId,
            RfqId = input.Quote.RfqId,
            NexoraSerial = input.Quote.NexoraSerial,
            SupplierQuoteReference = input.Quote.SupplierQuoteReference,
            CurrentRevisionNumber = input.Quote.CurrentRevisionNumber,
            InboxStatus = input.Quote.InboxStatus,
            Version = input.Quote.Version,
            CreatedOn = input.Quote.CreatedOn,
            CreatedBy = input.Quote.CreatedBy,
            UpdatedOn = input.Quote.UpdatedOn,
            UpdatedBy = input.Quote.UpdatedBy
        };
        foreach (var source in input.Quote.Revisions)
        {
            var revision = CloneRevision(source, quote);
            ApplyReviewedCorrections(revision);
            quote.Revisions.Add(revision);
        }
        var current = quote.Revisions.Single(x => x.Id == input.CurrentRevision.Id);
        return input with { Quote = quote, CurrentRevision = current };
    }

    private static SupplierQuoteRevision CloneRevision(SupplierQuoteRevision source, SupplierQuote quote)
    {
        var revision = new SupplierQuoteRevision
        {
            Id = source.Id,
            BusinessUnitId = source.BusinessUnitId,
            SupplierQuoteId = source.SupplierQuoteId,
            RevisionNumber = source.RevisionNumber,
            CaptureChannel = source.CaptureChannel,
            SourceDocumentId = source.SourceDocumentId,
            SourceIdentity = source.SourceIdentity,
            SourceSha256 = source.SourceSha256,
            CurrencyId = source.CurrencyId,
            ValidUntil = source.ValidUntil,
            Incoterms = source.Incoterms,
            FreightAmount = source.FreightAmount,
            TaxAmount = source.TaxAmount,
            PaymentTerms = source.PaymentTerms,
            Notes = source.Notes,
            RequiresReview = source.RequiresReview,
            IdempotencyKey = source.IdempotencyKey,
            RequestHash = source.RequestHash,
            CapturedOn = source.CapturedOn,
            CapturedBy = source.CapturedBy,
            CorrelationId = source.CorrelationId,
            SupplierQuote = quote,
            Evidence = source.Evidence.ToList(),
            ReviewDecisions = source.ReviewDecisions.ToList()
        };
        foreach (var sourceLine in source.Lines)
            revision.Lines.Add(new SupplierQuoteLine
            {
                Id = sourceLine.Id,
                BusinessUnitId = sourceLine.BusinessUnitId,
                SupplierQuoteRevisionId = sourceLine.SupplierQuoteRevisionId,
                LineNumber = sourceLine.LineNumber,
                RfqItemId = sourceLine.RfqItemId,
                CommercialDemandLineId = sourceLine.CommercialDemandLineId,
                PartNumber = sourceLine.PartNumber,
                Manufacturer = sourceLine.Manufacturer,
                SupplierPartNumber = sourceLine.SupplierPartNumber,
                Description = sourceLine.Description,
                Quantity = sourceLine.Quantity,
                AvailableQuantity = sourceLine.AvailableQuantity,
                UnitOfMeasure = sourceLine.UnitOfMeasure,
                UnitPrice = sourceLine.UnitPrice,
                MinimumOrderQuantity = sourceLine.MinimumOrderQuantity,
                LeadTimeDays = sourceLine.LeadTimeDays,
                AvailabilityType = sourceLine.AvailabilityType,
                OriginCountry = sourceLine.OriginCountry,
                Warranty = sourceLine.Warranty,
                IsAlternate = sourceLine.IsAlternate,
                Exceptions = sourceLine.Exceptions,
                Evidence = sourceLine.Evidence.ToList()
            });
        return revision;
    }

    private static void ApplyReviewedCorrections(SupplierQuoteRevision revision)
    {
        foreach (var evidence in revision.Evidence)
        {
            var value = EffectiveCorrection(revision, evidence.SupplierQuoteLineId, evidence.FieldName);
            if (value is null) continue;
            if (evidence.SupplierQuoteLineId is long lineId)
            {
                var line = revision.Lines.SingleOrDefault(x => x.Id == lineId);
                if (line is not null) ApplyLineCorrection(line, evidence.FieldName, value);
            }
            else
            {
                ApplyHeaderCorrection(revision, evidence.FieldName, value);
            }
        }
    }

    private static string? EffectiveCorrection(SupplierQuoteRevision revision, long? lineId, string fieldName)
    {
        var normalizedField = NormalizeField(fieldName);
        var evidence = revision.Evidence.Where(x => x.SupplierQuoteLineId == lineId &&
                NormalizeField(x.FieldName) == normalizedField)
            .OrderByDescending(x => x.CreatedOn).ThenByDescending(x => x.Id).FirstOrDefault();
        if (evidence is null) return null;
        var latest = revision.ReviewDecisions.Where(x => x.SupplierQuoteFieldEvidenceId == evidence.Id)
            .OrderByDescending(x => x.ReviewedOn).ThenByDescending(x => x.Id).FirstOrDefault();
        return latest?.Status == SupplierQuoteReviewStatuses.Corrected
            ? latest.CorrectedValue?.Trim()
            : null;
    }

    private static void ApplyHeaderCorrection(SupplierQuoteRevision revision, string fieldName, string value)
    {
        switch (NormalizeField(fieldName))
        {
            case "CURRENCYID" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var currencyId):
                revision.CurrencyId = currencyId;
                break;
            case "VALIDUNTIL" when DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var validUntil):
                revision.ValidUntil = validUntil;
                break;
            case "INCOTERMS": revision.Incoterms = value; break;
            case "FREIGHTAMOUNT" when TryDecimal(value, out var freight): revision.FreightAmount = freight; break;
            case "TAXAMOUNT" when TryDecimal(value, out var tax): revision.TaxAmount = tax; break;
            case "PAYMENTTERMS": revision.PaymentTerms = value; break;
        }
    }

    private static void ApplyLineCorrection(SupplierQuoteLine line, string fieldName, string value)
    {
        switch (NormalizeField(fieldName))
        {
            case "PARTNUMBER": line.PartNumber = value; break;
            case "MANUFACTURER": line.Manufacturer = value; break;
            case "SUPPLIERPARTNUMBER": line.SupplierPartNumber = value; break;
            case "DESCRIPTION": line.Description = value; break;
            case "QUANTITY" when TryDecimal(value, out var quantity): line.Quantity = quantity; break;
            case "AVAILABLEQUANTITY" when TryDecimal(value, out var available): line.AvailableQuantity = available; break;
            case "UNITOFMEASURE": line.UnitOfMeasure = value; break;
            case "UNITPRICE" when TryDecimal(value, out var price): line.UnitPrice = price; break;
            case "MINIMUMORDERQUANTITY" when TryDecimal(value, out var minimum): line.MinimumOrderQuantity = minimum; break;
            case "LEADTIMEDAYS" when int.TryParse(value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var leadDays): line.LeadTimeDays = leadDays; break;
            case "AVAILABILITYTYPE": line.AvailabilityType = value; break;
            case "ORIGINCOUNTRY": line.OriginCountry = value; break;
            case "WARRANTY": line.Warranty = value; break;
            case "ISALTERNATE" when bool.TryParse(value, out var alternate): line.IsAlternate = alternate; break;
        }
    }

    private static bool TryDecimal(string value, out decimal result) => decimal.TryParse(value,
        NumberStyles.Number, CultureInfo.InvariantCulture, out result);

    private static IReadOnlyCollection<SupplierBidQualityFlag> EvaluateFlags(NegotiationInput input)
    {
        var revision = input.CurrentRevision;
        var flags = new List<SupplierBidQualityFlag>();
        void Add(string code, string severity, bool blocking, string explanation, decimal confidence,
            IEnumerable<string> evidence)
        {
            var facts = evidence.Distinct(StringComparer.Ordinal).ToArray();
            flags.Add(new SupplierBidQualityFlag(code, severity, blocking, explanation,
                confidence, facts.Length, facts.Take(MaxEvidenceFacts).ToArray()));
        }

        if (!revision.ValidUntil.HasValue)
            Add(SupplierBidQualityFlagCodes.MissingValidity, "CRITICAL", true,
                "The current round has no validity deadline.", 1m, ["ValidUntil is absent"]);

        var unconfirmed = revision.Lines.Where(x => !x.AvailableQuantity.HasValue ||
            string.IsNullOrWhiteSpace(x.AvailabilityType)).ToArray();
        if (unconfirmed.Length > 0)
            Add(SupplierBidQualityFlagCodes.UnconfirmedStock, "CRITICAL", true,
                "Stock or availability is not confirmed for every quoted line.", 1m,
                unconfirmed.Select(x => $"Line {x.LineNumber}: stock evidence incomplete"));

        var missingTerms = new List<string>();
        if (string.IsNullOrWhiteSpace(revision.Incoterms)) missingTerms.Add("Incoterms absent");
        if (string.IsNullOrWhiteSpace(revision.PaymentTerms)) missingTerms.Add("Payment terms absent");
        var invalidPrices = revision.Lines.Where(x => x.UnitPrice <= 0m).ToArray();
        missingTerms.AddRange(invalidPrices.Select(x => $"Line {x.LineNumber}: price is missing or non-positive"));
        if (missingTerms.Count > 0)
            Add(SupplierBidQualityFlagCodes.IncompleteCommercialTerms,
                invalidPrices.Length > 0 ? "CRITICAL" : "WARNING", invalidPrices.Length > 0,
                invalidPrices.Length > 0 ? "Commercial terms include an invalid price."
                    : "Commercial terms are incomplete.", 1m, missingTerms);

        var implausibleLead = revision.Lines.Where(x => x.LeadTimeDays is > 365 ||
            x.LeadTimeDays == 0 && (!x.AvailableQuantity.HasValue || x.AvailableQuantity < x.Quantity)).ToArray();
        if (implausibleLead.Length > 0)
            Add(SupplierBidQualityFlagCodes.UnrealisticLeadTime, "WARNING", false,
                "Lead time conflicts with availability or exceeds one year.", .95m,
                implausibleLead.Select(x => $"Line {x.LineNumber}: lead time {x.LeadTimeDays} days"));

        var priceEvidence = PriceOutlierEvidence(input).ToArray();
        if (priceEvidence.Length > 0)
            Add(SupplierBidQualityFlagCodes.PriceOutlier, "WARNING", false,
                "Current price is outside the 25% band around comparable current offers.", .85m,
                priceEvidence);

        var currencyCohort = input.Competitors.Where(x => x.CurrencyId.HasValue)
            .GroupBy(x => x.CurrencyId!.Value).OrderByDescending(x => x.Count()).FirstOrDefault();
        if (currencyCohort is not null && currencyCohort.Count() >= 2 &&
            currencyCohort.Key != revision.CurrencyId)
            Add(SupplierBidQualityFlagCodes.UnusualCurrency, "WARNING", false,
                "The current round currency differs from the dominant comparable-offer currency.", .8m,
                [$"Current currency id {revision.CurrencyId}",
                    $"Cohort currency id {currencyCohort.Key}; {currencyCohort.Count()} offers"]);

        var volatility = RevisionVolatilityEvidence(input).ToArray();
        if (volatility.Length >= 2)
            Add(SupplierBidQualityFlagCodes.RevisionVolatility, "WARNING", false,
                "Repeated rounds contain multiple material commercial changes.", .9m, volatility);

        var selectedIncrease = PostSelectionIncreaseEvidence(input).ToArray();
        if (selectedIncrease.Length > 0)
            Add(SupplierBidQualityFlagCodes.PostSelectionPriceIncrease, "CRITICAL", true,
                "The current round exceeds a previously selected offer snapshot.", 1m, selectedIncrease);

        var alternateWithoutApproval = revision.Lines.Where(x => x.IsAlternate &&
            !HasAlternateAuthorization(revision, x)).ToArray();
        if (alternateWithoutApproval.Length > 0)
            Add(SupplierBidQualityFlagCodes.SuspiciousAlternate, "CRITICAL", true,
                "An alternate lacks accepted authorization evidence.", 1m,
                alternateWithoutApproval.Select(x => $"Line {x.LineNumber}: alternate authorization absent"));

        var authorizationEvidence = new List<string>();
        if (input.Supplier.IsActive != true ||
            input.Supplier.VerificationStatus != SupplierVerificationStatuses.Verified ||
            input.Supplier.GovernanceStatus is not (SupplierGovernanceStatuses.Approved or
                SupplierGovernanceStatuses.Preferred or SupplierGovernanceStatuses.Provisional) ||
            input.Supplier.ReadinessStatus != SupplierReadinessStatuses.Ready ||
            input.Supplier.ComplianceStatus is SupplierComplianceStatuses.Blocked or
                SupplierComplianceStatuses.Failed or SupplierComplianceStatuses.Restricted ||
            input.Supplier.RiskStatus is SupplierRiskStatuses.Blocked or SupplierRiskStatuses.High)
            authorizationEvidence.Add(
                $"Supplier active={input.Supplier.IsActive}; governance={input.Supplier.GovernanceStatus}; " +
                $"verification={input.Supplier.VerificationStatus}; readiness={input.Supplier.ReadinessStatus}; " +
                $"compliance={input.Supplier.ComplianceStatus}; risk={input.Supplier.RiskStatus}");
        authorizationEvidence.AddRange(alternateWithoutApproval.Select(x =>
            $"Line {x.LineNumber}: alternate approval absent"));
        if (authorizationEvidence.Count > 0)
            Add(SupplierBidQualityFlagCodes.MissingAuthorization, "CRITICAL", true,
                "Supplier or alternate authorization is incomplete.", 1m, authorizationEvidence);

        var staleEvidence = StaleEvidence(input).ToArray();
        if (staleEvidence.Length > 0)
            Add(SupplierBidQualityFlagCodes.StaleEvidence, "CRITICAL", true,
                "Critical evidence is expired or still unresolved.", 1m, staleEvidence);

        var (complete, expected, completenessEvidence) = Completeness(input);
        var completeness = expected == 0 ? 0m : decimal.Round((decimal)complete / expected, 4);
        if (completeness < .8m)
            Add(SupplierBidQualityFlagCodes.PoorResponseCompleteness, "WARNING", false,
                $"Response completeness is {completeness:P0}; at least 80% is required for a complete bid.",
                1m, completenessEvidence);
        return flags;
    }

    private static IReadOnlyCollection<SupplierNegotiationRecommendation> EvaluateRecommendations(
        NegotiationInput input, IReadOnlyCollection<SupplierBidQualityFlag> flags)
    {
        var revision = input.CurrentRevision;
        var result = new List<SupplierNegotiationRecommendation>();
        var highPriceEvidence = HighPriceOutlierEvidence(input).ToArray();
        if (highPriceEvidence.Length > 0)
            result.Add(Recommendation(SupplierNegotiationRecommendationCodes.BestAndFinalPrice,
                "Request best-and-final price",
                "Comparable current offers indicate the landed cost is materially high.",
                highPriceEvidence, highPriceEvidence.Length, .85m,
                "Comparable offers are not a commitment and may differ in terms or availability."));

        var quantityBreaks = revision.Lines.Where(x => x.MinimumOrderQuantity is > 0 &&
            x.Quantity >= x.MinimumOrderQuantity * 2).Select(x =>
            $"Line {x.LineNumber}: quantity {x.Quantity}; MOQ {x.MinimumOrderQuantity}").ToArray();
        if (quantityBreaks.Length > 0)
            result.Add(Recommendation(SupplierNegotiationRecommendationCodes.QuantityBreak,
                "Request quantity-break pricing",
                "The quoted volume is at least twice the stated minimum order quantity.",
                quantityBreaks, quantityBreaks.Length, .8m,
                "No lower tier price is assumed until the Supplier confirms it."));

        var deliveryEvidence = FasterDeliveryEvidence(input).ToArray();
        if (deliveryEvidence.Length > 0)
            result.Add(Recommendation(SupplierNegotiationRecommendationCodes.FasterDelivery,
                "Request faster delivery",
                "Comparable current offers show a shorter lead-time benchmark.",
                deliveryEvidence, deliveryEvidence.Length, .8m,
                "Competitor lead times may reflect different stock or commercial terms."));

        if (revision.FreightAmount > 0)
            result.Add(Recommendation(SupplierNegotiationRecommendationCodes.FreightInclusiveOffer,
                "Request a freight-inclusive offer",
                "Freight is currently itemized outside unit prices.",
                [$"Round freight amount {revision.FreightAmount}"], 1, .9m,
                "The recommendation does not assume a freight reduction."));

        if (string.IsNullOrWhiteSpace(revision.PaymentTerms) || IsRestrictivePayment(revision.PaymentTerms))
            result.Add(Recommendation(SupplierNegotiationRecommendationCodes.ImprovedPaymentTerms,
                "Request improved payment terms",
                string.IsNullOrWhiteSpace(revision.PaymentTerms)
                    ? "Payment terms are missing from the current round."
                    : "The current round records advance or immediate payment terms.",
                [string.IsNullOrWhiteSpace(revision.PaymentTerms) ? "Payment terms absent" :
                    "Advance or immediate payment terms recorded"], 1, .9m,
                "No replacement terms are inferred; the Supplier must confirm them."));

        var partial = revision.Lines.Where(x => x.AvailableQuantity is > 0 &&
            x.AvailableQuantity < x.Quantity).Select(x =>
            $"Line {x.LineNumber}: {x.AvailableQuantity} of {x.Quantity} available").ToArray();
        if (partial.Length > 0)
            result.Add(Recommendation(SupplierNegotiationRecommendationCodes.PartialImmediateAvailability,
                "Confirm partial immediate availability",
                "The Supplier reports usable stock below the full requested quantity.",
                partial, partial.Length, .95m,
                "Dispatch timing remains unconfirmed until the Supplier responds."));

        var approvedAlternates = revision.Lines.Where(x => x.IsAlternate &&
            HasAlternateAuthorization(revision, x)).Select(x =>
            $"Line {x.LineNumber}: alternate has accepted authorization evidence").ToArray();
        if (approvedAlternates.Length > 0)
            result.Add(Recommendation(SupplierNegotiationRecommendationCodes.ApprovedAlternate,
                "Include the approved alternate",
                "An alternate is supported by accepted authorization evidence in this round.",
                approvedAlternates, approvedAlternates.Length, 1m,
                "Commercial acceptance remains a human decision; this action sends nothing."));
        return result;
    }

    private static SupplierNegotiationRecommendation Recommendation(string code, string title,
        string rationale, IEnumerable<string> evidence, int sampleSize, decimal confidence,
        string limitation) => new(code, title, rationale, sampleSize, confidence,
        evidence.Distinct(StringComparer.Ordinal).Take(MaxEvidenceFacts).ToArray(),
        [limitation, "Shadow-only guidance; it cannot send, award, or change price."]);

    private static IEnumerable<string> PriceOutlierEvidence(NegotiationInput input)
    {
        foreach (var line in input.CurrentRevision.Lines)
        {
            var cohort = input.Competitors.Where(x => x.RfqItemId == line.RfqItemId &&
                    x.CurrencyId == input.CurrentRevision.CurrencyId && CommercialPrice(x).HasValue)
                .Select(x => CommercialPrice(x)!.Value).OrderBy(x => x).ToArray();
            if (cohort.Length < 2) continue;
            var median = Median(cohort);
            var current = CurrentLandedUnitCost(input.CurrentRevision, line, input.SupplierInputTaxRecoverable);
            if (median > 0 && (current > median * 1.25m || current < median * .75m))
                yield return $"Line {line.LineNumber}: current {current:F4}; cohort median {median:F4}; n={cohort.Length}";
        }
    }

    private static IEnumerable<string> HighPriceOutlierEvidence(NegotiationInput input)
    {
        foreach (var line in input.CurrentRevision.Lines)
        {
            var cohort = input.Competitors.Where(x => x.RfqItemId == line.RfqItemId &&
                    x.CurrencyId == input.CurrentRevision.CurrencyId && CommercialPrice(x).HasValue)
                .Select(x => CommercialPrice(x)!.Value).OrderBy(x => x).ToArray();
            if (cohort.Length < 2) continue;
            var median = Median(cohort);
            var current = CurrentLandedUnitCost(input.CurrentRevision, line, input.SupplierInputTaxRecoverable);
            if (median > 0 && current > median * 1.25m)
                yield return $"Line {line.LineNumber}: current {current:F4}; cohort median {median:F4}; n={cohort.Length}";
        }
    }

    private static IEnumerable<string> FasterDeliveryEvidence(NegotiationInput input)
    {
        foreach (var line in input.CurrentRevision.Lines.Where(x => x.LeadTimeDays.HasValue))
        {
            var cohort = input.Competitors.Where(x => x.RfqItemId == line.RfqItemId &&
                    x.LeadTimeDays.HasValue).Select(x => x.LeadTimeDays!.Value).OrderBy(x => x).ToArray();
            if (cohort.Length < 2) continue;
            var median = Median(cohort.Select(x => (decimal)x).ToArray());
            if (line.LeadTimeDays > median + 2)
                yield return $"Line {line.LineNumber}: current {line.LeadTimeDays} days; cohort median {median:F0}; n={cohort.Length}";
        }
    }

    private static IEnumerable<string> RevisionVolatilityEvidence(NegotiationInput input)
    {
        var current = input.CurrentRevision;
        if (current.RevisionNumber < 3) yield break;
        var previous = input.Quote.Revisions.SingleOrDefault(x =>
            x.RevisionNumber == current.RevisionNumber - 1);
        if (previous is null) yield break;
        if (previous.CurrencyId != current.CurrencyId) yield return "Currency changed from the prior round";
        if (!string.Equals(previous.Incoterms, current.Incoterms, StringComparison.OrdinalIgnoreCase))
            yield return "Incoterms changed from the prior round";
        if (!string.Equals(previous.PaymentTerms, current.PaymentTerms, StringComparison.OrdinalIgnoreCase))
            yield return "Payment terms changed from the prior round";
        foreach (var line in current.Lines)
        {
            var prior = previous.Lines.SingleOrDefault(x => x.LineNumber == line.LineNumber);
            if (prior is null)
            {
                yield return $"Line {line.LineNumber} was added in the current round";
                continue;
            }
            if (MaterialPercentChange(prior.UnitPrice, line.UnitPrice, .10m))
                yield return $"Line {line.LineNumber} price changed by at least 10%";
            if (prior.Quantity != line.Quantity)
                yield return $"Line {line.LineNumber} quantity changed";
            if (Math.Abs((prior.LeadTimeDays ?? 0) - (line.LeadTimeDays ?? 0)) >= 3)
                yield return $"Line {line.LineNumber} lead time changed by at least 3 days";
        }
    }

    private static IEnumerable<string> PostSelectionIncreaseEvidence(NegotiationInput input)
    {
        foreach (var line in input.CurrentRevision.Lines)
        foreach (var selected in input.SelectedOffers.Where(x => x.RfqItemId == line.RfqItemId &&
                     x.CurrencyId == input.CurrentRevision.CurrencyId && x.LandedUnitCost > 0))
        {
            var current = CurrentLandedUnitCost(input.CurrentRevision, line, input.SupplierInputTaxRecoverable);
            if (current > selected.LandedUnitCost * 1.02m)
                yield return $"Line {line.LineNumber}: current landed {current:F4}; selected snapshot {selected.LandedUnitCost:F4}";
        }
    }

    private static IEnumerable<string> StaleEvidence(NegotiationInput input)
    {
        if (input.CurrentRevision.ValidUntil.HasValue && input.CurrentRevision.ValidUntil <= input.NowUtc)
            yield return $"Validity expired {input.CurrentRevision.ValidUntil:O}";
        var latest = input.CurrentRevision.ReviewDecisions.GroupBy(x => x.SupplierQuoteFieldEvidenceId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.ReviewedOn)
                .ThenByDescending(y => y.Id).First());
        foreach (var evidence in input.CurrentRevision.Evidence.Where(x => x.Critical && x.ReviewRequired))
            if (!latest.TryGetValue(evidence.Id, out var decision) ||
                decision.Status is not (SupplierQuoteReviewStatuses.Accepted or SupplierQuoteReviewStatuses.Corrected))
                yield return $"Critical field {evidence.FieldName} remains unresolved";
    }

    private static (int Complete, int Expected, IReadOnlyCollection<string> Missing) Completeness(
        NegotiationInput input)
    {
        var complete = 0;
        var expected = 3;
        var missing = new List<string>();
        Check(input.CurrentRevision.ValidUntil.HasValue, "Validity absent");
        Check(!string.IsNullOrWhiteSpace(input.CurrentRevision.Incoterms), "Incoterms absent");
        Check(!string.IsNullOrWhiteSpace(input.CurrentRevision.PaymentTerms), "Payment terms absent");
        foreach (var line in input.CurrentRevision.Lines)
        {
            expected += 6;
            Check(!string.IsNullOrWhiteSpace(line.PartNumber) || !string.IsNullOrWhiteSpace(line.SupplierPartNumber),
                $"Line {line.LineNumber}: part identity absent");
            Check(line.Quantity > 0, $"Line {line.LineNumber}: quantity absent");
            Check(line.UnitPrice > 0, $"Line {line.LineNumber}: price absent");
            Check(line.AvailableQuantity.HasValue, $"Line {line.LineNumber}: stock quantity absent");
            Check(!string.IsNullOrWhiteSpace(line.AvailabilityType),
                $"Line {line.LineNumber}: availability type absent");
            Check(line.LeadTimeDays.HasValue, $"Line {line.LineNumber}: lead time absent");
        }
        return (complete, expected, missing);

        void Check(bool condition, string message)
        {
            if (condition) complete++;
            else missing.Add(message);
        }
    }

    private static bool HasAlternateAuthorization(SupplierQuoteRevision revision, SupplierQuoteLine line)
    {
        var latest = revision.ReviewDecisions.GroupBy(x => x.SupplierQuoteFieldEvidenceId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.ReviewedOn)
                .ThenByDescending(y => y.Id).First());
        return revision.Evidence.Where(x => x.SupplierQuoteLineId == line.Id &&
                NormalizeField(x.FieldName) is "ALTERNATEAUTHORIZATION" or "ALTERNATEAPPROVAL" or
                    "APPROVEDALTERNATE")
            .Any(x => latest.TryGetValue(x.Id, out var decision) &&
                IsAffirmative(decision.Status switch
                {
                    SupplierQuoteReviewStatuses.Accepted => x.NormalizedValue,
                    SupplierQuoteReviewStatuses.Corrected => decision.CorrectedValue,
                    _ => null
                }));
    }

    private static bool IsRestrictivePayment(string paymentTerms)
    {
        var normalized = paymentTerms.ToUpperInvariant();
        return normalized.Contains("PREPAID", StringComparison.Ordinal) ||
               normalized.Contains("ADVANCE", StringComparison.Ordinal) ||
               normalized.Contains("COD", StringComparison.Ordinal) ||
               normalized.Contains("DUE ON RECEIPT", StringComparison.Ordinal);
    }

    private static bool IsAffirmative(string? value) => value?.Trim().ToUpperInvariant() is
        "TRUE" or "YES" or "APPROVED" or "AUTHORIZED" or "1";
    private static string NormalizeField(string value) => new(value.Where(char.IsLetterOrDigit)
        .Select(char.ToUpperInvariant).ToArray());
    private static decimal? CommercialPrice(NegotiationProjection projection) =>
        projection.LandedUnitCost is > 0 ? projection.LandedUnitCost : null;
    /// <summary>
    /// The landed unit cost of a line in the CURRENT round, computed with the same definition
    /// the canonical projection persists (<see cref="LandedCostFormula"/>).
    ///
    /// This matters because the figure is compared against persisted
    /// <c>SupplierQuotedItem.LandedUnitCost</c> / <c>SourcingAward.LandedUnitCost</c> values in
    /// <see cref="PostSelectionIncreaseEvidence"/>, and a &gt;2% gap there raises a CRITICAL,
    /// BLOCKING flag. When the two sides used different allocation bases the flag fired on
    /// arithmetic rather than on anything the supplier did.
    /// </summary>
    private static decimal CurrentLandedUnitCost(SupplierQuoteRevision revision, SupplierQuoteLine line,
        bool supplierInputTaxRecoverable)
    {
        var totalLineValue = LandedCostFormula.TotalLineValue(
            revision.Lines.Select(x => (x.UnitPrice, x.Quantity)));
        var lineValue = LandedCostFormula.LineValue(line.UnitPrice, line.Quantity);
        var freight = LandedCostFormula.AllocateByValue(revision.FreightAmount, lineValue, totalLineValue);
        var tax = LandedCostFormula.AllocateByValue(revision.TaxAmount, lineValue, totalLineValue);
        return LandedCostFormula.UnitCost(line.UnitPrice, line.Quantity, freight, tax,
            supplierInputTaxRecoverable);
    }
    private static decimal Median(IReadOnlyList<decimal> values) => values.Count % 2 == 1
        ? values[values.Count / 2]
        : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2m;
    private static bool MaterialPercentChange(decimal before, decimal after, decimal threshold) =>
        before == 0 ? after != 0 : Math.Abs(after - before) / Math.Abs(before) >= threshold;

    private static SupplierNegotiationDecisionView Replay(SupplierNegotiationDecision decision,
        string requestHash)
    {
        if (!FixedEquals(decision.RequestHash, requestHash))
            throw new SupplierQuoteConflictException(
                "The idempotency key was already used for a different negotiation decision.");
        return ToView(decision, true, decision.ExpectedQuoteVersion + 1);
    }

    private static SupplierNegotiationDecisionView ToView(SupplierNegotiationDecision decision,
        bool replayed, long resultingQuoteVersion) => new(decision.Id, decision.SupplierQuoteRevisionId,
        decision.RecommendationCode, decision.Disposition, decision.Reason, decision.PolicyVersion,
        decision.ExpectedQuoteVersion, decision.Actor, decision.DecidedOn, decision.CorrelationId,
        replayed, resultingQuoteVersion);

    private void EnsureTenant(long businessUnitId)
    {
        if (businessUnitId <= 0 || context.ScopedTenantId != businessUnitId)
            throw new UnauthorizedAccessException(
                "The Supplier Quote tenant does not match the authenticated tenant.");
    }

    private static string Required(string? value, int maxLength, string name)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maxLength ||
            normalized.Any(char.IsControl))
            throw new SupplierQuoteValidationException(
                $"{name} is required and must not exceed {maxLength} printable characters.");
        return normalized;
    }

    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, SnapshotJson))));
    private static bool FixedEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left),
                Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsConcurrentWriteFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbUpdateConcurrencyException)
                return true;
            if (current is PostgresException postgres && postgres.SqlState is
                PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected)
                return true;
        }
        return false;
    }
}

internal sealed record NegotiationInput(
    SupplierQuote Quote,
    SupplierQuoteRevision CurrentRevision,
    Supplier Supplier,
    IReadOnlyCollection<NegotiationProjection> Competitors,
    IReadOnlyCollection<SelectedOfferSnapshot> SelectedOffers,
    IReadOnlyCollection<SupplierNegotiationDecisionView> PriorDecisions,
    string CurrencyCode,
    DateTime NowUtc,
    int PriorDecisionTotal = 0,
    // The tenant's CommercialMatchingPolicy.SupplierInputTaxRecoverable, carried in because the
    // evidence builders are static and pure but must reach the same landed-cost number the
    // projection persisted — otherwise the >2% gap that raises a blocking CRITICAL
    // POST_SELECTION_PRICE_INCREASE reopens, this time on the tax term.
    bool SupplierInputTaxRecoverable = true);

internal sealed record NegotiationProjection(
    long Id,
    long SupplierId,
    long RfqItemId,
    long? CurrencyId,
    decimal? UnitPrice,
    decimal? LandedUnitCost,
    decimal Quantity,
    decimal? AvailableQuantity,
    int? LeadTimeDays,
    long? SourceSupplierQuoteId,
    long? SourceSupplierQuoteRevisionId,
    DateTime CapturedOn,
    bool SupplierActive);

internal sealed record SelectedOfferSnapshot(
    long? RfqItemId,
    decimal LandedUnitCost,
    long? CurrencyId,
    DateTime SelectedOn);
