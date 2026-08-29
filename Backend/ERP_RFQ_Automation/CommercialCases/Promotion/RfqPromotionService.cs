using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.CommercialCases.Participation;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services.Uom;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialCases.Promotion;

/// <summary>
/// The only lead-origin RFQ creation service. It consumes a committed, current-revision
/// participation decision and copies only its Bid lines.
/// </summary>
public sealed class RfqPromotionService : IRfqPromotionService
{
    private readonly ErpRfqAutomationContext _db;
    private readonly IEvidenceObjectStorage _evidenceStorage;

    public RfqPromotionService(ErpRfqAutomationContext db, IEvidenceObjectStorage evidenceStorage)
    {
        _db = db;
        _evidenceStorage = evidenceStorage;
    }

    public async Task<RfqPromotionResult> PromoteAsync(
        long businessUnitId, long leadId, PromoteLeadToRfqCommand command, CancellationToken ct = default)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            throw new ArgumentException("An idempotency key is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.Actor))
            throw new ArgumentException("An authenticated actor is required.", nameof(command));

        var requestHash = Hash(new
        {
            businessUnitId,
            leadId,
            command.ExpectedLeadRevisionId,
            command.ExpectedDecisionVersion,
            command.ExpectedParticipationVersion,
            command.ParticipationDecisionId
        });
        var replay = await ReplayAsync(businessUnitId, command.IdempotencyKey, requestHash, ct);
        if (replay is not null) return replay;
        replay = await ReplayLeadWinnerAsync(businessUnitId, leadId, command, ct);
        if (replay is not null) return replay;

        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                replay = await ReplayAsync(businessUnitId, command.IdempotencyKey, requestHash, ct);
                if (replay is not null) return replay;
                replay = await ReplayLeadWinnerAsync(businessUnitId, leadId, command, ct);
                if (replay is not null) return replay;

                var lead = await _db.Leads
                    .Include(x => x.LeadStatus)
                    .Include(x => x.LeadItems)
                    .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == leadId, ct)
                    ?? throw new KeyNotFoundException($"Lead {leadId} was not found in this business unit.");
                if (!lead.CurrentRevisionId.HasValue)
                    throw new InvalidOperationException("The lead has no immutable current revision.");
                if (lead.CurrentRevisionId.Value != command.ExpectedLeadRevisionId)
                    throw new InvalidOperationException("The lead changed after participation was decided. Refresh and decide the current revision.");
                if (lead.CurrentRevisionNumber != command.ExpectedDecisionVersion)
                    throw new InvalidOperationException("The Lead decision version changed after participation was decided. Refresh the workbench.");
                LeadInquiryPromotionGate.EnsureProductRfqEligible(lead);
                LeadConversionGate.EnsureEligible(lead);

                var immutableRevision = await _db.Set<LeadRevision>().AsNoTracking()
                    .SingleAsync(x => x.BusinessUnitId == businessUnitId
                        && x.Id == command.ExpectedLeadRevisionId && x.LeadId == leadId, ct);
                var hasCompleteCommercialSnapshot = LeadRevisionCommercialSnapshot.TryParse(
                    immutableRevision.SnapshotJson, out var frozenHeader);
                if (!hasCompleteCommercialSnapshot)
                    throw new InvalidOperationException(
                        "The immutable Lead revision has no complete v2 commercial snapshot. Append a governed revision before promotion.");
                EnsureFrozenCommercialIdentityMatches(lead, immutableRevision, frozenHeader!);

                var decision = await _db.Set<LeadParticipationDecision>()
                    .Include(x => x.Lines)
                    .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId
                        && (!command.ParticipationDecisionId.HasValue || x.Id == command.ParticipationDecisionId.Value)
                        && x.Sequence == command.ExpectedParticipationVersion
                        && x.LeadId == leadId
                        && x.LeadRevisionId == command.ExpectedLeadRevisionId, ct)
                    ?? throw new InvalidOperationException("The participation decision does not belong to the current lead revision.");
                var latestDecisionId = await _db.Set<LeadParticipationDecision>().AsNoTracking()
                    .Where(x => x.BusinessUnitId == businessUnitId && x.LeadRevisionId == command.ExpectedLeadRevisionId)
                    .OrderByDescending(x => x.Sequence).Select(x => x.Id).FirstAsync(ct);
                if (latestDecisionId != decision.Id)
                    throw new InvalidOperationException("A newer participation decision exists for this revision. Promote the current decision.");
                if (!decision.IsCommitted)
                    throw new InvalidOperationException("Participation is still a draft. Commit the current-revision decision before promotion.");
                // Do not trust an append-only historical header merely because it says FullBid
                // or PartialBid. Legacy/imported data may predate the database aggregate trigger,
                // and promotion is the last boundary before formal commercial records exist.
                LeadParticipationOutcomeConsistency.EnsureCommittedSnapshot(
                    decision.Outcome, decision.Lines.Select(x => x.Choice));
                var fit = await _db.Set<LeadFitAssessment>().AsNoTracking().SingleOrDefaultAsync(x =>
                    x.BusinessUnitId == businessUnitId && x.Id == decision.FitAssessmentId
                    && x.LeadId == leadId && x.LeadRevisionId == command.ExpectedLeadRevisionId, ct)
                    ?? throw new InvalidOperationException("The committed participation decision has no fit assessment for the current revision.");
                var latestFitId = await _db.Set<LeadFitAssessment>().AsNoTracking()
                    .Where(x => x.BusinessUnitId == businessUnitId && x.LeadId == leadId
                        && x.LeadRevisionId == command.ExpectedLeadRevisionId)
                    .OrderByDescending(x => x.Sequence).Select(x => x.Id).FirstAsync(ct);
                if (fit.Id != latestFitId)
                    throw new InvalidOperationException("The fit assessment changed after participation was committed. Recommit participation against the latest assessment.");
                if (!fit.IsActionable)
                    throw new InvalidOperationException("The current human fit assessment is not actionable for RFQ promotion.");
                if (decision.Outcome == LeadParticipationOutcome.NoBid || decision.Lines.All(x => x.Choice != LeadLineParticipationChoice.Bid))
                    throw new InvalidOperationException("A no-bid decision closes participation without creating an RFQ.");
                var approvedDecisionLines = decision.Lines.Where(x => x.Choice == LeadLineParticipationChoice.Bid).ToArray();
                var selectedProductIds = approvedDecisionLines.Where(x => x.ProductId.HasValue)
                    .Select(x => x.ProductId!.Value).Distinct().ToArray();
                if (selectedProductIds.Length > 0 && await _db.Products.AsNoTracking()
                    .CountAsync(x => selectedProductIds.Contains(x.Id) && x.IsActive != false, ct) != selectedProductIds.Length)
                    throw new InvalidOperationException("A selected catalog product is no longer active or visible to this business unit.");
                var selectedUomIds = approvedDecisionLines.Where(x => x.UomId.HasValue)
                    .Select(x => x.UomId!.Value).Distinct().ToArray();
                if (approvedDecisionLines.Any(x => !x.UomId.HasValue)
                    || await _db.SetUoms.AsNoTracking().CountAsync(x => x.BusinessUnitId == businessUnitId
                        && selectedUomIds.Contains(x.UomId) && x.IsActive, ct) != selectedUomIds.Length)
                    throw new InvalidOperationException("Every approved line must retain an active tenant unit-of-measure identity.");
                var selectedCurrencyIds = approvedDecisionLines.Where(x => x.CurrencyId.HasValue)
                    .Select(x => x.CurrencyId!.Value).Distinct().ToArray();
                if (approvedDecisionLines.Any(x => !x.CurrencyId.HasValue)
                    || await _db.Currencies.AsNoTracking().CountAsync(x => x.BusinessUnitId == businessUnitId
                        && selectedCurrencyIds.Contains(x.Id) && x.IsActive != false, ct) != selectedCurrencyIds.Length)
                    throw new InvalidOperationException("Every approved line must retain an active tenant currency identity.");

                var existing = await _db.Rfqs.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.LeadId == leadId, ct);
                if (existing is not null)
                {
                    if (existing.ParticipationDecisionId == decision.Id && existing.PromotionId.HasValue)
                    {
                        var receipt = await _db.Set<RfqPromotion>().AsNoTracking()
                            .SingleAsync(x => x.BusinessUnitId == businessUnitId && x.Id == existing.PromotionId.Value, ct);
                        await tx.CommitAsync(ct);
                        return Result(receipt, existing, command.ExpectedDecisionVersion, decision.Sequence, replayed: true);
                    }
                    throw new InvalidOperationException($"Lead {leadId} already has RFQ #{existing.Id} ({existing.Rfqno}).");
                }

                var revisionLines = await _db.Set<LeadItemRevision>().AsNoTracking()
                    .Where(x => x.BusinessUnitId == businessUnitId && x.LeadRevisionId == command.ExpectedLeadRevisionId)
                    .OrderBy(x => x.LineNumber).ToListAsync(ct);
                if (hasCompleteCommercialSnapshot && frozenHeader!.Items.Count != revisionLines.Count)
                    throw new InvalidOperationException(
                        "The immutable Lead revision commercial snapshot does not cover every frozen line. Reconcile a new revision before promotion.");
                if (decision.Lines.Count != revisionLines.Count
                    || decision.Lines.Select(x => x.LeadItemRevisionId).Except(revisionLines.Select(x => x.Id)).Any())
                    throw new InvalidOperationException("The participation decision does not cover every line of the current revision.");

                var approvedRevisionIds = decision.Lines
                    .Where(x => x.Choice == LeadLineParticipationChoice.Bid)
                    .Select(x => x.LeadItemRevisionId)
                    .ToHashSet();
                var approvedRevisionLines = revisionLines.Where(x => approvedRevisionIds.Contains(x.Id)).ToArray();
                if (approvedRevisionLines.Any(x => !x.LeadItemId.HasValue))
                    throw new InvalidOperationException(
                        "An approved revision line has no exact immutable canonical Lead-item lineage. Reconcile the source before promotion.");
                var approvedLeadItemIds = approvedRevisionLines.Select(x => x.LeadItemId!.Value).Distinct().ToArray();
                var approvedEvidenceSourceByLeadItemId = await _db.LeadItems.AsNoTracking()
                    .Where(x => approvedLeadItemIds.Contains(x.Id) && x.LeadId == leadId)
                    .Select(x => new { x.Id, x.EvidenceSourceLeadItemId })
                    .ToDictionaryAsync(x => x.Id, x => x.EvidenceSourceLeadItemId ?? x.Id, ct);
                if (approvedEvidenceSourceByLeadItemId.Count != approvedLeadItemIds.Length)
                    throw new InvalidOperationException(
                        "An approved revision line does not resolve to its retained canonical Lead item.");
                var approvedDecisionByRevisionId = approvedDecisionLines
                    .ToDictionary(x => x.LeadItemRevisionId);
                var provenanceRequirements = new List<BidSourceProvenanceValidator.Requirement>(
                    approvedRevisionLines.Length);
                foreach (var revisionLine in approvedRevisionLines)
                {
                    if (!LeadRevisionLineCommercialSnapshot.TryParse(
                            revisionLine.SnapshotJson, out var frozenLine))
                        throw new InvalidOperationException(
                            $"Immutable revision line {revisionLine.LineNumber} has an incomplete commercial snapshot. Append a governed Lead revision before promotion.");
                    var approved = approvedDecisionByRevisionId[revisionLine.Id];
                    var projectionId = revisionLine.LeadItemId!.Value;
                    provenanceRequirements.Add(new BidSourceProvenanceValidator.Requirement(
                        revisionLine.Id,
                        projectionId,
                        approvedEvidenceSourceByLeadItemId[projectionId],
                        new[]
                        {
                            new CriticalSourceEvidence.Identity("ItemMaterialCode", frozenLine!.ItemMaterialCode),
                            new CriticalSourceEvidence.Identity("ManufacturerPartNumber", frozenLine.ManufacturerPartNumber),
                            new CriticalSourceEvidence.Identity("ProductShortName", frozenLine.ProductShortName),
                            new CriticalSourceEvidence.Identity("ProductShortDescription", frozenLine.ProductShortDescription),
                            new CriticalSourceEvidence.Identity("ItemText", frozenLine.ItemText)
                        },
                        approved.Quantity ?? frozenLine.Quantity,
                        approved.UnitOfMeasure ?? frozenLine.UnitOfMeasure));
                }
                var approvedEvidenceObjects = await BidSourceProvenanceValidator.ValidateAsync(
                    _db, businessUnitId, lead, command.ExpectedLeadRevisionId,
                    provenanceRequirements, ct);
                foreach (var evidenceObject in approvedEvidenceObjects)
                {
                    await using var verified = await _evidenceStorage.OpenVerifiedReadAsync(
                        evidenceObject.StoragePath, evidenceObject.ContentHash, ct);
                }

                var receiptEntity = new RfqPromotion
                {
                    BusinessUnitId = businessUnitId,
                    LeadId = leadId,
                    LeadRevisionId = command.ExpectedLeadRevisionId,
                    ParticipationDecisionId = decision.Id,
                    IdempotencyKey = command.IdempotencyKey.Trim(),
                    RequestHash = requestHash,
                    PromotedBy = command.Actor.Trim(),
                    PromotedAtUtc = DateTimeOffset.UtcNow
                };
                _db.Add(receiptEntity);
                await _db.SaveChangesAsync(ct);

                var now = DateTime.UtcNow;
                var rfq = new Rfq
                {
                    Rfqno = await NextRfqNumberAsync(businessUnitId, ct),
                    BuyersName = frozenHeader!.BuyersName,
                    RecDate = frozenHeader.RecDate,
                    BidClosingDate = frozenHeader.BidClosingDate,
                    AcknowledgmentDate = frozenHeader.AcknowledgmentDate,
                    SubDate = frozenHeader.SubmissionDate,
                    HeaderRemarks = frozenHeader.HeaderRemarks,
                    OpportunityNo = frozenHeader.OpportunityNo,
                    Rfqtype = frozenHeader.RfqType,
                    DurationAgreement = frozenHeader.DurationAgreement,
                    CustomerRfqReference = frozenHeader.CustomerRfqReference,
                    RequiredDeliveryDate = frozenHeader.RequiredDeliveryDate,
                    DeliveryLocation = frozenHeader.DeliveryLocation,
                    AgreementReference = frozenHeader.AgreementReference,
                    BidClosingDateHijri = frozenHeader.BidClosingDateHijri,
                    InquiryType = frozenHeader.InquiryType,
                    LeadId = lead.Id,
                    CustomerId = frozenHeader.CustomerId,
                    BusinessUnitId = businessUnitId,
                    RfqstatusId = await LifecycleStatusCatalog.ResolveIdAsync(_db, businessUnitId, "Rfq", "DRAFT", ct),
                    CreatedBy = command.Actor.Trim(),
                    CreatedDate = now,
                    PromotionId = receiptEntity.Id,
                    SourceLeadRevisionId = command.ExpectedLeadRevisionId,
                    ParticipationDecisionId = decision.Id
                };
                // The domain mutator below retains all case/customer invariants. A v2 revision
                // reaches it only after the mutable Lead identity exactly matches the frozen
                // values above; promotion therefore cannot silently switch customers or cases.
                rfq.InheritCommercialIdentity(lead);

                var currentLinesById = lead.LeadItems.ToDictionary(x => x.Id);
                var revisionById = revisionLines.ToDictionary(x => x.Id);
                foreach (var approved in decision.Lines.Where(x => x.Choice == LeadLineParticipationChoice.Bid)
                             .OrderBy(x => revisionById[x.LeadItemRevisionId].LineNumber))
                {
                    var revisionLine = revisionById[approved.LeadItemRevisionId];
                    var source = revisionLine.LeadItemId.HasValue
                        && currentLinesById.TryGetValue(revisionLine.LeadItemId.Value, out var linkedLeadItem)
                        ? linkedLeadItem
                        : throw new InvalidOperationException(
                            $"Current revision line {revisionLine.LineNumber} has no exact immutable canonical Lead-item lineage. Reconcile the source before promotion.");
                    var hasFrozenLine = LeadRevisionLineCommercialSnapshot.TryParse(
                        revisionLine.SnapshotJson, out var frozenLine);
                    if (!hasFrozenLine)
                        throw new InvalidOperationException(
                            $"Immutable revision line {revisionLine.LineNumber} has an incomplete commercial snapshot. Append a governed Lead revision before promotion.");

                    // Human participation corrections may override only the three explicitly
                    // governed quote-critical fields. Every other formal value comes from the
                    // immutable revision snapshot, never from the mutable current projection.
                    var quantity = approved.Quantity ?? frozenLine!.Quantity;
                    var uom = Clean(approved.UnitOfMeasure)
                        ?? Clean(frozenLine.UnitOfMeasure);
                    var currency = Clean(approved.Currency)?.ToUpperInvariant()
                        ?? Clean(frozenLine.Currency)?.ToUpperInvariant();
                    var part = Clean(frozenLine.ManufacturerPartNumber) ?? Clean(frozenLine.ItemMaterialCode);
                    var description = Clean(frozenLine.ProductShortDescription) ?? Clean(frozenLine.ProductShortName) ?? Clean(frozenLine.ItemText);
                    if (quantity is null or <= 0)
                        throw new InvalidOperationException($"Approved line {revisionLine.LineNumber} has no positive quantity.");
                    if (string.IsNullOrWhiteSpace(uom))
                        throw new InvalidOperationException($"Approved line {revisionLine.LineNumber} has no unit of measure.");
                    if (string.IsNullOrWhiteSpace(currency))
                        throw new InvalidOperationException($"Approved line {revisionLine.LineNumber} has no currency.");
                    if (string.IsNullOrWhiteSpace(part) && string.IsNullOrWhiteSpace(description))
                        throw new InvalidOperationException($"Approved line {revisionLine.LineNumber} has no part number or description.");

                    var item = new Rfqitem
                    {
                        CompanyRef = frozenLine.CompanyRef,
                        CustomerAccountPortalId = frozenLine.CustomerAccountPortalId,
                        CustomerRfqno = frozenLine.CustomerRfqno,
                        ItemMaterialCode = frozenLine.ItemMaterialCode ?? part,
                        LineItemNo = frozenLine.LineItemNo ?? revisionLine.LineNumber.ToString(),
                        ProductId = approved.ProductId,
                        CommodityProduct = frozenLine.CommodityProduct,
                        ProductShortName = frozenLine.ProductShortName ?? description,
                        ProductShortDescription = frozenLine.ProductShortDescription ?? description,
                        Alternative = frozenLine.Alternative,
                        BuyerName = frozenLine.BuyerName,
                        Currency = currency,
                        CurrencyId = approved.CurrencyId,
                        UnitOfMeasure = uom,
                        UomId = approved.UomId,
                        UnitPrice = frozenLine.UnitPrice,
                        Quantity = quantity,
                        StorageLocation = frozenLine.StorageLocation,
                        ManufacturerName = frozenLine.ManufacturerName,
                        ManufacturerPartNumber = frozenLine.ManufacturerPartNumber ?? part,
                        AlternateProductName = frozenLine.AlternateProductName,
                        AlternatePartNumber = frozenLine.AlternatePartNumber,
                        ItemText = frozenLine.ItemText,
                        MaterialPotext = frozenLine.MaterialPoText,
                        LeadTime = frozenLine.LeadTime,
                        ReceivedDate = frozenLine.ReceivedDate,
                        BidClosingDateLine = frozenLine.BidClosingDateLine,
                        RequiredDesiredDate = frozenHeader.RequiredDeliveryDate,
                        Aiconfidence = frozenLine.AiConfidence,
                        ExtraFields = frozenLine.ExtraFields,
                        CreatedBy = command.Actor.Trim(),
                        CreatedDate = now,
                        SourceBusinessUnitId = businessUnitId,
                        SourceLeadId = leadId,
                        SourceLeadRevisionId = command.ExpectedLeadRevisionId,
                        SourceLeadItemRevisionId = revisionLine.Id
                    };
                    // Compatibility projection for QuoteService: participation was decided on
                    // the Lead; every RFQ line is therefore already an explicit Quote line.
                    item.DecideParticipation(Rfqitem.ParticipationQuote, null, command.Actor, now);
                    rfq.Rfqitems.Add(item);
                }
                rfq.NoOfLineItems = rfq.Rfqitems.Count;
                _db.Rfqs.Add(rfq);
                await _db.SaveChangesAsync(ct);

                var lifecycle = new LifecycleApplicationService(_db);
                var actor = new LifecycleActor(command.Actor.Trim(), "AuthenticatedUser");
                await lifecycle.CompleteRfqPromotionInCurrentTransactionAsync(
                    businessUnitId, leadId, rfq.Id, receiptEntity.Id, command.ExpectedLeadRevisionId, decision.Id, actor,
                    new LifecycleTransitionCommand("CONVERTED_TO_RFQ", lead.LifecycleVersion, null, decision.Notes,
                        "Api", $"promotion-{receiptEntity.Id}", $"rfq-{rfq.Id}",
                        $"rfq-promotion:{businessUnitId}:{receiptEntity.Id}"), ct);
                await tx.CommitAsync(ct);
                return Result(receiptEntity, rfq, command.ExpectedDecisionVersion, decision.Sequence, replayed: false);
            });
        }
        catch (DbUpdateException ex) when (LeadConversionGate.IsDuplicateKey(ex))
        {
            _db.ChangeTracker.Clear();
            replay = await ReplayAsync(businessUnitId, command.IdempotencyKey, requestHash, ct);
            if (replay is not null) return replay;
            replay = await ReplayLeadWinnerAsync(businessUnitId, leadId, command, ct);
            if (replay is not null) return replay;
            var winner = await _db.Rfqs.AsNoTracking()
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.LeadId == leadId, ct);
            if (winner is null) throw;
            throw new InvalidOperationException($"Lead {leadId} was already promoted to RFQ #{winner.Id} ({winner.Rfqno}).");
        }
    }

    private async Task<RfqPromotionResult?> ReplayAsync(long businessUnitId, string key, string requestHash, CancellationToken ct)
    {
        var receipt = await _db.Set<RfqPromotion>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == key, ct);
        if (receipt is null) return null;
        if (!string.Equals(receipt.RequestHash, requestHash, StringComparison.Ordinal))
            throw new InvalidOperationException("The idempotency key was already used for a different RFQ promotion request.");
        var rfq = await _db.Rfqs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.PromotionId == receipt.Id, ct);
        if (rfq is null)
            throw new InvalidOperationException(
                "The RFQ promotion receipt exists but its immutable RFQ is missing. Recovery must restore the original RFQ; replay will not create a replacement.");
        var decision = await _db.Set<LeadParticipationDecision>().AsNoTracking()
            .SingleAsync(x => x.BusinessUnitId == businessUnitId && x.Id == receipt.ParticipationDecisionId, ct);
        var revisionNumber = await _db.Set<LeadRevision>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.Id == receipt.LeadRevisionId)
            .Select(x => x.RevisionNumber).SingleAsync(ct);
        return Result(receipt, rfq, revisionNumber, decision.Sequence, replayed: true);
    }

    /// <summary>
    /// Recovers the one durable Lead-origin RFQ when a transport retry arrives with a fresh
    /// idempotency key. Promotion changes the Lead lifecycle, so this lookup must happen before
    /// current-state eligibility checks. The immutable revision and participation snapshot still
    /// have to match exactly; a different commercial request never aliases to the winner.
    /// </summary>
    private async Task<RfqPromotionResult?> ReplayLeadWinnerAsync(
        long businessUnitId, long leadId, PromoteLeadToRfqCommand command, CancellationToken ct)
    {
        var receipt = await _db.Set<RfqPromotion>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.LeadId == leadId, ct);
        if (receipt is null) return null;

        var decision = await _db.Set<LeadParticipationDecision>().AsNoTracking()
            .SingleAsync(x => x.BusinessUnitId == businessUnitId && x.Id == receipt.ParticipationDecisionId, ct);
        var revisionNumber = await _db.Set<LeadRevision>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.Id == receipt.LeadRevisionId && x.LeadId == leadId)
            .Select(x => x.RevisionNumber).SingleAsync(ct);
        if (receipt.LeadRevisionId != command.ExpectedLeadRevisionId
            || revisionNumber != command.ExpectedDecisionVersion
            || decision.Sequence != command.ExpectedParticipationVersion
            || (command.ParticipationDecisionId.HasValue
                && receipt.ParticipationDecisionId != command.ParticipationDecisionId.Value))
            throw new InvalidOperationException(
                "This Lead was already promoted from a different immutable revision or participation decision.");

        var rfq = await _db.Rfqs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.PromotionId == receipt.Id, ct);
        if (rfq is null)
            throw new InvalidOperationException(
                "The RFQ promotion receipt exists but its immutable RFQ is missing. Recovery must restore the original RFQ; replay will not create a replacement.");
        return Result(receipt, rfq, revisionNumber, decision.Sequence, replayed: true);
    }

    private async Task<string> NextRfqNumberAsync(long businessUnitId, CancellationToken ct)
    {
        long sequence;
        if (_db.Database.IsNpgsql())
            sequence = await _db.Database.SqlQueryRaw<long>(
                "SELECT nextval('public.nexora_rfq_number_seq') AS \"Value\"").SingleAsync(ct);
        else
        {
            var numbers = await _db.Rfqs.IgnoreQueryFilters().AsNoTracking().Select(x => x.Rfqno).ToListAsync(ct);
            sequence = numbers.Select(x => System.Text.RegularExpressions.Regex.Match(x ?? "", "([0-9]+)$"))
                .Where(x => x.Success && long.TryParse(x.Groups[1].Value, out _))
                .Select(x => long.Parse(x.Groups[1].Value)).DefaultIfEmpty().Max() + 1;
        }
        return $"NXR-RFQ-{businessUnitId}-{DateTime.UtcNow:yyyy}-{sequence:D8}";
    }

    private static RfqPromotionResult Result(RfqPromotion p, Rfq r, int revisionNumber, int participationVersion, bool replayed) =>
        new(p.Id, r.Id, r.Rfqno, p.LeadId, p.LeadRevisionId, p.ParticipationDecisionId,
            revisionNumber, participationVersion, r.NoOfLineItems ?? r.Rfqitems.Count,
            p.PromotedAtUtc, p.PromotedBy, replayed);
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void EnsureFrozenCommercialIdentityMatches(
        Lead lead, LeadRevision revision, LeadRevisionCommercialSnapshot snapshot)
    {
        if (snapshot.CommercialCaseId <= 0 || string.IsNullOrWhiteSpace(snapshot.CommercialCaseReference))
            throw new InvalidOperationException(
                "The immutable Lead revision has no frozen commercial-case identity. Append a governed Lead revision before promotion.");
        if (snapshot.CommercialCaseId != lead.CommercialCaseId
            || !string.Equals(snapshot.CommercialCaseReference, lead.CommercialCaseReference, StringComparison.Ordinal)
            || snapshot.CustomerId != lead.CustomerId
            || snapshot.ContactId != lead.ContactId
            || snapshot.CustomerId != revision.CustomerIdSnapshot
            || snapshot.ContactId != revision.ContactIdSnapshot)
            throw new InvalidOperationException(
                "The Lead commercial identity changed after the immutable revision was established. Append a governed revision and recommit participation before promotion.");
    }

    /// <summary>
    /// Revisions written before schema v2 remain readable and promotable only when every value
    /// they actually froze still matches the current projection. Missing legacy fields are never
    /// claimed to be frozen; all new revisions use the complete snapshot above.
    /// </summary>
    private static void EnsureLegacyHeaderStillMatches(Lead lead, LeadRevision revision)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(revision.SnapshotJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                "The immutable Lead revision snapshot is unreadable. Append a governed Lead revision before promotion.");
        }
        if (root.ValueKind != JsonValueKind.Object
            || (!root.TryGetProperty("rfq", out _) && !root.TryGetProperty("buyer", out _)
                && !root.TryGetProperty("closing", out _)))
            throw new InvalidOperationException(
                "The immutable Lead revision predates verifiable commercial snapshots. Append a governed Lead revision before promotion.");
        if (!LegacyStringEquals(root, "rfq", NormalizeLegacy(lead.Rfqno))
            || !LegacyStringEquals(root, "buyer", NormalizeLegacy(lead.BuyersName))
            || !LegacyStringEquals(root, "closing", lead.BidClosingDate?.ToUniversalTime().ToString("O")))
            throw new InvalidOperationException(
                "The Lead header changed after its immutable legacy revision. Append a governed revision and recommit participation before promotion.");
    }

    private static void EnsureLegacyLineStillMatches(LeadItemRevision revisionLine, LeadItem source)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(revisionLine.SnapshotJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                $"Immutable revision line {revisionLine.LineNumber} is unreadable. Append a governed Lead revision before promotion.");
        }
        if (root.ValueKind != JsonValueKind.Object
            || (!root.TryGetProperty("line", out _) && !root.TryGetProperty("part", out _)))
            throw new InvalidOperationException(
                $"Immutable revision line {revisionLine.LineNumber} predates verifiable commercial snapshots. Append a governed Lead revision before promotion.");

        var legacyUom = NormalizeLegacy(UomCanonicalizer.CanonicalizeForStorage(source.UnitOfMeasure));
        var legacyDate = source.BidClosingDateLine?.ToUniversalTime().ToString("O");
        var quantityMatches = !root.TryGetProperty("Quantity", out var quantity)
            || quantity.ValueKind == JsonValueKind.Null && source.Quantity is null
            || quantity.ValueKind == JsonValueKind.Number && source.Quantity == quantity.GetInt32();
        if (!LegacyStringEquals(root, "line", NormalizeLegacy(source.LineItemNo))
            || !LegacyStringEquals(root, "part", NormalizeLegacy(source.ManufacturerPartNumber ?? source.ItemMaterialCode))
            || !LegacyStringEquals(root, "description", NormalizeLegacy(source.ProductShortDescription ?? source.ItemText))
            || !LegacyStringEquals(root, "uom", legacyUom)
            || !LegacyStringEquals(root, "date", legacyDate)
            || !quantityMatches)
            throw new InvalidOperationException(
                $"Lead line {revisionLine.LineNumber} changed after its immutable legacy revision. Append a governed revision and recommit participation before promotion.");
    }

    private static bool LegacyStringEquals(JsonElement root, string property, string? expected)
    {
        if (!root.TryGetProperty(property, out var value)) return true;
        var actual = value.ValueKind == JsonValueKind.Null ? null
            : value.ValueKind == JsonValueKind.String ? value.GetString()
            : value.GetRawText();
        return string.Equals(actual, expected, StringComparison.Ordinal);
    }

    private static string? NormalizeLegacy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new string(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return normalized.Length == 0 ? null : normalized;
    }

    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();

}
